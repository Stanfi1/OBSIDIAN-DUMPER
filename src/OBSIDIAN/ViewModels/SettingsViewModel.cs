using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Obsidian.Common;
using Obsidian.Dumper.Core.Diagnostics;
using Obsidian.Dumper.Core.Injection;
using Obsidian.Dumper.Core.Model;
using Obsidian.Services;

namespace Obsidian.ViewModels;

/// <summary>One accent preset chip in the Appearance section.</summary>
public sealed class AccentOption : ObservableObject
{
    private Brush _swatch = Brushes.Transparent;
    private bool _isSelected;

    public AccentOption(AccentPreset preset, string label)
    {
        Preset = preset;
        Label = label;
    }

    public AccentPreset Preset { get; }
    public string Label { get; }

    public Brush Swatch
    {
        get => _swatch;
        private set => Set(ref _swatch, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    internal void Refresh(AccentPreset current, Brush? swatch)
    {
        IsSelected = Preset == current;
        if (swatch != null)
            Swatch = swatch;
    }
}

/// <summary>Operator-facing label/value pair for a themed combo box.</summary>
public sealed class ChoiceOption<T>
{
    public ChoiceOption(T value, string label, string description = "")
    {
        Value = value;
        Label = label;
        Description = description;
    }

    public T Value { get; }
    public string Label { get; }
    public string Description { get; }
    public override string ToString() => Label;
}

public sealed class SettingsViewModel : ObservableObject
{
    private static readonly int[] DefaultMaxLogEntryOptions = { 500, 1000, 2000, 5000, 10000, 25000, 50000 };

    private readonly ShellViewModel _shell;
    private readonly SettingsStore _store;
    private AppTheme? _themeDisplayOverride;

    public SettingsViewModel(ShellViewModel shell, SettingsStore store)
    {
        _shell = shell;
        _store = store;

        BrowseOutputCommand = new RelayCommand(BrowseOutput);
        OpenOutputCommand = new RelayCommand(OpenOutput);
        ResetCommand = new RelayCommand(ResetDefaults);
        OpenSettingsFolderCommand = new RelayCommand(OpenSettingsFolder);
        BrowseInjectionDirectoryCommand = new RelayCommand(BrowseInjectionDirectory);
        BrowseDeepDumpDirectoryCommand = new RelayCommand(BrowseDeepDumpDirectory);
        CopyDiagnosticsCommand = new RelayCommand(CopyDiagnostics);
        OpenThirdPartyNoticesCommand = new RelayCommand(OpenThirdPartyNotices);
        SelectAccentCommand = new RelayCommand(SelectAccent);
        SelectThemeCommand = new RelayCommand(SelectTheme);
        SelectDensityCommand = new RelayCommand(SelectDensity);

        foreach ((AccentPreset preset, string label) in new[]
                 {
                     (AccentPreset.Red, "Ember"),
                     (AccentPreset.Cyan, "Steel"),
                     (AccentPreset.Green, "Jade"),
                     (AccentPreset.Amber, "Amber"),
                     (AccentPreset.Violet, "Iris"),
                 })
        {
            AccentOptions.Add(new AccentOption(preset, label));
        }

        OverwritePolicyChoices = new[]
        {
            new ChoiceOption<DumpOverwritePolicy>(DumpOverwritePolicy.Rename, "Rename",
                "Keep both files: writes module_2.dll, module_3.dll, ..."),
            new ChoiceOption<DumpOverwritePolicy>(DumpOverwritePolicy.Overwrite, "Overwrite",
                "Replace the existing dump file."),
            new ChoiceOption<DumpOverwritePolicy>(DumpOverwritePolicy.Fail, "Fail",
                "Leave the existing file untouched and report the module as failed."),
        };

        InjectionMethodChoices = new[]
        {
            new ChoiceOption<InjectionMethod>(InjectionMethod.CreateRemoteThread, "CreateRemoteThread",
                "Classic LoadLibraryW thread. Most compatible; some endpoint tools flag it."),
            new ChoiceOption<InjectionMethod>(InjectionMethod.NtCreateThreadEx, "NtCreateThreadEx",
                "Same idea through ntdll; x64 targets only."),
            new ChoiceOption<InjectionMethod>(InjectionMethod.QueueUserApc, "QueueUserAPC",
                "Queues LoadLibraryW on the target's threads. No new thread is created."),
        };

        AutoRefreshChoices = new[]
        {
            new ChoiceOption<int>(0, "Off"),
            new ChoiceOption<int>(2000, "Every 2 s"),
            new ChoiceOption<int>(5000, "Every 5 s"),
            new ChoiceOption<int>(10000, "Every 10 s"),
            new ChoiceOption<int>(30000, "Every 30 s"),
        };

        MaxLogEntriesOptions = new ObservableCollection<int>(DefaultMaxLogEntryOptions);
        int persistedCapacity = _store.Current.MaxLogEntries;
        if (!MaxLogEntriesOptions.Contains(persistedCapacity))
            MaxLogEntriesOptions.Insert(0, persistedCapacity);

        ParallelThreadOptions = Enumerable.Range(1, 8).ToArray();

        RefreshAccentOptions();

        // External changes (output-directory fallback, theme service, another
        // page writing a setting) must refresh the visible values.
        _store.Changed += OnStoreChanged;
        ThemeService.ThemeChanged += OnThemeChanged;
    }

    public AppSettings Model => _store.Current;

    public ObservableCollection<AccentOption> AccentOptions { get; } = new();
    public ObservableCollection<int> MaxLogEntriesOptions { get; }
    public IReadOnlyList<ChoiceOption<DumpOverwritePolicy>> OverwritePolicyChoices { get; }
    public IReadOnlyList<ChoiceOption<InjectionMethod>> InjectionMethodChoices { get; }
    public IReadOnlyList<ChoiceOption<int>> AutoRefreshChoices { get; }
    public IReadOnlyList<int> ParallelThreadOptions { get; }
    public IReadOnlyList<LogLevel> LogLevels { get; } = Enum.GetValues<LogLevel>();

    public RelayCommand BrowseOutputCommand { get; }
    public RelayCommand OpenOutputCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand OpenSettingsFolderCommand { get; }
    public RelayCommand BrowseInjectionDirectoryCommand { get; }
    public RelayCommand BrowseDeepDumpDirectoryCommand { get; }
    public RelayCommand CopyDiagnosticsCommand { get; }
    public RelayCommand OpenThirdPartyNoticesCommand { get; }
    public RelayCommand SelectAccentCommand { get; }
    public RelayCommand SelectThemeCommand { get; }
    public RelayCommand SelectDensityCommand { get; }

    // ================================================================ appearance

    public AppTheme Theme
    {
        get => _themeDisplayOverride ?? _store.Current.Theme;
        set
        {
            _themeDisplayOverride = null;
            if (_store.Current.Theme == value)
            {
                RaiseThemeProperties();
                return;
            }

            _store.Current.Theme = value;
            _store.Save();
            ThemeService.Apply(value, _store.Current.AccentColor);
            RaiseThemeProperties();
        }
    }

    public void ApplyStartupThemeOverride(AppTheme? theme)
    {
        _themeDisplayOverride = theme;
        RaiseThemeProperties();
    }

    private void RaiseThemeProperties()
        => Raise(nameof(Theme), nameof(IsSystemTheme), nameof(IsDarkTheme), nameof(IsLightTheme), nameof(ThemeSummary));

    /// <summary>
    /// Radio-style handler for the theme chips: the chips use a OneWay binding
    /// plus this command, so re-selecting the active chip cannot leave it
    /// visually unchecked (the setter would ignore the false value).
    /// </summary>
    private void SelectTheme(object? parameter)
    {
        if (parameter is string text && Enum.TryParse(text, ignoreCase: true, out AppTheme theme))
            Theme = theme;

        RaiseThemeProperties();
    }

    /// <summary>Radio-style handler for the density chips (see <see cref="SelectTheme"/>).</summary>
    private void SelectDensity(object? parameter)
    {
        if (parameter is string text && Enum.TryParse(text, ignoreCase: true, out UiDensity density))
            Density = density;

        Raise(nameof(IsComfortableDensity), nameof(IsCompactDensity));
    }

    public bool IsSystemTheme
    {
        get => Theme == AppTheme.System;
        set
        {
            if (value)
                Theme = AppTheme.System;
        }
    }

    public bool IsDarkTheme
    {
        get => Theme == AppTheme.Dark;
        set
        {
            if (value)
                Theme = AppTheme.Dark;
        }
    }

    public bool IsLightTheme
    {
        get => Theme == AppTheme.Light;
        set
        {
            if (value)
                Theme = AppTheme.Light;
        }
    }

    public string ThemeSummary
    {
        get
        {
            string summary = Theme switch
            {
                AppTheme.System => ThemeService.IsDark
                    ? "Following Windows — currently dark"
                    : "Following Windows — currently light",
                AppTheme.Dark => "Always dark",
                _ => "Always light",
            };

            return _themeDisplayOverride is null
                ? summary
                : summary + " · override for this run only (not saved)";
        }
    }

    public AccentPreset AccentColor
    {
        get => _store.Current.AccentColor;
        set
        {
            if (_store.Current.AccentColor == value)
                return;

            _store.Current.AccentColor = value;
            _store.Save();
            ThemeService.Apply(_store.Current.Theme, value);
            Raise(nameof(AccentColor), nameof(AccentLabel));
            RefreshAccentOptions();
        }
    }

    /// <summary>Design-system name of the active accent preset (see DESIGN_SYSTEM.md §1.3).</summary>
    public string AccentLabel => AccentColor switch
    {
        AccentPreset.Red => "Ember (OBSIDIAN)",
        AccentPreset.Cyan => "Steel",
        AccentPreset.Green => "Jade",
        AccentPreset.Amber => "Amber",
        _ => "Iris",
    };

    public bool IsComfortableDensity
    {
        get => _store.Current.Density == UiDensity.Comfortable;
        set
        {
            if (value)
                Density = UiDensity.Comfortable;
        }
    }

    public bool IsCompactDensity
    {
        get => _store.Current.Density == UiDensity.Compact;
        set
        {
            if (value)
                Density = UiDensity.Compact;
        }
    }

    public UiDensity Density
    {
        get => _store.Current.Density;
        set
        {
            if (_store.Current.Density == value)
                return;

            _store.Current.Density = value;
            _store.Save();
            DensityService.Apply(value);
            Raise(nameof(Density), nameof(IsComfortableDensity), nameof(IsCompactDensity));
        }
    }

    public bool Animations
    {
        get => _store.Current.Animations;
        set
        {
            if (_store.Current.Animations == value)
                return;
            _store.Current.Animations = value;
            _store.Save();
            Raise();
        }
    }

    public bool ShowEngineVersions
    {
        get => _store.Current.ShowEngineVersions;
        set
        {
            if (_store.Current.ShowEngineVersions == value)
                return;
            _store.Current.ShowEngineVersions = value;
            _store.Save();
            _shell.RefreshEngineVersionsVisibility();
            Raise();
        }
    }

    // ================================================================== dumping

    public string OutputDirectory
    {
        get => _store.Current.OutputDirectory;
        set
        {
            if (_store.Current.OutputDirectory == value)
                return;
            _store.Current.OutputDirectory = value;
            _store.Save();
            Raise(nameof(OutputDirectory), nameof(OutputDirectoryHint));
        }
    }

    public string OutputDirectoryHint
    {
        get
        {
            string configured = _store.Current.OutputDirectory;
            string effective = _store.Current.PeekEffectiveOutputDirectory();
            return string.Equals(ConfiguredPath(configured), ConfiguredPath(effective), StringComparison.OrdinalIgnoreCase)
                ? "Writes to this folder."
                : "Configured folder is not writable — dumps go to " + effective;
        }
    }

    private static string ConfiguredPath(string path)
    {
        try
        {
            return Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? "." : path);
        }
        catch
        {
            return path;
        }
    }

    public string FileNameTemplate
    {
        get => _store.Current.FileNameTemplate;
        set
        {
            if (_store.Current.FileNameTemplate == value)
                return;
            _store.Current.FileNameTemplate = value;
            _store.Save();
            Raise(nameof(FileNameTemplate), nameof(FileNamePreview), nameof(FileNamePreviewHint),
                nameof(HasFileNamePreviewHint));
        }
    }

    /// <summary>Live preview of the template using a representative module.</summary>
    public string FileNamePreview
    {
        get
        {
            string template = string.IsNullOrWhiteSpace(FileNameTemplate) ? "{name}" : FileNameTemplate;
            string name = template
                .Replace("{name}", "Sample.Module", StringComparison.OrdinalIgnoreCase)
                .Replace("{pid}", "4242", StringComparison.OrdinalIgnoreCase)
                .Replace("{base}", "0x7FF6A1C40000", StringComparison.OrdinalIgnoreCase)
                .Replace("{ext}", "dll", StringComparison.OrdinalIgnoreCase)
                .Replace("{module}", "Sample.Module.dll", StringComparison.OrdinalIgnoreCase);

            foreach (char invalid in Path.GetInvalidFileNameChars())
                name = name.Replace(invalid, '_');

            name = name.Trim();
            if (string.IsNullOrWhiteSpace(name))
                name = "module";

            if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                name += ".dll";
            }

            return "Preview: " + name;
        }
    }

