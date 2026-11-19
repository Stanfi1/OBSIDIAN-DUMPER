using System.Reflection;
using System.Runtime.InteropServices;

namespace Obsidian.Services;

/// <summary>
/// Single source of truth for the product identity and the engine dependency
/// labels shown in the shell, the settings page and the activity log.
///
/// The version comes from the assembly informational version (which
/// <c>Directory.Build.props</c> controls), so bumping the build version is a
/// one-line change instead of a grep across hard-coded strings.
/// </summary>
public static class AppInfo
{
    private const string FallbackVersion = "1.4.1";

    public const string ProductName = "OBSIDIAN";
    public const string ProductTitle = "OBSIDIAN Dumper";

    /// <summary>Semantic version without build metadata, e.g. "1.1.0".</summary>
    public static string Version { get; } = ResolveVersion();

    /// <summary>Short label for the status bar, e.g. "v1.1.0".</summary>
    public static string VersionLabel => "v" + Version;

    /// <summary>Full label for the settings/about card, e.g. "OBSIDIAN Dumper 1.1.0".</summary>
    public static string VersionText => ProductTitle + " " + Version;

    /// <summary>Human friendly runtime label, e.g. ".NET 10".</summary>
    public static string RuntimeLabel { get; } = ResolveRuntimeLabel();

    /// <summary>dnlib major.minor, resolved from the loaded assembly.</summary>
    public static string DnlibVersion { get; } = ResolveLibraryVersion("dnlib", "4.4");

    /// <summary>ILSpy decompiler major.minor, resolved from the loaded assembly.</summary>
    public static string DecompilerVersion { get; } = ResolveLibraryVersion("ICSharpCode.Decompiler", "9.1");

    /// <summary>Compact dependency summary used by the settings page.</summary>
    public static string EngineSummary => $"dnlib {DnlibVersion} · ILSpy {DecompilerVersion} · {RuntimeLabel}";

    private static string ResolveVersion()
    {
        try
        {
            Assembly assembly = typeof(AppInfo).Assembly;
            string? informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            // Deterministic builds may append "+<source revision>"; that is not
            // part of the product version.
            if (!string.IsNullOrWhiteSpace(informational))
            {
                int metadata = informational.IndexOf('+');
                string version = (metadata >= 0 ? informational[..metadata] : informational).Trim();
                if (version.Length > 0)
                    return version;
            }

            return FormatProductVersion(assembly.GetName().Version) ?? FallbackVersion;
        }
        catch
        {
            return FallbackVersion;
        }
    }

    private static string ResolveRuntimeLabel()
    {
        try
        {
            string description = RuntimeInformation.FrameworkDescription;
            if (description.StartsWith(".NET Framework", StringComparison.OrdinalIgnoreCase))
            {
                // ".NET Framework 4.8.9261.0" -> ".NET Framework 4.8"
                string[] parts = description.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                System.Version? framework = parts.Length > 2 && System.Version.TryParse(parts[2], out System.Version? parsed)
                    ? parsed
                    : null;
                return framework is null
                    ? description
                    : $".NET Framework {framework.Major}.{framework.Minor}";
            }

            System.Version? runtime = Environment.Version;
            return runtime is null ? description : $".NET {runtime.Major}";
        }
        catch
        {
            return ".NET";
        }
    }

    private static string ResolveLibraryVersion(string simpleName, string fallback)
    {
        try
        {
            Assembly? assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase));

            // The engine assemblies may not have been touched yet at shell
            // startup; ask the loader so the About card is always accurate.
            assembly ??= Assembly.Load(new AssemblyName(simpleName));
            System.Version? version = assembly?.GetName().Version;
            return version is null ? fallback : $"{version.Major}.{Math.Max(0, version.Minor)}";
        }
        catch
        {
            return fallback;
        }
    }

    private static string? FormatProductVersion(System.Version? version)
    {
        if (version is null)
            return null;

        // 1.2.3.4 -> "1.2.3.4", 1.2.3.0 -> "1.2.3", 1.2.0.0 -> "1.2.0".
        if (version.Revision > 0)
            return version.ToString(4);
        if (version.Build >= 0)
            return version.ToString(3);
        return version.ToString(2);
    }
}
