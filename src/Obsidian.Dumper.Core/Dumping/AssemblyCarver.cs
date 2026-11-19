using System.Security.Cryptography;
using System.Text;
using Obsidian.Dumper.Core.Memory;
using Obsidian.Dumper.Core.Model;
using Obsidian.Dumper.Shared;

namespace Obsidian.Dumper.Core.Dumping;

/// <summary>One assembly recovered by walking process memory.</summary>
public sealed class CarvedAssembly
{
    public string Name { get; init; } = string.Empty;
    public string? Path { get; init; }
    public ulong Address { get; init; }
    public long Size { get; init; }
    public string Method { get; init; } = "memory";
    public string? ModuleName { get; init; }
    public string? Sha256 { get; init; }
    public string? Error { get; init; }
    public bool Saved => !string.IsNullOrEmpty(Path);

    public string AddressLabel => "0x" + Address.ToString("X", System.Globalization.CultureInfo.InvariantCulture);
    public string SizeLabel => ProcessRecord.FormatBytes(Size);
    public string ModuleLabel => string.IsNullOrEmpty(ModuleName) ? "—" : ModuleName!;
    public string StatusLabel => Saved ? "saved" : "failed";
    public string DetailLabel => Path ?? Error ?? "—";
}

public sealed class CarveProgress
{
    public int Candidates { get; init; }
    public int Saved { get; init; }
    public long BytesScanned { get; init; }
    public string Current { get; init; } = string.Empty;
}

public sealed class CarveOptions
{
    public string OutputDirectory { get; set; } = string.Empty;
    public bool IncludeFrameworkAssemblies { get; set; }
    public bool OwningModuleOnly { get; set; }
    public int MaxCandidates { get; set; } = 4096;
    public long MaxImageBytes { get; set; } = 128L * 1024 * 1024;
    public long MaxScanBytes { get; set; } = 512L * 1024 * 1024;
}

public sealed class CarveResult
{
    public IReadOnlyList<CarvedAssembly> Assemblies { get; init; } = Array.Empty<CarvedAssembly>();
    public int Candidates { get; init; }
    public int SkippedFramework { get; init; }
    public int Duplicates { get; init; }
    public long BytesScanned { get; init; }
    public bool Truncated { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Page-walk assembly recovery, the same idea as ExtremeDumper's "dump all
/// valid .NET assemblies": scan committed readable memory for MZ images, try
/// both mapped and file layouts, validate the result with dnlib and save each
/// distinct managed assembly. This reaches manually mapped modules that never
/// appear in the loader's module list.
/// </summary>
public sealed class AssemblyCarver
{
    private const int HeaderBytes = 0x1000;

    public CarveResult Carve(
        RemoteProcess process,
        IReadOnlyList<ModuleRecord> modules,
        CarveOptions options,
        IProgress<CarveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.OutputDirectory))
            return new CarveResult { Error = "No output directory was supplied." };

        Directory.CreateDirectory(options.OutputDirectory);

        var assemblies = new List<CarvedAssembly>();
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int skippedFramework = 0;
        int duplicates = 0;
        int candidates = 0;
        int saved = 0;

        // Reuse the read-only scanner for candidate discovery. A hex MZ pattern
        // keeps the page walk on committed readable regions and gives us
        // progress/cancellation for free.
        var scanOptions = new MemoryScanOptions
        {
            Kind = MemoryScanKind.Hex,
            Pattern = "4D 5A",
            CaseInsensitive = false,
            MaxResults = Math.Max(64, options.MaxCandidates),
            MaxBytes = options.MaxScanBytes,
            ContextBytes = 0,
        };

        var scanProgress = new Progress<MemoryScanProgress>(value =>
        {
            progress?.Report(new CarveProgress
            {
                Candidates = value.Hits,
                Saved = Volatile.Read(ref saved),
                BytesScanned = value.BytesScanned,
                Current = value.CurrentModule ?? "scanning memory…",
            });
        });

        MemoryScanResult scan = MemoryScanner.Scan(process, modules, scanOptions, scanProgress, cancellationToken);

        foreach (MemoryScanHit hit in scan.Hits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidates++;
            if (candidates > options.MaxCandidates)
                break;

            if (options.OwningModuleOnly && string.IsNullOrEmpty(hit.ModuleName))
                continue;

            progress?.Report(new CarveProgress
            {
                Candidates = candidates,
                Saved = Volatile.Read(ref saved),
                BytesScanned = scan.BytesScanned,
                Current = "checking " + hit.AddressLabel,
            });

            CarvedAssembly? carved = TryCarveCandidate(process, hit, options);
            if (carved == null)
                continue;

            if (!options.IncludeFrameworkAssemblies && IsFrameworkAssembly(carved.Name))
            {
                skippedFramework++;
                continue;
            }

            if (carved.Sha256 != null && !hashes.Add(carved.Sha256))
            {
                duplicates++;
                continue;
            }

            assemblies.Add(carved);
            Interlocked.Increment(ref saved);
        }

        return new CarveResult
        {
            Assemblies = assemblies,
            Candidates = candidates,
            SkippedFramework = skippedFramework,
            Duplicates = duplicates,
            BytesScanned = scan.BytesScanned,
            Truncated = scan.Truncated,
            Error = scan.Error,
        };
    }