    public bool HasFileNamePreviewHint => !string.IsNullOrEmpty(FileNamePreviewHint);

    public string FileNamePreviewHint
    {
        get
        {
            string template = FileNameTemplate ?? string.Empty;
            if (template.Length == 0)
                return "Empty templates fall back to {name}.";

            string unknown = template;
            foreach (string token in new[] { "{name}", "{pid}", "{base}", "{ext}", "{module}" })
                unknown = unknown.Replace(token, string.Empty, StringComparison.OrdinalIgnoreCase);

            bool invalid = Path.GetInvalidFileNameChars().Any(template.Contains);
            if (unknown.Contains('{') || unknown.Contains('}'))
                return "Unknown token — supported tokens are {name}, {pid}, {base}, {ext}, {module}.";
            if (invalid)
                return "The template contains characters Windows does not allow in file names; they become '_'.";
            return string.Empty;
        }
    }

    public bool RebuildPe
    {
        get => _store.Current.RebuildPe;
        set
        {
            if (_store.Current.RebuildPe == value)
                return;
            _store.Current.RebuildPe = value;
            _store.Save();
            Raise();
        }
    }

    public bool VerifyMetadata
    {
        get => _store.Current.VerifyMetadata;
        set
        {
            if (_store.Current.VerifyMetadata == value)
                return;
            _store.Current.VerifyMetadata = value;
            _store.Save();
            Raise();
        }
    }

