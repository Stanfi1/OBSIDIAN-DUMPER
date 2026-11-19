using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using dnlib.DotNet;
using Obsidian.Dumper.Core.Diagnostics;
using Obsidian.Dumper.Core.Model;
using Obsidian.Dumper.Shared;

namespace Obsidian.Dumper.Core.Dumping;

public sealed record DumpProgress(int Completed, int Total, string Current, string? OutputPath);

/// <summary>
/// The out-of-process dump engine.
///
/// It reads the loader-mapped image of a module straight out of the target
/// (ReadProcessMemory only), rebuilds a structurally valid PE file in memory and
/// writes it to disk. No code runs inside the target and the target is never
/// suspended, so the operation is safe on live services and on malware.
/// </summary>
public sealed class ModuleDumper
{
    private readonly TargetSession _session;
    private readonly ILogSink _log;

    public ModuleDumper(TargetSession session, ILogSink? log = null)
    {
        _session = session;
        _log = log ?? NullLogSink.Instance;
    }

    public async Task<IReadOnlyList<DumpResult>> DumpAsync(
        IEnumerable<ModuleRecord> modules,
        DumpOptions options,
        IProgress<DumpProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var targets = modules.ToList();
        var results = new List<DumpResult>(targets.Count);

        Directory.CreateDirectory(options.OutputDirectory);

        int completed = 0;
        using var gate = new SemaphoreSlim(Math.Max(1, options.MaxDegreeOfParallelism));
        var tasks = new List<Task>(targets.Count);

        foreach (ModuleRecord module in targets)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            tasks.Add(Task.Run(() =>
            {
                DumpResult result;
                try
                {
                    result = DumpOne(module, options, cancellationToken);
                }
                catch (Exception ex)
                {
                    result = Fail(module, ex.Message, 0);
                }
                finally
                {
                    gate.Release();
                }

                lock (results)
                {
                    results.Add(result);
                }

                int done = Interlocked.Increment(ref completed);
                progress?.Report(new DumpProgress(done, targets.Count, module.Name, result.OutputPath));
            }, cancellationToken));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    public DumpResult DumpOne(ModuleRecord module, DumpOptions options, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();

        if (module.BaseAddress == 0)
            return Fail(module, "The module has no base address (not mapped).", stopwatch.ElapsedMilliseconds);

        if (!module.IsManaged && !options.DumpNativeModules)
        {
            // Not an error: the operator chose not to dump native modules.
            module.Status = DumpStatus.Skipped;
            module.Note = "Native module skipped (enable native dumps to include it).";
            return new DumpResult
            {
                Module = module,
                Success = false,
                Mode = options.RawImage ? DumpMode.Raw : DumpMode.Memory,
                Error = "Native module skipped: native dumping is disabled.",
                DurationMs = (int)stopwatch.ElapsedMilliseconds,
            };
        }

        module.Status = DumpStatus.Dumping;
        module.Note = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte[] header = _session.Target.ReadBytes(module.BaseAddress, 0x1000, out _);
            if (!PeImageLayout.TryReadHeaders(header, out PeHeaders headers))
            {
                return Fail(module, "PE headers are unreadable or invalid at the module base.", stopwatch.ElapsedMilliseconds);
            }

            long imageSize = headers.SizeOfImage;
            if (imageSize <= 0 || imageSize > 512L * 1024 * 1024)
                imageSize = module.Size;
            if (imageSize < 0x1000 || imageSize > 512L * 1024 * 1024)
                return Fail(module, "The image size reported by the headers is not plausible.", stopwatch.ElapsedMilliseconds);

            cancellationToken.ThrowIfCancellationRequested();

            byte[] mapped = _session.Target.ReadBytes(module.BaseAddress, (int)imageSize, out int unreadablePages);
            if (unreadablePages > 0)
                warnings.Add($"{unreadablePages} page(s) were unreadable and are zero-filled.");

            cancellationToken.ThrowIfCancellationRequested();

            byte[] file;
            DumpMode mode = options.RawImage || !options.RebuildPe ? DumpMode.Raw : DumpMode.Memory;

            if (mode == DumpMode.Raw)
            {
                file = mapped;
                warnings.Add(options.RawImage
                    ? "Raw mode: the file still carries mapped RVAs as raw offsets."
                    : "PE rebuild is disabled: the output file carries the mapped image layout.");
            }
            else
            {
                byte[]? rebuilt = PeImageLayout.Rebuild(mapped, options.ToRebuildOptions(), out string? rebuildError);
                if (rebuilt == null)
                {
                    string signature = mapped.Length >= 4
                        ? $"{mapped[0]:X2}{mapped[1]:X2}{mapped[2]:X2}{mapped[3]:X2}"
                        : "empty";
                    return Fail(module,
                        $"The mapped image could not be rebuilt into a PE file ({rebuildError}; size=0x{imageSize:X}, buffer=0x{mapped.Length:X}, pages={unreadablePages}, sig={signature}, base=0x{module.BaseAddress:X}, reported=0x{module.Size:X}).",
                        stopwatch.ElapsedMilliseconds);
                }
                file = rebuilt;
            }

            if (options.InvalidateStrongName && InvalidateStrongName(file))
                warnings.Add("Strong-name signature invalidated (the signature cannot be valid after a memory dump).");

            string verification = string.Empty;
            bool verified = false;
            if (options.VerifyWithMetadata && module.IsManaged)
            {
                (verified, verification) = VerifyMetadata(file);
                if (!verified)
                    warnings.Add("The dumped image does not expose readable .NET metadata — likely packed or metadata-encrypted.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!WriteOutputFile(module, file, options, out string path, out string? outputError))
                return Fail(module, outputError ?? "The dump file could not be written.", stopwatch.ElapsedMilliseconds);

            if (!verified && module.IsManaged && options.RepairMetadata && mode == DumpMode.Memory)
            {
                MetadataRepairResult repair = MetadataRepairService.TryRepair(
                    _session.Id, module.BaseAddress, path, module.Name);
                if (repair.Success && repair.Changed)
                {
                    (verified, verification) = VerifyMetadata(File.ReadAllBytes(path));
                    if (verified)
                    {
                        file = File.ReadAllBytes(path);
                        warnings.Add("Metadata repaired from the live CLR: " + repair.Message);
                        _log.Write(LogLevel.Info, $"{module.Name}: {repair.Message}", "dump");
                    }
                }
                else if (!repair.Success)
                {
                    warnings.Add("Metadata repair was not possible: " + repair.Message);
                }
            }

            string hash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();

            module.Status = DumpStatus.Dumped;
            module.DumpedPath = path;
            module.Note = verified ? verification : warnings.FirstOrDefault(w => w.Contains("metadata")) ?? "dumped";

            _log.Write(LogLevel.Success,
                $"{module.Name} → {Path.GetFileName(path)} ({FormatSize(file.Length)}, {stopwatch.ElapsedMilliseconds} ms){(verified ? ", " + verification : string.Empty)}",
                "dump");

            return new DumpResult
            {
                Module = module,
                Success = true,
                Mode = mode,
                OutputPath = path,
                Bytes = file.Length,
                Sha256 = hash,
                Verified = verified,
                Verification = verification,
                Warnings = warnings,
                DurationMs = (int)stopwatch.ElapsedMilliseconds,
            };
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a caller decision, not a dump failure: surface it to
            // DumpAsync so the UI can report "cancelled" instead of an error.
            module.Status = DumpStatus.Idle;
            throw;
        }
        catch (Exception ex)
        {
            return Fail(module, ex.Message, stopwatch.ElapsedMilliseconds);
        }
    }

    private DumpResult Fail(ModuleRecord module, string error, long elapsed)
    {
        module.Status = DumpStatus.Failed;
        module.Note = error;
        _log.Write(LogLevel.Error, $"{module.Name}: {error}", "dump");
        return new DumpResult
        {
            Module = module,
            Success = false,
            Error = error,
            DurationMs = (int)elapsed,
        };
    }

    private static (bool Verified, string Summary) VerifyMetadata(byte[] file)
    {
        try
        {
            using var module = ModuleDefMD.Load(file);
            int types = 0;
            int methods = 0;
            foreach (TypeDef type in module.GetTypes())
            {
                // `<Module>` is the global container type every module has; it is
                // not a user type, and the inspector excludes it too, so both
                // places must report the same count.
                if (type.IsGlobalModuleType)
                    continue;

                types++;
                methods += type.Methods.Count;
            }

            string name = module.Assembly?.Name ?? module.Name?.String ?? "?";
            string version = module.Assembly?.Version?.ToString() ?? "?";
            return (true, $"ok · {types} types · {methods} methods · {name} {version}");
        }
        catch (Exception ex)
        {
            return (false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static bool InvalidateStrongName(byte[] file)
    {
        // The rebuilt image is a *file*: section content lives at PointerToRawData,
        // so the CLI header has to be located through the section table.
        if (!PeImageLayout.TryGetCliHeader(file, mapped: false, out CliHeader cli))
            return false;
        if ((PeImageLayout.ReadU32(file, cli.Offset + 16) & 0x8) == 0 && cli.StrongNameSignatureRva == 0)
            return false;

        uint flags = PeImageLayout.ReadU32(file, cli.Offset + 16) & ~0x8u;
        PeImageLayout.WriteU32(file, cli.Offset + 16, flags);
        PeImageLayout.WriteU32(file, cli.Offset + 32, 0);
        PeImageLayout.WriteU32(file, cli.Offset + 36, 0);
        return true;
    }

    /// <summary>
    /// Resolves the output path and writes the file. <see cref="DumpOverwritePolicy.Rename"/>
    /// uses <see cref="FileMode.CreateNew"/>, so two parallel dumps of same-named
    /// modules can never silently overwrite each other.
    /// </summary>
    private bool WriteOutputFile(ModuleRecord module, byte[] file, DumpOptions options, out string path, out string? error)
    {
        path = string.Empty;
        error = null;

        string candidate = BuildCandidatePath(module, file, options);
        string? directory = Path.GetDirectoryName(candidate);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        switch (options.OverwritePolicy)
        {
            case DumpOverwritePolicy.Overwrite:
                try
                {
                    File.WriteAllBytes(candidate, file);
                    path = candidate;
                    return true;
                }
                catch (Exception ex)
                {
                    error = $"Could not write '{candidate}': {ex.Message}";
                    return false;
                }

            case DumpOverwritePolicy.Fail:
                try
                {
                    using var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    stream.Write(file, 0, file.Length);
                    path = candidate;
                    return true;
                }
                catch (IOException) when (File.Exists(candidate))
                {
                    error = $"The output file already exists: {candidate}";
                    return false;
                }
                catch (Exception ex)
                {
                    error = $"Could not write '{candidate}': {ex.Message}";
                    return false;
                }

            default: // DumpOverwritePolicy.Rename
                for (int index = 1; index <= 10_000; index++)
                {
                    string candidatePath = index == 1 ? candidate : AddSuffix(candidate, index);
                    try
                    {
                        using var stream = new FileStream(candidatePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        stream.Write(file, 0, file.Length);
                        path = candidatePath;
                        return true;
                    }
                    catch (IOException) when (File.Exists(candidatePath))
                    {
                        // Name taken by an earlier dump or another thread: try the next suffix.
                    }
                    catch (Exception ex)
                    {
                        error = $"Could not write '{candidatePath}': {ex.Message}";
                        return false;
                    }
                }

                error = $"Could not find a free output file name for '{Path.GetFileName(candidate)}'.";
                return false;
        }
    }

    private static string AddSuffix(string path, int index)
    {
        string extension = Path.GetExtension(path);
        return path[..^extension.Length] + "_" + index + extension;
    }

    private string BuildCandidatePath(ModuleRecord module, byte[] file, DumpOptions options)
    {
        string extension = ResolveExtension(module.Name, file);
        string name = ExpandFileNameTemplate(options.FileNameTemplate, module.Name,
            _session.Id, module.BaseAddress, extension);
        return Path.Combine(options.OutputDirectory, name);
    }

    /// <summary>
    /// Expands the configured <see cref="DumpOptions.FileNameTemplate"/> into a
    /// file name (no directory) for one module. Shared by the dump engine and the
    /// workspace overwrite prediction so the predicted path always matches the
    /// file the dumper will really write.
    /// </summary>
    public static string ExpandFileNameTemplate(string? template, string moduleName, int pid,
        ulong baseAddress, string extension)
    {
        string stem = string.IsNullOrWhiteSpace(moduleName) ? "module" : moduleName;
        string bare = Path.GetFileNameWithoutExtension(stem);

        string pattern = string.IsNullOrWhiteSpace(template) ? "{name}" : template;
        string name = pattern
            .Replace("{name}", bare, StringComparison.OrdinalIgnoreCase)
            .Replace("{pid}", pid.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{base}", $"0x{baseAddress:X}", StringComparison.OrdinalIgnoreCase)
            .Replace("{ext}", extension.TrimStart('.'), StringComparison.OrdinalIgnoreCase)
            .Replace("{module}", stem, StringComparison.OrdinalIgnoreCase);

        name = Sanitize(name);
        if (string.IsNullOrWhiteSpace(name))
            name = "module";

        if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            name += extension;

        return name;
    }

    /// <summary>
    /// Best-effort list of output paths the dumper may write for
    /// <paramref name="module"/> before its bytes are available. A module name
    /// with a <c>.dll</c>/<c>.exe</c> extension determines the extension; without
    /// one both candidates are returned because the real choice needs the PE
    /// headers. Used for the pre-dump overwrite confirmation.
    /// </summary>
    public static IReadOnlyList<string> PredictOutputPaths(ModuleRecord module, DumpOptions options, int pid)
    {
        string stem = string.IsNullOrWhiteSpace(module.Name) ? "module" : module.Name;
        string known = Path.GetExtension(stem);
        if (known.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
            known.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                Path.Combine(options.OutputDirectory,
                    ExpandFileNameTemplate(options.FileNameTemplate, module.Name, pid,
                        module.BaseAddress, known.ToLowerInvariant())),
            ];
        }

        return
        [
            Path.Combine(options.OutputDirectory,
                ExpandFileNameTemplate(options.FileNameTemplate, module.Name, pid, module.BaseAddress, ".dll")),
            Path.Combine(options.OutputDirectory,
                ExpandFileNameTemplate(options.FileNameTemplate, module.Name, pid, module.BaseAddress, ".exe")),
        ];
    }

    private static string ResolveExtension(string moduleName, byte[] file)
    {
        string candidate = Path.GetExtension(moduleName);
        if (candidate.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return candidate.ToLowerInvariant();
        }

        if (PeImageLayout.TryReadHeaders(file, out PeHeaders headers) && (headers.Characteristics & 0x2000) == 0)
            return ".exe";
        return ".dll";
    }

    private static string Sanitize(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            bool invalid = c == '\\' || c == '/' || c == ':' || c == '*' || c == '?' || c == '"' ||
                           c == '<' || c == '>' || c == '|' || c < 32;
            builder.Append(invalid ? '_' : c);
        }
        return builder.ToString().Trim().TrimEnd('.');
    }

    internal static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }
}
