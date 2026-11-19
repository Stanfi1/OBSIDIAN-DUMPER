using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Obsidian.Common;
using Obsidian.Dumper.Core.Diagnostics;
using Obsidian.Dumper.Core.Injection;
using Obsidian.Dumper.Core.Model;

namespace Obsidian.Services;

/// <summary>
/// Persisted operator settings (schema v2 — see docs/SETTINGS-CONTRACT.md).
/// Property names are frozen: the shell, pages, injection surface and the
/// verification tooling all bind to them.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 2;

    // ------------------------------------------------------------ appearance

    /// <summary>Dark / light / follow Windows.</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Accent preset used for accent surfaces and gradients.</summary>
    public AccentPreset AccentColor { get; set; } = AccentPreset.Red;

    /// <summary>Comfortable (default) or compact paddings/row heights.</summary>
    public UiDensity Density { get; set; } = UiDensity.Comfortable;

    public bool Animations { get; set; } = true;

    /// <summary>Show the dnlib/ILSpy/runtime block in the sidebar.</summary>
    public bool ShowEngineVersions { get; set; } = true;

    // -------------------------------------------------------------- dumping

    public string OutputDirectory { get; set; } = DefaultOutputDirectory();
    public string FileNameTemplate { get; set; } = "{name}";
    public bool RebuildPe { get; set; } = true;
    public bool StripDebugDirectory { get; set; } = true;
    public bool StripBoundImports { get; set; } = true;
    public bool InvalidateStrongName { get; set; } = true;
    public bool VerifyMetadata { get; set; } = true;
    public bool RepairMetadata { get; set; } = true;
    public bool HideFrameworkModules { get; set; }
    public bool DumpNativeModules { get; set; }
    public bool RawImage { get; set; }
    public DumpOverwritePolicy OverwritePolicy { get; set; } = DumpOverwritePolicy.Rename;
    public bool ConfirmOverwrite { get; set; }
    public bool AutoOpenOutputFolder { get; set; }

    /// <summary>1..8 parallel module dump workers.</summary>
    public int ParallelDumpThreads { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <summary>Empty = let the deep dumper pick a writable location.</summary>
    public string DeepDumpWorkDirectory { get; set; } = string.Empty;

    // ------------------------------------------------------------ processes

    public bool ShowInaccessibleProcesses { get; set; } = true;
    public bool LazyCommandLine { get; set; } = true;

    /// <summary>0 = off, otherwise 2000/5000/10000/30000 ms.</summary>
    public int AutoRefreshIntervalMs { get; set; }

    public bool RememberFilters { get; set; } = true;
    public bool ConfirmDestructiveActions { get; set; } = true;
    public bool HighlightElevated { get; set; } = true;
    public bool ShowIntegrity { get; set; } = true;

    // ------------------------------------------------------------- injection

    public bool ConfirmInjection { get; set; } = true;
    public InjectionMethod DefaultInjectionMethod { get; set; } = InjectionMethod.CreateRemoteThread;
    public string InjectionDllDirectory { get; set; } = string.Empty;
    public bool AutoUnloadOnDetach { get; set; }

    // -------------------------------------------------------------- activity

    /// <summary>500..50000; also the LogBus ring capacity for the next session.</summary>
    public int MaxLogEntries { get; set; } = 2000;

    public bool LogShowTimestamps { get; set; } = true;
    public bool LogAutoScroll { get; set; } = true;
    public LogLevel LogMinLevel { get; set; } = LogLevel.Trace;

    // ----------------------------------------------------------- diagnostics

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>The file the settings were loaded from / will be saved to.</summary>
    [JsonIgnore]
    public string SettingsPath { get; set; } = string.Empty;

    /// <summary>
    /// Session state remembered when <see cref="RememberFilters"/> is enabled.
    /// Kept at the end of the file so the contract properties stay stable.
    /// </summary>
    public string? LastTargetsFilter { get; set; }
    public string? LastTargetsSearch { get; set; }
    public string? LastWorkspaceSearch { get; set; }
    public int LastSelectedPid { get; set; }

    /// <summary>
    /// Raised when <see cref="EffectiveOutputDirectory"/> had to fall back to a
    /// writable location because the configured one is unusable.
    /// </summary>
    public event Action<string>? OutputDirectoryFallback;

    public static string DefaultOutputDirectory()
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
            documents = Environment.CurrentDirectory;
        return Path.Combine(documents, "OBSIDIAN", "dumps");
    }

    /// <summary>
    /// Repairs values that would make a dump or the settings UI fail. Called
    /// after loading a settings file so a hand-edited, truncated or
    /// out-of-range document cannot put the application into a broken state.
    /// Unknown JSON members are ignored by System.Text.Json (forward compatible).
    /// </summary>
    public void Normalise()
    {
        // Migration: files without a schema version were written by 1.0/1.1;
        // all v2 members have safe property initialisers, so only the version
        // marker is missing. A version *newer* than ours is kept as-is.
        if (SchemaVersion < 1)
            SchemaVersion = 1;
        if (SchemaVersion < CurrentSchemaVersion)
            SchemaVersion = CurrentSchemaVersion;

        if (string.IsNullOrWhiteSpace(OutputDirectory))
            OutputDirectory = DefaultOutputDirectory();
        if (string.IsNullOrWhiteSpace(FileNameTemplate))
            FileNameTemplate = "{name}";

        if (!Enum.IsDefined(Theme))
            Theme = AppTheme.System;
        if (!Enum.IsDefined(AccentColor))
            AccentColor = AccentPreset.Violet;
        if (!Enum.IsDefined(Density))
            Density = UiDensity.Comfortable;
        if (!Enum.IsDefined(OverwritePolicy))
            OverwritePolicy = DumpOverwritePolicy.Rename;
        if (!Enum.IsDefined(DefaultInjectionMethod))
            DefaultInjectionMethod = InjectionMethod.CreateRemoteThread;
        if (!Enum.IsDefined(LogMinLevel))
            LogMinLevel = LogLevel.Trace;

        ParallelDumpThreads = Math.Clamp(ParallelDumpThreads, 1, 8);
        MaxLogEntries = Math.Clamp(MaxLogEntries, 500, 50000);

        if (AutoRefreshIntervalMs is not (0 or 2000 or 5000 or 10000 or 30000))
            AutoRefreshIntervalMs = 0;

        DeepDumpWorkDirectory = (DeepDumpWorkDirectory ?? string.Empty).Trim();
        InjectionDllDirectory = (InjectionDllDirectory ?? string.Empty).Trim();
        if (OutputDirectory != null)
            OutputDirectory = OutputDirectory.Trim();
    }

    /// <summary>Copies every persisted property from <paramref name="source"/>.</summary>
    public void CopyFrom(AppSettings source)
    {
        ArgumentNullException.ThrowIfNull(source);
        foreach (PropertyInfo property in PersistedProperties)
            property.SetValue(this, property.GetValue(source));
    }

    /// <summary>Restores every persisted property to its default value.</summary>
    public void ResetToDefaults()
    {
        string path = SettingsPath;
        CopyFrom(new AppSettings());
        SettingsPath = path;
    }

    private static readonly PropertyInfo[] PersistedProperties = typeof(AppSettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite && p.Name != nameof(SettingsPath))
        .ToArray();

    public DumpOptions ToDumpOptions() => new()
    {
        OutputDirectory = EffectiveOutputDirectory(),
        FileNameTemplate = string.IsNullOrWhiteSpace(FileNameTemplate) ? "{name}" : FileNameTemplate,
        WorkDirectory = string.IsNullOrWhiteSpace(DeepDumpWorkDirectory) ? null : DeepDumpWorkDirectory,
        RebuildPe = RebuildPe,
        StripDebugDirectory = StripDebugDirectory,
        StripBoundImports = StripBoundImports,
        InvalidateStrongName = InvalidateStrongName,
        VerifyWithMetadata = VerifyMetadata,
        RepairMetadata = RepairMetadata,
        IncludeFrameworkModules = !HideFrameworkModules,
        DumpNativeModules = DumpNativeModules,
        RawImage = RawImage,
        OverwritePolicy = OverwritePolicy,
        MaxDegreeOfParallelism = Math.Clamp(ParallelDumpThreads, 1, 8),
    };

    /// <summary>
    /// Returns a directory the process can actually write to. Some hardened
    /// machines refuse Documents/Temp for unsigned binaries, so there is a
    /// deterministic fallback next to the executable and in the current folder.
    /// </summary>
    public string EffectiveOutputDirectory()
    {
        string configured = OutputDirectory;
        string resolved = ResolveOutputDirectory(configured);
        if (!string.Equals(resolved, configured, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(resolved, Environment.CurrentDirectory, StringComparison.OrdinalIgnoreCase))
        {
            OutputDirectory = resolved;
            OutputDirectoryFallback?.Invoke(resolved);
        }

        return resolved;
    }

    /// <summary>
    /// Same resolution as <see cref="EffectiveOutputDirectory"/> but without
    /// mutating the persisted setting — safe to call from UI bindings.
    /// </summary>
    public string PeekEffectiveOutputDirectory() => ResolveOutputDirectory(OutputDirectory);

    private static string ResolveOutputDirectory(string configured)
    {
        foreach (string candidate in new[]
                 {
                     configured,
                     DefaultOutputDirectory(),
                     Path.Combine(AppContext.BaseDirectory, "dumps"),
                     Path.Combine(Environment.CurrentDirectory, "dumps"),
                 })
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            try
            {
                Directory.CreateDirectory(candidate);
                string probe = Path.Combine(candidate, ".obsidian-write-probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return candidate;
            }
            catch
            {
                // Try the next candidate.
            }
        }

        return Environment.CurrentDirectory;
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public AppSettings Current { get; private set; }

    /// <summary>Settings object changed (persisted or not). Always UI thread.</summary>
    public event Action? Changed;

    /// <summary>Persistence failed; carries a human readable reason.</summary>
    public event Action<string>? SaveFailed;

    /// <summary>A settings file existed but could not be read; defaults are used.</summary>
    public string? LoadError { get; }

    public SettingsStore()
    {
        (Current, LoadError) = Load();
        Current.OutputDirectoryFallback += _ => NotifyChanged();
    }

    public string? SettingsPath => Current.SettingsPath;

    public bool Save() => TrySave(out _);

    /// <summary>
    /// Persists the current settings. Never throws: the UI treats settings as a
    /// convenience, but it does report the failure and always notifies
    /// subscribers that the object changed.
    /// </summary>
    public bool TrySave(out string? error)
    {
        error = null;
        string? failure = null;
        try
        {
            string path = Current.SettingsPath;
            if (string.IsNullOrEmpty(path))
            {
                path = PickWritablePath();
                if (string.IsNullOrEmpty(path))
                {
                    failure = "No writable settings location was found.";
                    error = failure;
                    return false;
                }
                Current.SettingsPath = path;
            }

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, JsonSerializer.Serialize(Current, JsonOptions));
            return true;
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            error = failure;
            return false;
        }
        finally
        {
            string? failureText = failure;
            if (!string.IsNullOrEmpty(failureText))
                UiDispatch.Run(() => SaveFailed?.Invoke(failureText));
            NotifyChanged();
        }
    }

    public void NotifyChanged() => UiDispatch.Run(() => Changed?.Invoke());

    private (AppSettings Settings, string? Error) Load()
    {
        string? firstError = null;
        foreach (string candidate in Candidates())
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            try
            {
                if (!File.Exists(candidate))
                    continue;
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(candidate), JsonOptions);
                if (settings == null)
                    continue;

                settings.SettingsPath = candidate;
                settings.Normalise();
                return (settings, null);
            }
            catch (Exception ex)
            {
                // Corrupt or unreadable settings file: fall through to defaults
                // but remember why so the UI can tell the user.
                firstError ??= $"{Path.GetFileName(candidate)}: {ex.Message}";
            }
        }

        var fresh = new AppSettings();
        fresh.SettingsPath = PickWritablePath();
        return (fresh, firstError);
    }

    private static JsonSerializerOptions CreateJsonOptions() => new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string PickWritablePath()
    {
        foreach (string candidate in Candidates())
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            try
            {
                string? directory = Path.GetDirectoryName(candidate);
                if (directory == null)
                    continue;
                Directory.CreateDirectory(directory);
                string probe = Path.Combine(directory, ".obsidian-settings-probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return candidate;
            }
            catch
            {
                // next
            }
        }
        return string.Empty;
    }

    private static IEnumerable<string> Candidates()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OBSIDIAN", "settings.json");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OBSIDIAN", "settings.json");
        yield return Path.Combine(AppContext.BaseDirectory, "settings.json");
    }
}