    public bool RepairMetadata
    {
        get => _store.Current.RepairMetadata;
        set
        {
            if (_store.Current.RepairMetadata == value)
                return;
            _store.Current.RepairMetadata = value;
            _store.Save();
            Raise();
        }
    }

    public bool InvalidateStrongName
    {
        get => _store.Current.InvalidateStrongName;
        set
        {
            if (_store.Current.InvalidateStrongName == value)
                return;
            _store.Current.InvalidateStrongName = value;
            _store.Save();
            Raise();
        }
    }

    public bool StripDebugDirectory
    {
        get => _store.Current.StripDebugDirectory;
        set
        {
            if (_store.Current.StripDebugDirectory == value)
                return;
            _store.Current.StripDebugDirectory = value;
            _store.Save();
            Raise();
        }
    }

    public bool StripBoundImports
    {
        get => _store.Current.StripBoundImports;
        set
        {
            if (_store.Current.StripBoundImports == value)
                return;
            _store.Current.StripBoundImports = value;
            _store.Save();
            Raise();
        }
    }

    public bool HideFrameworkModules
    {
        get => _store.Current.HideFrameworkModules;
        set
        {
            if (_store.Current.HideFrameworkModules == value)
                return;
            _store.Current.HideFrameworkModules = value;
            _store.Save();
            _shell.Workspace.HideFramework = value;
            Raise();
        }
    }