    private static CarvedAssembly? TryCarveCandidate(RemoteProcess process, MemoryScanHit hit, CarveOptions options)
    {
        byte[] header;
        try
        {
            header = process.ReadBytes(hit.Address, HeaderBytes, out _);
        }
        catch
        {
            return null;
        }

        if (!PeImageLayout.TryReadHeaders(header, out PeHeaders headers))
            return null;
        if (headers.ClrDirectoryRva == 0 && headers.ClrDirectorySize == 0)
            return null; // native image, not a managed assembly
        if (headers.SizeOfImage == 0 || headers.SizeOfImage > options.MaxImageBytes)
            return null;

        string? moduleName = hit.ModuleName;

        byte[]? result = TryMemoryLayout(process, hit.Address, headers, options)
            ?? TryFileLayout(process, hit.Address, headers, options);
        if (result == null)
            return null;

        if (!TryReadAssemblyName(result, out string name, out bool hasEntryPoint))
            return null;

        string hash = Convert.ToHexString(SHA256.HashData(result));
        string extension = hasEntryPoint ? ".exe" : ".dll";
        string fileName = SanitizeName(name) + extension;
        string path = NextFreePath(options.OutputDirectory, fileName);

        try
        {
            File.WriteAllBytes(path, result);
        }
        catch
        {
            return new CarvedAssembly
            {
                Name = name,
                Address = hit.Address,
                Size = result.Length,
                Method = "memory",
                ModuleName = moduleName,
                Sha256 = hash,
                Error = "The carved image could not be written.",
            };
        }

        return new CarvedAssembly
        {
            Name = name,
            Path = path,
            Address = hit.Address,
            Size = result.Length,
            Method = "memory",
            ModuleName = moduleName,
            Sha256 = hash,
        };
    }

    private static byte[]? TryMemoryLayout(RemoteProcess process, ulong address, PeHeaders headers, CarveOptions options)
    {
        try
        {
            byte[] mapped = process.ReadBytes(address, (int)headers.SizeOfImage, out _);
            if (mapped.Length < headers.SizeOfImage)
                return null;

            byte[]? file = PeImageLayout.Rebuild(mapped, new PeRebuildOptions());
            if (file == null)
                return null;

            return TryReadAssemblyName(file, out _, out _) ? file : null;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? TryFileLayout(RemoteProcess process, ulong address, PeHeaders headers, CarveOptions options)
    {
        long length = headers.SizeOfHeaders;
        foreach (PeSection section in headers.Sections)
        {
            if (section.RawSize == 0)
                continue;
            long end = (long)section.RawPointer + section.RawSize;
            if (end > length)
                length = end;
        }

        if (length <= 0 || length > options.MaxImageBytes)
            return null;

        try
        {
            byte[] file = process.ReadBytes(address, (int)length, out _);
            if (file.Length < length)
                return null;
            // Do not use PeImageLayout.IsManaged here: it probes the buffer as a
            // mapped image, while this path is a file-layout image. dnlib is the
            // authority for both layouts.
            return TryReadAssemblyName(file, out _, out _) ? file : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool TryReadAssemblyName(byte[] image, out string name, out bool hasEntryPoint)
    {
        name = string.Empty;
        hasEntryPoint = false;
        try
        {
            using var module = dnlib.DotNet.ModuleDefMD.Load(image, new dnlib.DotNet.ModuleContext());
            string? assemblyName = module.Assembly?.Name?.String;
            string? moduleName = module.Name?.String;
            name = string.IsNullOrWhiteSpace(assemblyName) ? moduleName ?? string.Empty : assemblyName;
            if (string.IsNullOrWhiteSpace(name))
                return false;
            hasEntryPoint = module.EntryPoint != null;
            // Touch metadata collections; a corrupt stream throws here.
            _ = module.Types.Count;
            _ = module.GetTypes().Take(1).ToList();
            return true;
        }
        catch
        {
            name = string.Empty;
            return false;
        }
    }

    private static string NextFreePath(string directory, string fileName)
    {
        string path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
            return path;

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int i = 2; i < 10_000; i++)
        {
            path = Path.Combine(directory, $"{stem}_{i}{extension}");
            if (!File.Exists(path))
                return path;
        }

        return Path.Combine(directory, $"{stem}_{Guid.NewGuid():N}{extension}");
    }

    private static string SanitizeName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (char c in name)
            builder.Append(Path.GetInvalidFileNameChars().Contains(c) || c is '\\' or '/' ? '_' : c);
        return builder.Length == 0 ? "assembly" : builder.ToString();
    }

    public static bool IsFrameworkAssembly(string name)
    {
        string[] prefixes =
        [
            "mscorlib", "netstandard", "System", "Microsoft", "WindowsBase",
            "PresentationCore", "PresentationFramework", "Accessibility",
            "UIAutomation", "WindowsFormsIntegration", "ReachFramework",
        ];

        foreach (string prefix in prefixes)
        {
            if (name.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
