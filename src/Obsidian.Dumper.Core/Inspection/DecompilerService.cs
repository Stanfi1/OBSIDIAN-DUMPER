using System.Runtime.InteropServices;
using dnlib.DotNet;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

namespace Obsidian.Dumper.Core.Inspection;

/// <summary>
/// Thin, cached wrapper around the ILSpy decompiler engine. Types are decompiled
/// on demand so a 4000-type assembly opens instantly and only pays for what the
/// operator actually looks at.
/// </summary>
public sealed class DecompilerService
{
    private static readonly Lazy<DecompilerService> LazyInstance = new(() => new DecompilerService());

    /// <summary>Cache entry, invalidated when the on-disk file changes (re-dumped assemblies).</summary>
    private sealed record DecompilerEntry(string Stamp, Lazy<CSharpDecompiler> Decompiler);

    private readonly Dictionary<string, DecompilerEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public static DecompilerService Shared => LazyInstance.Value;

    public string DecompileType(ModuleDefMD module, string? path, string fullName, string simpleName)
    {
        if (string.IsNullOrEmpty(path))
            return "// decompilation requires the assembly to exist on disk (in-memory module)";
        if (!File.Exists(path))
            return "// decompilation requires the assembly to exist on disk: file not found: " + path;

        try
        {
            CSharpDecompiler decompiler = GetDecompiler(path, DetectTargetFramework(module, path));
            FullTypeName name = BuildFullTypeName(module, fullName, simpleName);

            // CSharpDecompiler instances are not thread-safe (ILSpy documents
            // that transform instances cannot be shared), so serialise calls.
            lock (_gate)
            {
                return decompiler.DecompileTypeAsString(name);
            }
        }
        catch (Exception ex)
        {
            return "// decompilation failed: " + ex.GetType().Name + ": " + ex.Message;
        }
    }