    public bool DumpNativeModules
    {
        get => _store.Current.DumpNativeModules;
        set
        {
            if (_store.Current.DumpNativeModules == value)
                return;
            _store.Current.DumpNativeModules = value;
            _store.Save();
            Raise();
        }
    }

    public bool RawImage
    {
        get => _store.Current.RawImage;
        set
        {
            if (_store.Current.RawImage == value)
                return;
            _store.Current.RawImage = value;
            _store.Save();
            Raise();
        }
    }

    public DumpOverwritePolicy OverwritePolicy
    {
        get => _store.Current.OverwritePolicy;
        set
        {
            if (_store.Current.OverwritePolicy == value)
                return;
            _store.Current.OverwritePolicy = value;
            _store.Save();
            Raise();
        }
    }

    public ChoiceOption<DumpOverwritePolicy> SelectedOverwritePolicy
    {
        get => OverwritePolicyChoices.FirstOrDefault(c => c.Value == _store.Current.OverwritePolicy)
               ?? OverwritePolicyChoices[0];
        set
        {
            DumpOverwritePolicy policy = value?.Value ?? DumpOverwritePolicy.Rename;
            if (_store.Current.OverwritePolicy == policy)
                return;
            _store.Current.OverwritePolicy = policy;
            _store.Save();
            Raise();
        }
    }

    public bool ConfirmOverwrite
    {
        get => _store.Current.ConfirmOverwrite;
        set
        {
            if (_store.Current.ConfirmOverwrite == value)
                return;
            _store.Current.ConfirmOverwrite = value;
            _store.Save();
            Raise();
        }
    }

    public bool AutoOpenOutputFolder
    {
        get => _store.Current.AutoOpenOutputFolder;
        set
        {
            if (_store.Current.AutoOpenOutputFolder == value)
                return;
            _store.Current.AutoOpenOutputFolder = value;
            _store.Save();
            Raise();
        }
    }

    public int ParallelDumpThreads
    {
        get => _store.Current.ParallelDumpThreads;
        set
        {
            int clamped = Math.Clamp(value, 1, 8);
            if (_store.Current.ParallelDumpThreads == clamped)
                return;
            _store.Current.ParallelDumpThreads = clamped;
            _store.Save();
            Raise();
        }
    }

    public string DeepDumpWorkDirectory
    {
        get => _store.Current.DeepDumpWorkDirectory;
        set
        {
            string trimmed = (value ?? string.Empty).Trim();
            if (_store.Current.DeepDumpWorkDirectory == trimmed)
                return;
            _store.Current.DeepDumpWorkDirectory = trimmed;
            _store.Save();
            Raise(nameof(DeepDumpWorkDirectory), nameof(DeepDumpWorkDirectoryHint));
        }
    }

    public string DeepDumpWorkDirectoryHint => string.IsNullOrEmpty(DeepDumpWorkDirectory)
        ? "Automatic: the dumper picks a writable folder."
        : "A per-run GUID folder is created inside this directory.";

    // ================================================================ processes