    /// <summary>
    /// Drops every cached decompiler (called when dumps are replaced wholesale).
    /// Disposes the underlying metadata readers when the ILSpy version supports it.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            foreach (DecompilerEntry entry in _cache.Values)
                DisposeEntry(entry);
            _cache.Clear();
        }
    }

    private CSharpDecompiler GetDecompiler(string path, string targetFramework)
    {
        string stamp = FileStamp(path);
        lock (_gate)
        {
            if (_cache.TryGetValue(path, out DecompilerEntry? existing))
            {
                if (existing.Stamp == stamp)
                    return existing.Decompiler.Value;

                // The file was re-dumped since it was cached: retire the stale entry.
                _cache.Remove(path);
                DisposeEntry(existing);
            }

            var entry = new DecompilerEntry(
                stamp,
                new Lazy<CSharpDecompiler>(() => CreateDecompiler(path, targetFramework)));
            _cache[path] = entry;
            try
            {
                return entry.Decompiler.Value;
            }
            catch
            {
                _cache.Remove(path);
                throw;
            }
        }
    }

    private static string FileStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "0:0";
        }
        catch
        {
            return "0:0";
        }
    }

    private static void DisposeEntry(DecompilerEntry entry)
    {
        if (!entry.Decompiler.IsValueCreated)
            return;
        try
        {
            if (entry.Decompiler.Value is IDisposable disposable)
                disposable.Dispose();
        }
        catch
        {
            // best effort
        }
    }

    private static CSharpDecompiler CreateDecompiler(string path, string targetFramework)
    {
        var settings = new DecompilerSettings(LanguageVersion.CSharp12_0)
        {
            ThrowOnAssemblyResolveErrors = false,
            RemoveDeadCode = true,
            RemoveDeadStores = true,
            ShowXmlDocumentation = false,
            UseDebugSymbols = false,
            LoadInMemory = false,
            AnonymousMethods = true,
            AlwaysShowEnumMemberValues = false,
        };

        var resolver = new UniversalAssemblyResolver(path, false, targetFramework);
        AddResolverRoots(resolver, path);
        return new CSharpDecompiler(path, resolver, settings);
    }

    /// <summary>
    /// ILSpy's universal resolver already probes the main assembly folder and the
    /// framework directories it can derive from the target framework, but dumps
    /// are usually opened from an arbitrary output folder and reference siblings
    /// that live next to the dumped file. Seed the resolver with the folders and
    /// runtime directories that actually exist.
    /// </summary>
    private static void AddResolverRoots(UniversalAssemblyResolver resolver, string path)
    {
        foreach (string? directory in CandidateResolverDirectories(path))
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;
            try
            {
                if (Directory.Exists(directory))
                    resolver.AddSearchDirectory(directory);
            }
            catch
            {
                // The resolver logs its own errors; a bad search directory is not fatal.
            }
        }
    }

    private static IEnumerable<string?> CandidateResolverDirectories(string path)
    {
        yield return SafeDirectoryOf(path);
        yield return AppContext.BaseDirectory;

        string? runtimeDirectory = null;
        try
        {
            runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
        }
        catch
        {
            // Some hosts refuse to report the framework directory.
        }
        yield return runtimeDirectory;

        // Common machine-wide reference locations used when inspecting arbitrary
        // Program Files assemblies without their deployment folder.
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows))
        {
            yield return Path.Combine(windows, "Microsoft.NET", "Framework64", "v4.0.30319");
            yield return Path.Combine(windows, "Microsoft.NET", "Framework", "v4.0.30319");
        }

        string dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT") ??
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
        yield return Path.Combine(dotnetRoot, "shared", "Microsoft.NETCore.App");
        yield return Path.Combine(dotnetRoot, "shared", "Microsoft.WindowsDesktop.App");
    }

    private static string? SafeDirectoryOf(string path)
    {
        try
        {
            return Path.GetDirectoryName(path);
        }
        catch
        {
            return null;
        }
    }

    private static string DetectTargetFramework(ModuleDefMD module, string path)
    {
        try
        {
            CustomAttribute? attribute = module.Assembly?.CustomAttributes
                .FirstOrDefault(a => a.TypeFullName == "System.Runtime.Versioning.TargetFrameworkAttribute");
            string? framework = attribute?.ConstructorArguments.Count > 0
                ? attribute.ConstructorArguments[0].Value?.ToString()
                : null;
            if (!string.IsNullOrWhiteSpace(framework))
                return framework!;

            string? metadataVersion = module.Metadata?.VersionString;
            if (metadataVersion == "v2.0.50727")
                return ".NETFramework,Version=v3.5";
            if (metadataVersion == "v4.0.30319")
                return ".NETFramework,Version=v4.8";
            if (!string.IsNullOrEmpty(metadataVersion))
                return ".NETCoreApp,Version=v8.0";
        }
        catch
        {
            // Fall back to a neutral framework below.
        }

        // Attribute/metadata unreadable: sniff the framework from the folder name.
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                if (directory.Contains(@"\Microsoft.NET\Framework", StringComparison.OrdinalIgnoreCase))
                    return ".NETFramework,Version=v4.8";
                if (directory.Contains(@"\shared\Microsoft.NETCore.App\", StringComparison.OrdinalIgnoreCase) ||
                    directory.Contains(@"\shared\Microsoft.WindowsDesktop.App\", StringComparison.OrdinalIgnoreCase))
                    return ".NETCoreApp,Version=v8.0";
            }
        }
        catch
        {
            // ignore
        }

        return ".NETCoreApp,Version=v8.0";
    }

    private static FullTypeName BuildFullTypeName(ModuleDefMD module, string fullName, string simpleName)
    {
        var chain = new List<TypeDef>();
        TypeDef? match = module.GetTypes().FirstOrDefault(t => t.FullName == fullName);
        if (match != null)
        {
            for (TypeDef? current = match; current != null; current = current.DeclaringType as TypeDef)
                chain.Add(current);
            chain.Reverse();
        }

        if (chain.Count == 0)
            return new FullTypeName(fullName.Replace('/', '+'));

        TypeDef top = chain[0];
        string ns = top.Namespace?.String ?? string.Empty;
        string topName = StripArity(top.Name?.String ?? simpleName);
        var result = new FullTypeName(new TopLevelTypeName(ns, topName, top.GenericParameters.Count));

        for (int i = 1; i < chain.Count; i++)
        {
            TypeDef nested = chain[i];
            result = result.NestedType(StripArity(nested.Name?.String ?? "?"), nested.GenericParameters.Count);
        }

        return result;
    }

    private static string StripArity(string name)
    {
        int tick = name.LastIndexOf('`');
        return tick > 0 ? name[..tick] : name;
    }
}