    public bool ShowInaccessibleProcesses
    {
        get => _store.Current.ShowInaccessibleProcesses;
        set => SetBool(_store.Current.ShowInaccessibleProcesses, value, v => _store.Current.ShowInaccessibleProcesses = v);
    }

    public bool LazyCommandLine
    {
        get => _store.Current.LazyCommandLine;
        set => SetBool(_store.Current.LazyCommandLine, value, v => _store.Current.LazyCommandLine = v);
    }

    public ChoiceOption<int> SelectedAutoRefresh
    {
        get => AutoRefreshChoices.FirstOrDefault(c => c.Value == _store.Current.AutoRefreshIntervalMs)
               ?? AutoRefreshChoices[0];
        set
        {
            int milliseconds = value?.Value ?? 0;
            if (_store.Current.AutoRefreshIntervalMs == milliseconds)
                return;
            _store.Current.AutoRefreshIntervalMs = milliseconds;
            _store.Save();
            Raise();
        }
    }

    public bool RememberFilters
    {
        get => _store.Current.RememberFilters;
        set => SetBool(_store.Current.RememberFilters, value, v => _store.Current.RememberFilters = v);
    }

    public bool ConfirmDestructiveActions
    {
        get => _store.Current.ConfirmDestructiveActions;
        set => SetBool(_store.Current.ConfirmDestructiveActions, value, v => _store.Current.ConfirmDestructiveActions = v);
    }

    public bool HighlightElevated
    {
        get => _store.Current.HighlightElevated;
        set => SetBool(_store.Current.HighlightElevated, value, v => _store.Current.HighlightElevated = v);
    }

    public bool ShowIntegrity
    {
        get => _store.Current.ShowIntegrity;
        set => SetBool(_store.Current.ShowIntegrity, value, v => _store.Current.ShowIntegrity = v);
    }

    // ================================================================ injection

    public bool ConfirmInjection
    {
        get => _store.Current.ConfirmInjection;
        set => SetBool(_store.Current.ConfirmInjection, value, v => _store.Current.ConfirmInjection = v);
    }

    public ChoiceOption<InjectionMethod> SelectedInjectionMethod
    {
        get => InjectionMethodChoices.FirstOrDefault(c => c.Value == _store.Current.DefaultInjectionMethod)
               ?? InjectionMethodChoices[0];
        set
        {
            InjectionMethod method = value?.Value ?? InjectionMethod.CreateRemoteThread;
            if (_store.Current.DefaultInjectionMethod == method)
                return;
            _store.Current.DefaultInjectionMethod = method;
            _store.Save();
            Raise();
        }
    }

    public string InjectionDllDirectory
    {
        get => _store.Current.InjectionDllDirectory;
        set
        {
            string trimmed = (value ?? string.Empty).Trim();
            if (_store.Current.InjectionDllDirectory == trimmed)
                return;
            _store.Current.InjectionDllDirectory = trimmed;
            _store.Save();
            Raise(nameof(InjectionDllDirectory), nameof(InjectionDllDirectoryHint));
        }
    }

    public string InjectionDllDirectoryHint => string.IsNullOrEmpty(InjectionDllDirectory)
        ? "Automatic: the last used folder is remembered."
        : InjectionDllDirectory;

    public bool AutoUnloadOnDetach
    {
        get => _store.Current.AutoUnloadOnDetach;
        set => SetBool(_store.Current.AutoUnloadOnDetach, value, v => _store.Current.AutoUnloadOnDetach = v);
    }

    // ================================================================= activity

    public int MaxLogEntries
    {
        get => _store.Current.MaxLogEntries;
        set
        {
            int clamped = Math.Clamp(value, 500, 50000);
            if (_store.Current.MaxLogEntries == clamped)
                return;
            _store.Current.MaxLogEntries = clamped;
            if (!MaxLogEntriesOptions.Contains(clamped))
                MaxLogEntriesOptions.Insert(0, clamped);
            _store.Save();
            Raise();
        }
    }

    public bool LogShowTimestamps
    {
        get => _store.Current.LogShowTimestamps;
        set => SetBool(_store.Current.LogShowTimestamps, value, v => _store.Current.LogShowTimestamps = v);
    }

    public bool LogAutoScroll
    {
        get => _store.Current.LogAutoScroll;
        set
        {
            if (_store.Current.LogAutoScroll == value)
                return;
            _store.Current.LogAutoScroll = value;
            _store.Save();
            _shell.LogPage.AutoScroll = value;
            Raise();
        }
    }

    public LogLevel LogMinLevel
    {
        get => _store.Current.LogMinLevel;
        set
        {
            if (_store.Current.LogMinLevel == value)
                return;
            _store.Current.LogMinLevel = value;
            _store.Save();
            _shell.ApplyLogLevelFilter(value);
            Raise();
        }
    }

    // ============================================================== diagnostics

    public string SettingsPath => string.IsNullOrEmpty(_store.Current.SettingsPath)
        ? "not persisted"
        : _store.Current.SettingsPath;

    public string SettingsFolder => string.IsNullOrEmpty(_store.Current.SettingsPath)
        ? AppContext.BaseDirectory
        : Path.GetDirectoryName(_store.Current.SettingsPath) ?? AppContext.BaseDirectory;

    public string EffectiveOutputDirectory => _store.Current.PeekEffectiveOutputDirectory();

    public string VersionText => AppInfo.VersionText;

    public string EngineText => AppInfo.EngineSummary;

    public bool HasLoadError => !string.IsNullOrEmpty(_store.LoadError);

    public string LoadErrorText => _store.LoadError ?? string.Empty;

    public void RefreshAll() => Raise(string.Empty);

    private void OnStoreChanged() => RefreshAll();

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        Raise(nameof(ThemeSummary));
        RefreshAccentOptions();
    }

    private void RefreshAccentOptions()
    {
        foreach (AccentOption option in AccentOptions)
            option.Refresh(_store.Current.AccentColor, ThemeService.GetAccentPresetBrush(option.Preset));
    }

    private void SelectAccent(object? parameter)
    {
        if (parameter is AccentPreset preset)
            AccentColor = preset;
    }

    private bool SetBool(bool current, bool value, Action<bool> assign, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (current == value)
            return false;

        assign(value);
        _store.Save();
        Raise(name);
        return true;
    }

    // ================================================================ commands

    private void BrowseOutput()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select the dump output directory",
                InitialDirectory = Directory.Exists(OutputDirectory) ? OutputDirectory : Environment.CurrentDirectory,
            };

            if (dialog.ShowDialog() == true)
            {
                OutputDirectory = dialog.FolderName;
                _shell.Toast("Output directory updated", dialog.FolderName, ToastKind.Info);
            }
        }
        catch (Exception ex)
        {
            _shell.Toast("Could not browse", ex.Message, ToastKind.Warning);
        }
    }

    private void BrowseInjectionDirectory()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Default DLL directory for the Injection page",
                InitialDirectory = Directory.Exists(InjectionDllDirectory)
                    ? InjectionDllDirectory
                    : Environment.SystemDirectory,
            };

            if (dialog.ShowDialog() == true)
            {
                InjectionDllDirectory = dialog.FolderName;
                _shell.Toast("Injection folder updated", dialog.FolderName, ToastKind.Info);
            }
        }
        catch (Exception ex)
        {
            _shell.Toast("Could not browse", ex.Message, ToastKind.Warning);
        }
    }

    private void BrowseDeepDumpDirectory()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Deep-dump work directory",
                InitialDirectory = Directory.Exists(DeepDumpWorkDirectory)
                    ? DeepDumpWorkDirectory
                    : Environment.CurrentDirectory,
            };

            if (dialog.ShowDialog() == true)
                DeepDumpWorkDirectory = dialog.FolderName;
        }
        catch (Exception ex)
        {
            _shell.Toast("Could not browse", ex.Message, ToastKind.Warning);
        }
    }

    private void OpenOutput()
    {
        try
        {
            string directory = _store.Current.EffectiveOutputDirectory();
            Directory.CreateDirectory(directory);
            Process.Start("explorer.exe", $"\"{directory}\"");
        }
        catch (Exception ex)
        {
            _shell.Toast("Could not open the output folder", ex.Message, ToastKind.Warning);
        }
    }

    private void OpenSettingsFolder()
    {
        try
        {
            Directory.CreateDirectory(SettingsFolder);
            Process.Start("explorer.exe", $"\"{SettingsFolder}\"");
        }
        catch (Exception ex)
        {
            _shell.Toast("Could not open the settings folder", ex.Message, ToastKind.Warning);
        }
    }

    private void OpenThirdPartyNotices()
    {
        try
        {
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md"),
                Path.Combine(AppContext.BaseDirectory, "..", "THIRD-PARTY-NOTICES.md"),
            };

            string? notice = candidates.FirstOrDefault(File.Exists);
            if (notice is null)
            {
                _shell.Toast("Notices not available", "THIRD-PARTY-NOTICES.md was not shipped with this build.", ToastKind.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo(notice) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _shell.Toast("Could not open the notices", ex.Message, ToastKind.Warning);
        }
    }

    private void CopyDiagnostics()
    {
        try
        {
            var text = new StringBuilder();
            text.AppendLine(AppInfo.ProductTitle + " " + AppInfo.Version);
            text.AppendLine("Engine: " + AppInfo.EngineSummary);
            text.AppendLine("Runtime: " + AppInfo.RuntimeLabel + " (" + (Environment.Is64BitProcess ? "x64" : "x86") + " process)");
            text.AppendLine("Windows: " + Environment.OSVersion.Version);
            text.AppendLine("Theme: " + Theme + (ThemeService.IsDark ? " (dark)" : " (light)") + ", accent " + AccentColor);
            text.AppendLine("Settings: " + SettingsPath);
            text.AppendLine("Settings schema: " + _store.Current.SchemaVersion);
            text.AppendLine("Output directory: " + EffectiveOutputDirectory);
            text.AppendLine("Attached target: " + (_shell.Session?.Process.DisplayName ?? "none"));
            text.AppendLine("Activity log: " + _shell.Log.Snapshot().Count + " entries");
            text.AppendLine();
            text.AppendLine("Recent activity:");
            foreach (LogEntry entry in _shell.Log.Snapshot().TakeLast(25))
                text.AppendLine("  " + entry);

            Clipboard.SetText(text.ToString());
            _shell.Toast("Diagnostics copied", "Version, paths, theme and the last 25 log lines are on the clipboard.", ToastKind.Success);
        }
        catch (Exception ex)
        {
            _shell.Toast("Could not copy diagnostics", ex.Message, ToastKind.Warning);
        }
    }

    private void ResetDefaults()
    {
        if (!_shell.Confirm(
                "Reset settings",
                "Restore every option in Settings to its default value?\n\nOutput and dump preferences are reset; files already on disk are kept.",
                "Reset",
                destructive: true))
        {
            return;
        }

        _store.Current.ResetToDefaults();
        _store.Save();

        // Reapply values that live outside the settings object.
        _shell.Workspace.HideFramework = _store.Current.HideFrameworkModules;
        ThemeService.Apply(_store.Current.Theme, _store.Current.AccentColor);
        DensityService.Apply(_store.Current.Density);
        _shell.ApplyLogLevelFilter(_store.Current.LogMinLevel);
        _shell.LogPage.AutoScroll = _store.Current.LogAutoScroll;
        _shell.RefreshEngineVersionsVisibility();

        RefreshAll();
        RefreshAccentOptions();
        _shell.Toast("Settings reset", "Defaults restored.", ToastKind.Info);
    }
}
