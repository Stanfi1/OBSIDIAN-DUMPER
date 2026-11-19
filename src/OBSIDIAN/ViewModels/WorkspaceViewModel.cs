using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using Obsidian.Common;
using Obsidian.Dumper.Core;
using Obsidian.Dumper.Core.Dumping;
using Obsidian.Dumper.Core.Injection;
using Obsidian.Dumper.Core.Inspection;
using Obsidian.Dumper.Core.Model;

namespace Obsidian.ViewModels;

public sealed class WorkspaceViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly ViewRefreshDebouncer _searchDebouncer;
    private TargetSession? _session;
    private string _search = string.Empty;
    private bool _showManagedOnly = true;
    private bool _hideFramework = true;
    private bool _showDumpedOnly;
    private long _dumpedBytes;
    private int _dumpDurationMs;
    private string _lastDumpSummary = string.Empty;
    private ModuleRecord? _selected;
    private bool _isDumping;
    private double _dumpProgress;
    private double _dumpMax = 1;
    private string _dumpStatusText = string.Empty;
    private int _dumpedCount;
    private int _failedCount;
    private int _selectedCount;
    private bool _isDeepDumping;
    private CancellationTokenSource? _dumpCts;

    private string _targetName = "No target attached";
    private string _targetRuntime = "—";
    private string _targetPid = "—";
    private string _targetArch = "—";
    private string _targetIntegrity = "—";
    private string _targetPath = "—";
    private string _targetCommandLine = "—";

    public WorkspaceViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Modules = new ObservableCollection<ModuleRecord>();
        View = CollectionViewSource.GetDefaultView(Modules);
        View.Filter = FilterModule;

        DumpAllCommand = new AsyncRelayCommand(DumpAllAsync, () => HasTarget && !IsBusyOrDumping);
        DumpSelectedCommand = new AsyncRelayCommand(DumpSelectedAsync, () => HasTarget && SelectedCount > 0 && !IsBusyOrDumping);
        DumpModuleCommand = new AsyncRelayCommand(DumpModuleAsync, () => HasTarget && Selected != null && !IsBusyOrDumping);
        DumpNativeCommand = new AsyncRelayCommand(DumpNativeAsync, () => HasTarget && SelectedCount > 0 && !IsBusyOrDumping);
        DumpAllModulesCommand = new AsyncRelayCommand(DumpAllModulesAsync, () => HasTarget && !IsBusyOrDumping);
        DeepDumpCommand = new AsyncRelayCommand(RunDeepDumpAsync, () => CanDeepDump && !IsBusyOrDumping);
        CancelDumpCommand = new RelayCommand(CancelDump, () => IsDumping);
        RescanCommand = new RelayCommand(Rescan, () => HasTarget && !IsBusyOrDumping);
        InspectCommand = new AsyncRelayCommand(InspectSelectedAsync, () => Selected?.IsManaged == true && !IsBusyOrDumping);
        RevealCommand = new RelayCommand(RevealSelected, () => Selected?.DumpedPath != null);
        OpenDumpedFileCommand = new RelayCommand(OpenDumpedFile, () => Selected?.DumpedPath != null);
        OpenDumpedFolderCommand = new RelayCommand(OpenDumpedFolder, () => Selected?.DumpedPath != null);
        CopyModulePathCommand = new RelayCommand(CopyModulePath, () => Selected != null);
        CopyModuleNameCommand = new RelayCommand(CopyModuleName, () => Selected != null);
        OpenOutputCommand = new RelayCommand(OpenOutput);
        SelectAllCommand = new RelayCommand(SelectAllVisible, () => HasTarget && !IsBusyOrDumping);
        InvertSelectionCommand = new RelayCommand(InvertSelection, () => HasTarget && Modules.Count > 0 && !IsBusyOrDumping);
        ClearSelectionCommand = new RelayCommand(ClearSelection, () => SelectedCount > 0 && !IsBusyOrDumping);
        ClearFiltersCommand = new RelayCommand(ClearFilters, () => ShowManagedOnly || HideFramework || ShowDumpedOnly || !string.IsNullOrWhiteSpace(Search));
        GoToTargetsCommand = new RelayCommand(() => _shell.NavigateTo("targets"));
        ExportCsvCommand = new RelayCommand(ExportCsv, () => !View.IsEmpty);
        ExportJsonCommand = new RelayCommand(ExportJson, () => !View.IsEmpty);

        _searchDebouncer = new ViewRefreshDebouncer(ApplySearchFilter);
        _shell.SettingsStore.Changed += OnSettingsChanged;
    }

    public ObservableCollection<ModuleRecord> Modules { get; }
    public ICollectionView View { get; }

    public AsyncRelayCommand DumpAllCommand { get; }
    public AsyncRelayCommand DumpSelectedCommand { get; }
    public AsyncRelayCommand DumpModuleCommand { get; }
    public AsyncRelayCommand DumpNativeCommand { get; }
    public AsyncRelayCommand DumpAllModulesCommand { get; }
    public AsyncRelayCommand DeepDumpCommand { get; }
    public RelayCommand CancelDumpCommand { get; }
    public RelayCommand RescanCommand { get; }
    public AsyncRelayCommand InspectCommand { get; }
    public RelayCommand RevealCommand { get; }
    public RelayCommand OpenDumpedFileCommand { get; }
    public RelayCommand OpenDumpedFolderCommand { get; }
    public RelayCommand CopyModulePathCommand { get; }
    public RelayCommand CopyModuleNameCommand { get; }
    public RelayCommand OpenOutputCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand InvertSelectionCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public RelayCommand ClearFiltersCommand { get; }
    public RelayCommand GoToTargetsCommand { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand ExportJsonCommand { get; }

    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value))
            {
                _searchDebouncer.Request();
                ClearFiltersCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool ShowManagedOnly
    {
        get => _showManagedOnly;
        set
        {
            if (Set(ref _showManagedOnly, value))
            {
                ClearFiltersCommand.RaiseCanExecuteChanged();
                RefreshView();
            }
        }
    }

    public bool HideFramework
    {
        get => _hideFramework;
        set
        {
            if (!Set(ref _hideFramework, value))
                return;

            ClearFiltersCommand.RaiseCanExecuteChanged();
            RefreshView();

            // The display filter mirrors the dump setting ("include framework
            // modules"), so flipping the chip also persists the operator choice.
            try
            {
                if (_shell.SettingsStore.Current.HideFrameworkModules != value)
                {
                    _shell.SettingsStore.Current.HideFrameworkModules = value;
                    _shell.SettingsStore.Save();
                }
            }
            catch
            {
                // Settings are a convenience; never break the page because of them.
            }
        }
    }

    /// <summary>Quick filter: only modules with a dump on disk.</summary>
    public bool ShowDumpedOnly
    {
        get => _showDumpedOnly;
        set
        {
            if (Set(ref _showDumpedOnly, value))
            {
                ClearFiltersCommand.RaiseCanExecuteChanged();
                RefreshView();
            }
        }
    }

    // ---------------------------------------------------------------- dump settings (quick toggles)

    /// <summary>Mirrors <c>AppSettings.DumpNativeModules</c> as a quick toggle.</summary>
    public bool DumpNativeModules
    {
        get => _shell.SettingsStore.Current.DumpNativeModules;
        set => SetDumpSetting(() => _shell.SettingsStore.Current.DumpNativeModules = value,
            () => _shell.SettingsStore.Current.DumpNativeModules, nameof(DumpNativeModules), value);
    }

    /// <summary>Mirrors <c>AppSettings.RawImage</c> as a quick toggle.</summary>
    public bool RawImage
    {
        get => _shell.SettingsStore.Current.RawImage;
        set => SetDumpSetting(() => _shell.SettingsStore.Current.RawImage = value,
            () => _shell.SettingsStore.Current.RawImage, nameof(RawImage), value);
    }

    /// <summary>Mirrors <c>AppSettings.VerifyMetadata</c> as a quick toggle.</summary>
    public bool VerifyMetadata
    {
        get => _shell.SettingsStore.Current.VerifyMetadata;
        set => SetDumpSetting(() => _shell.SettingsStore.Current.VerifyMetadata = value,
            () => _shell.SettingsStore.Current.VerifyMetadata, nameof(VerifyMetadata), value);
    }

    private void SetDumpSetting(Action assign, Func<bool> read, string propertyName, bool value)
    {
        if (read() == value)
            return;

        assign();
        Raise(propertyName);
        try
        {
            _shell.SettingsStore.Save();
        }
        catch
        {
            // Settings are a convenience; the current dump still uses the value.
        }
    }

    public string DumpSummaryText => _lastDumpSummary;
    public bool HasDumpSummary => !string.IsNullOrEmpty(_lastDumpSummary);

    private void RecordDumpSummary(IReadOnlyList<DumpResult> results)
    {
        int ok = results.Count(result => result.Success);
        int failed = results.Count - ok;
        _dumpedBytes += results.Where(result => result.Success).Sum(result => result.Bytes);
        _dumpDurationMs += results.Sum(result => result.DurationMs);
        _lastDumpSummary = $"{ok} dumped · {failed} failed · {FormatBytes(_dumpedBytes)} · {_dumpDurationMs} ms";
        Raise(nameof(DumpSummaryText), nameof(HasDumpSummary));
    }

    private static string FormatBytes(long bytes)
        => Dumper.Core.Inspection.AssemblySummary.FormatBytes(bytes);

    private void ResetDumpSummary()
    {
        _dumpedBytes = 0;
        _dumpDurationMs = 0;
        _lastDumpSummary = string.Empty;
        Raise(nameof(DumpSummaryText), nameof(HasDumpSummary));
    }

    public ModuleRecord? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value))
                return;
            Raise(nameof(HasSelection), nameof(SelectedModulePath), nameof(SelectedModuleOutput),
                nameof(SelectedModuleNote), nameof(SelectedFrameworkText), nameof(SelectedInMemoryText),
                nameof(SelectedStatusText), nameof(HasDumpedSelection));
            RaiseSelectionCommands();
        }
    }

    public bool HasSelection => Selected != null;
    public string SelectedModulePath => Selected?.DisplayPath ?? "—";
    public string SelectedModuleOutput => Selected?.DumpedPath ?? "not dumped";
    public string SelectedModuleNote => Selected?.Note ?? "—";
    public string SelectedFrameworkText => Selected == null ? "—" : Selected.IsFramework ? "yes" : "no";
    public string SelectedInMemoryText => Selected == null ? "—" : Selected.IsInMemory ? "yes" : "no";
    public string SelectedStatusText => Selected?.StatusLabel ?? "—";
    public bool HasDumpedSelection => Selected?.DumpedPath != null;

    public bool HasTarget => _session != null;

    public int TotalCount => Modules.Count;
    public int ManagedCount => Modules.Count(m => m.IsManaged);
    public int FrameworkCount => Modules.Count(m => m.IsManaged && m.IsFramework);
    public int InMemoryCount => Modules.Count(m => m.IsInMemory);

    public int DumpedCount
    {
        get => _dumpedCount;
        private set => Set(ref _dumpedCount, value);
    }

    public int FailedCount
    {
        get => _failedCount;
        private set => Set(ref _failedCount, value);
    }

    public string VisibleCountLabel => View.Cast<object>().Count().ToString();

    /// <summary>Empty state for the module table (target attached but nothing matches).</summary>
    public bool IsModuleListEmpty => HasTarget && !IsDumping && View.IsEmpty;

    public string ModuleEmptyTitle => ShowManagedOnly || HideFramework || ShowDumpedOnly || !string.IsNullOrWhiteSpace(Search)
        ? "No modules match these filters"
        : "No modules mapped";

    public string ModuleEmptyHint => ShowManagedOnly || HideFramework || ShowDumpedOnly || !string.IsNullOrWhiteSpace(Search)
        ? "Relax the filters or clear the search box."
        : "Press Rescan modules to re-read the target's module list.";

    public int SelectedCount
    {
        get => _selectedCount;
        private set
        {
            if (!Set(ref _selectedCount, value))
                return;
            DumpSelectedCommand.RaiseCanExecuteChanged();
            DumpNativeCommand.RaiseCanExecuteChanged();
            ClearSelectionCommand.RaiseCanExecuteChanged();
            Raise(nameof(HasSelectionCount));
        }
    }

    public bool HasSelectionCount => SelectedCount > 0;

    public bool IsDumping
    {
        get => _isDumping;
        private set
        {
            if (!Set(ref _isDumping, value))
                return;

            RaiseDumpCommandStates();
            Raise(nameof(IsBusyOrDumping), nameof(IsModuleListEmpty));
        }
    }

    public bool IsBusyOrDumping => IsDumping || _isDeepDumping;

    public double DumpProgress
    {
        get => _dumpProgress;
        private set
        {
            if (Set(ref _dumpProgress, value))
                Raise(nameof(DumpProgressText));
        }
    }

    public double DumpMax
    {
        get => _dumpMax;
        private set
        {
            if (Set(ref _dumpMax, value))
                Raise(nameof(DumpProgressText));
        }
    }

    public string DumpProgressText => $"{DumpProgress:0}/{DumpMax:0}";

    public string DumpStatusText
    {
        get => _dumpStatusText;
        private set => Set(ref _dumpStatusText, value);
    }

    public bool CanDeepDump => _session != null
        && _session.Clr?.Flavor == ClrFlavor.Framework
        && _session.Process.Architecture == ProcessArchitecture.X64
        && DeepDumper.IsAvailable;

    public string DeepDumpHint
    {
        get
        {
            if (_session == null)
                return "Attach to a target first.";
            if (!DeepDumper.IsAvailable)
                return "Managed agent assets are missing from this build.";
            if (_session.Clr?.Flavor != ClrFlavor.Framework)
                return "Deep dump hosts a managed agent and requires .NET Framework 4.x. CoreCLR targets cannot host a second runtime.";
            if (_session.Process.Architecture != ProcessArchitecture.X64)
                return "The managed agent is x64 only.";
            return "Run a managed agent inside the target to enumerate assemblies that never appear in the module list (Assembly.Load(byte[])).";
        }
    }

    public string TargetName
    {
        get => _targetName;
        private set => Set(ref _targetName, value);
    }

    public string TargetRuntime
    {
        get => _targetRuntime;
        private set => Set(ref _targetRuntime, value);
    }

    public string TargetPid
    {
        get => _targetPid;
        private set => Set(ref _targetPid, value);
    }

    public string TargetArch
    {
        get => _targetArch;
        private set => Set(ref _targetArch, value);
    }

    public string TargetIntegrity
    {
        get => _targetIntegrity;
        private set => Set(ref _targetIntegrity, value);
    }

    public string TargetPath
    {
        get => _targetPath;
        private set => Set(ref _targetPath, value);
    }

    public string TargetCommandLine
    {
        get => _targetCommandLine;
        private set => Set(ref _targetCommandLine, value);
    }

    public void Load(TargetSession session)
    {
        _session = session;
        HideFramework = _shell.SettingsStore.Current.HideFrameworkModules;
        Unhook();
        Modules.Clear();

        foreach (ModuleRecord module in session.Modules
                     .OrderByDescending(m => m.IsManaged)
                     .ThenBy(m => m.IsFramework)
                     .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            module.PropertyChanged += ModuleOnPropertyChanged;
            Modules.Add(module);
        }

        View.Refresh();
        Selected = null;
        SelectedCount = 0;
        DumpedCount = 0;
        FailedCount = 0;
        ResetDumpSummary();
        RefreshCounts();

        TargetName = session.Process.DisplayName;
        TargetRuntime = session.Clr?.DisplayName ?? "native process";
        TargetPid = session.Id.ToString();
        TargetArch = session.Process.ArchitectureLabel;
        TargetIntegrity = session.Process.IntegrityLabel;
        TargetPath = session.Process.ImagePath ?? "—";
        TargetCommandLine = session.CommandLine ?? "—";

        Raise(nameof(HasTarget), nameof(CanDeepDump), nameof(DeepDumpHint), nameof(IsModuleListEmpty),
            nameof(ModuleEmptyTitle), nameof(ModuleEmptyHint));
        RaiseDumpCommandStates();
        _shell.Log.Info($"Module map loaded: {Modules.Count} modules ({ManagedCount} managed, {FrameworkCount} framework)", "workspace");
    }

    public void Clear()
    {
        _dumpCts?.Cancel();
        Unhook();
        Modules.Clear();
        _session = null;
        TargetName = "No target attached";
        TargetRuntime = "—";
        TargetPid = "—";
        TargetArch = "—";
        TargetIntegrity = "—";
        TargetPath = "—";
        TargetCommandLine = "—";
        DumpedCount = 0;
        FailedCount = 0;
        SelectedCount = 0;
        Selected = null;
        ResetDumpSummary();
        RefreshCounts();
        Raise(nameof(HasTarget), nameof(CanDeepDump), nameof(DeepDumpHint), nameof(IsModuleListEmpty),
            nameof(ModuleEmptyTitle), nameof(ModuleEmptyHint));
        RaiseDumpCommandStates();
    }

    private void RaiseDumpCommandStates()
    {
        DumpAllCommand.RaiseCanExecuteChanged();
        DumpSelectedCommand.RaiseCanExecuteChanged();
        DumpModuleCommand.RaiseCanExecuteChanged();
        DumpNativeCommand.RaiseCanExecuteChanged();
        DumpAllModulesCommand.RaiseCanExecuteChanged();
        DeepDumpCommand.RaiseCanExecuteChanged();
        CancelDumpCommand.RaiseCanExecuteChanged();
        RescanCommand.RaiseCanExecuteChanged();
        InspectCommand.RaiseCanExecuteChanged();
        SelectAllCommand.RaiseCanExecuteChanged();
        InvertSelectionCommand.RaiseCanExecuteChanged();
        ClearSelectionCommand.RaiseCanExecuteChanged();
        ExportCsvCommand.RaiseCanExecuteChanged();
        ExportJsonCommand.RaiseCanExecuteChanged();
        RaiseSelectionCommands();
    }

    private void RaiseSelectionCommands()
    {
        InspectCommand.RaiseCanExecuteChanged();
        RevealCommand.RaiseCanExecuteChanged();
        DumpModuleCommand.RaiseCanExecuteChanged();
        OpenDumpedFileCommand.RaiseCanExecuteChanged();
        OpenDumpedFolderCommand.RaiseCanExecuteChanged();
        CopyModulePathCommand.RaiseCanExecuteChanged();
        CopyModuleNameCommand.RaiseCanExecuteChanged();
    }

    private void Unhook()
    {
        foreach (ModuleRecord module in Modules)
            module.PropertyChanged -= ModuleOnPropertyChanged;
    }

    private void ModuleOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModuleRecord.IsSelected))
        {
            SelectedCount = Modules.Count(m => m.IsSelected);
            return;
        }

        if (e.PropertyName is nameof(ModuleRecord.Status) or nameof(ModuleRecord.DumpedPath) or nameof(ModuleRecord.Note))
        {
            if (ReferenceEquals(sender, Selected))
            {
                Raise(nameof(SelectedModuleOutput), nameof(SelectedModuleNote), nameof(SelectedStatusText),
                    nameof(HasDumpedSelection));
                RaiseSelectionCommands();
            }

            RefreshCounts();
        }
    }

    private void RefreshCounts()
    {
        DumpedCount = Modules.Count(m => m.Status == DumpStatus.Dumped);
        FailedCount = Modules.Count(m => m.Status == DumpStatus.Failed);
        Raise(nameof(ManagedCount), nameof(FrameworkCount), nameof(InMemoryCount), nameof(TotalCount),
            nameof(VisibleCountLabel), nameof(IsModuleListEmpty), nameof(ModuleEmptyTitle), nameof(ModuleEmptyHint));
    }

    private void ApplySearchFilter()
    {
        RefreshView();
        ClearFiltersCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Refresh the filtered module view. WPF clears the ListBox selection when the
    /// ICollectionView raises Reset, so the selected module is restored explicitly and
    /// only cleared when the filters actually hide it.
    /// </summary>
    private void RefreshView()
    {
        ModuleRecord? keep = _selected;
        View.Refresh();

        if (keep != null && View.Contains(keep))
        {
            // The ListBox may have pushed null through the TwoWay SelectedItem
            // binding while the view reset; restore through the property so the
            // dependent notifications and command states are raised.
            if (!ReferenceEquals(_selected, keep))
                Selected = keep;
        }
        else if (keep != null)
        {
            Selected = null;
        }

        Raise(nameof(VisibleCountLabel), nameof(IsModuleListEmpty), nameof(ModuleEmptyTitle),
            nameof(ModuleEmptyHint));
    }

    private void ClearFilters()
    {
        _search = string.Empty;
        Raise(nameof(Search));
        _showManagedOnly = false;
        Raise(nameof(ShowManagedOnly));
        _showDumpedOnly = false;
        Raise(nameof(ShowDumpedOnly));
        _hideFramework = false;
        Raise(nameof(HideFramework));
        ClearFiltersCommand.RaiseCanExecuteChanged();
        RefreshView();
    }

    public void Rescan()
    {
        if (_session == null || IsBusyOrDumping)
            return;

        try
        {
            _shell.Log.Info("Rescanning the module list…", "workspace");
            IReadOnlyList<ModuleRecord> modules = _session.ReloadModules();

            // ReloadModules() only carries dump state for managed modules (engine
            // behaviour), so native dump results are preserved here as well.
            var previous = Modules
                .Where(m => m.DumpedPath != null)
                .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            Unhook();
            Modules.Clear();
            foreach (ModuleRecord module in modules.OrderByDescending(m => m.IsManaged)
                         .ThenBy(m => m.IsFramework)
                         .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (module.DumpedPath == null && previous.TryGetValue(module.Name, out ModuleRecord? old))
                {
                    module.Status = old.Status;
                    module.DumpedPath = old.DumpedPath;
                    module.Note = old.Note;
                }

                module.PropertyChanged += ModuleOnPropertyChanged;
                Modules.Add(module);
            }

            View.Refresh();
            RefreshCounts();
            Selected = null;
            SelectedCount = 0;
            Raise(nameof(TotalCount), nameof(ManagedCount), nameof(IsModuleListEmpty));
            _shell.Toast("Module map refreshed", $"{modules.Count} modules · {modules.Count(m => m.IsManaged)} managed", ToastKind.Info);
        }
        catch (Exception ex)
        {
            _shell.Log.Error("Rescan failed: " + ex.Message, "workspace");
            _shell.Toast("Rescan failed", ex.Message, ToastKind.Error);
        }
    }

    private bool FilterModule(object item)
    {
        if (item is not ModuleRecord module)
            return false;
        if (ShowManagedOnly && !module.IsManaged)
            return false;
        if (HideFramework && module.IsFramework)
            return false;
        if (ShowDumpedOnly && module.Status != DumpStatus.Dumped)
            return false;

        if (string.IsNullOrWhiteSpace(Search))
            return true;

        return module.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
            || (module.Path?.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false)
            || module.AddressLabel.Contains(Search, StringComparison.OrdinalIgnoreCase);
    }

    private void SelectAllVisible()
    {
        foreach (ModuleRecord module in View)
            module.IsSelected = true;
    }

    private void InvertSelection()
    {
        foreach (ModuleRecord module in Modules)
            module.IsSelected = !module.IsSelected;
    }

    private void ClearSelection()
    {
        foreach (ModuleRecord module in Modules)
            module.IsSelected = false;
    }

    private Task DumpAllAsync()
        => DumpAsync(Modules.Where(m => m.IsManaged && (!HideFramework || !m.IsFramework)));

    private Task DumpSelectedAsync()
        => DumpAsync(Modules.Where(m => m.IsSelected));

    private Task DumpNativeAsync()
        => DumpAsync(Modules.Where(m => m.IsSelected && !m.IsManaged));

    private Task DumpModuleAsync()
        => Selected != null ? DumpAsync(new[] { Selected }) : Task.CompletedTask;

    private async Task DumpAllModulesAsync()
    {
        if (_session == null || IsBusyOrDumping)
            return;

        DumpOptions options = _shell.SettingsStore.Current.ToDumpOptions();
        List<ModuleRecord> native = Modules.Where(m => !m.IsManaged && m.BaseAddress != 0).ToList();
        if (native.Count > 0 && !options.DumpNativeModules)
        {
            _shell.Toast("Native modules skipped",
                $"{native.Count} native module(s) exist. Enable the Native modules toggle (or Settings → Dumping) to include them.",
                ToastKind.Warning);
        }

        await DumpAsync(Modules.Where(m => m.IsManaged || options.DumpNativeModules));
    }

    private async Task DumpAsync(IEnumerable<ModuleRecord> source)
    {
        if (_session == null || IsBusyOrDumping)
            return;

        DumpOptions options = _shell.SettingsStore.Current.ToDumpOptions();
        var targets = source
            .Where(m => m.BaseAddress != 0)
            .Where(m => m.IsManaged || options.DumpNativeModules)
            .ToList();

        if (targets.Count == 0)
        {
            _shell.Toast("Nothing to dump", "Select at least one managed module (or enable native dumping in Settings).", ToastKind.Warning);
            return;
        }

        if (_shell.SettingsStore.Current.ConfirmOverwrite
            && options.OverwritePolicy != DumpOverwritePolicy.Rename
            && WouldOverwrite(targets, options, _session.Id))
        {
            bool overwrite = _shell.Confirm(
                "Overwrite dump files",
                $"The output folder already contains files matching this batch:\n{options.OutputDirectory}\n\n" +
                $"Continue and let the '{options.OverwritePolicy}' policy replace them?",
                "Overwrite",
                destructive: false);
            if (!overwrite)
                return;
        }

        var cts = new CancellationTokenSource();
        _dumpCts = cts;
        IsDumping = true;
        DumpMax = targets.Count;
        DumpProgress = 0;
        DumpStatusText = "Starting…";
        _shell.SetProgress(0, targets.Count);
        _shell.SetStatus($"Dumping {targets.Count} module(s) from {_session.Process.Name}…");

        try
        {
            var dumper = new ModuleDumper(_session, _shell.Log);
            var progress = new Progress<DumpProgress>(p =>
            {
                if (!IsDumping)
                    return;

                DumpProgress = p.Completed;
                DumpStatusText = $"{p.Completed}/{p.Total} · {p.Current}";
                _shell.SetProgress(p.Completed, p.Total);
                _shell.SetStatus($"Dumping {p.Current}…");
            });

            IReadOnlyList<DumpResult> results = await dumper.DumpAsync(targets, options, progress, cts.Token);

            _shell.NotifyDumped(results);
            RecordDumpSummary(results);
            RefreshCounts();

            if (Selected == null)
                Selected = targets.FirstOrDefault(m => m.Status == DumpStatus.Dumped);

            int ok = results.Count(r => r.Success);
            int failed = results.Count - ok;
            string folder = Path.GetFileName(options.OutputDirectory);
            _shell.SetStatus($"Dumped {ok}/{results.Count} module(s) → {(string.IsNullOrEmpty(folder) ? options.OutputDirectory : folder)}");
            DumpStatusText = failed == 0
                ? $"Done · {ok}/{results.Count}"
                : $"Done · {ok} ok, {failed} failed";

            if (results.Any(r => r.Success))
                _shell.Log.Success($"Dump finished: {ok}/{results.Count} module(s)", "dump");
        }
        catch (OperationCanceledException)
        {
            _shell.Log.Warn($"Dump cancelled after {DumpProgress:0}/{DumpMax:0} module(s).", "dump");
            _shell.Toast("Dump cancelled", "Modules that were already written are kept on disk.", ToastKind.Warning);
            _shell.SetStatus("Dump cancelled.");
            DumpStatusText = "Cancelled.";
        }
        catch (Exception ex)
        {
            _shell.Log.Error("Dump failed: " + ex.Message, "dump");
            _shell.Toast("Dump failed", ex.Message, ToastKind.Error);
            DumpStatusText = "Failed.";
        }
        finally
        {
            if (ReferenceEquals(_dumpCts, cts))
                _dumpCts = null;
            cts.Dispose();
            IsDumping = false;
            _shell.HideProgress();
        }
    }

    private void CancelDump()
    {
        try
        {
            _dumpCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The batch finished a moment ago.
        }
    }

    /// <summary>
    /// Best-effort prediction of whether the batch would replace an existing dump.
    /// The candidate paths come from the same template expansion the dump engine
    /// uses (<see cref="ModuleDumper.PredictOutputPaths"/>), so a custom
    /// <see cref="DumpOptions.FileNameTemplate"/> is covered by the confirmation.
    /// </summary>
    private static bool WouldOverwrite(IEnumerable<ModuleRecord> targets, DumpOptions options, int pid)
    {
        try
        {
            if (!Directory.Exists(options.OutputDirectory))
                return false;

            foreach (ModuleRecord module in targets)
            {
                foreach (string candidate in ModuleDumper.PredictOutputPaths(module, options, pid))
                {
                    if (File.Exists(candidate))
                        return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private async Task RunDeepDumpAsync()
    {
        if (_session == null || IsBusyOrDumping)
            return;

        _isDeepDumping = true;
        RaiseDumpCommandStates();
        Raise(nameof(IsBusyOrDumping));
        _shell.SetStatus("Running the managed agent inside the target…");

        try
        {
            var dumper = new DeepDumper(_shell.Log);
            var progress = new Progress<string>(message => _shell.SetStatus(message));
            DeepDumpResult result = await dumper.RunAsync(_session, _shell.SettingsStore.Current.ToDumpOptions(), progress);

            if (result.Success && result.Files.Count > 0)
            {
                foreach (DeepDumpedFile file in result.Files)
                    _shell.Inspector.AddCapture(file.Path, "deep");

                _shell.Toast("Deep dump captured", $"{result.Files.Count} module(s) from inside the target", ToastKind.Success);
                _shell.SetStatus($"Deep dump: {result.Files.Count} module(s)");
            }
            else
            {
                _shell.Log.Write(Dumper.Core.Diagnostics.LogLevel.Warning, result.Message, "deep");
                _shell.Toast("Deep dump unavailable", result.Message, ToastKind.Warning);
                _shell.SetStatus("Deep dump did not produce output.");
            }
        }
        catch (Exception ex)
        {
            _shell.Log.Error("Deep dump failed: " + ex.Message, "deep");
            _shell.Toast("Deep dump failed", ex.Message, ToastKind.Error);
        }
        finally
        {
            _isDeepDumping = false;
            RaiseDumpCommandStates();
            Raise(nameof(IsBusyOrDumping));
        }
    }

    private async Task InspectSelectedAsync()
    {
        ModuleRecord? module = Selected;
        if (module == null)
            return;

        if (module.DumpedPath == null)
            await DumpAsync(new[] { module });

        if (module.DumpedPath is { } path)
        {
            _shell.Inspector.AddCapture(path, "dumped");
            _shell.NavigateTo("inspector");
        }
        else
        {
            _shell.Toast("Nothing to inspect", "The module has no dump on disk yet.", ToastKind.Warning);
        }
    }

    private void RevealSelected()
    {
        if (Selected?.DumpedPath is not { } path)
            return;

        if (!File.Exists(path))
        {
            _shell.Toast("File not found", "The dump no longer exists at " + path, ToastKind.Warning);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex)
        {
            _shell.Toast("Could not open Explorer", ex.Message, ToastKind.Warning);
        }
    }

    private void OpenOutput()
    {
        string directory = _shell.SettingsStore.Current.EffectiveOutputDirectory();
        if (!Directory.Exists(directory))
        {
            _shell.Toast("Output folder missing", directory, ToastKind.Warning);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"\"{directory}\"");
        }
        catch (Exception ex)
        {
            _shell.Toast("Could not open Explorer", ex.Message, ToastKind.Warning);
        }
    }

    /// <summary>
    /// Opens the selected dump safely: a managed assembly is opened in the
    /// Inspector, anything else is only selected in Explorer. Dumped files are
    /// untrusted (often malware samples), so they are never handed to ShellExecute
    /// — the default action for an .exe is "run it".
    /// </summary>
    private void OpenDumpedFile()
    {
        if (Selected?.DumpedPath is not { } path)
            return;

        if (!File.Exists(path))
        {
            _shell.Toast("File not found", "The dump no longer exists at " + path, ToastKind.Warning);
            return;
        }

        try
        {
            if (AssemblyInspector.IsManagedFile(path))
            {
                _shell.Inspector.AddCapture(path, "dumped");
                _shell.NavigateTo("inspector");
                _shell.SetStatus("Opened the dump in the Inspector.");
                return;
            }

            // Native images cannot be previewed: reveal the file instead of
            // executing it.
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            _shell.SetStatus("Selected the dumped file in Explorer (dumped files are never run).");
        }
        catch (Exception ex)
        {
            _shell.Toast("Could not open the dump", ex.Message, ToastKind.Warning);
        }
    }

    private void OpenDumpedFolder()
    {
        if (Selected?.DumpedPath is not { } path)
            return;

        if (!File.Exists(path))
        {
            _shell.Toast("File not found", "The dump no longer exists at " + path, ToastKind.Warning);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex)
        {
            _shell.Toast("Could not open Explorer", ex.Message, ToastKind.Warning);
        }
    }

    private void CopyModulePath()
    {
        if (Selected == null)
            return;
        CopyText(Selected.DisplayPath, "Module path copied");
    }

    private void CopyModuleName()
    {
        if (Selected == null)
            return;
        CopyText(Selected.Name, "Module name copied");
    }

    private void CopyText(string text, string title)
    {
        try
        {
            Clipboard.SetText(text);
            _shell.Toast(title, text, ToastKind.Info);
        }
        catch (Exception ex)
        {
            _shell.Toast("Copy failed", ex.Message, ToastKind.Warning);
        }
    }

    private void ExportCsv()
        => ExportModules("Comma-separated values", "csv", "CSV file (*.csv)|*.csv",
            PageExports.ModulesCsv(View.Cast<ModuleRecord>()), utf8Bom: true);

    private void ExportJson()
        => ExportModules("JSON", "json", "JSON file (*.json)|*.json",
            PageExports.ModulesJson(View.Cast<ModuleRecord>()), utf8Bom: false);

    private void ExportModules(string format, string extension, string filter, string content, bool utf8Bom)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export module list as " + format,
            Filter = filter,
            DefaultExt = "." + extension,
            AddExtension = true,
            OverwritePrompt = true,
            FileName = PageExports.SuggestedFileName(
                "obsidian-modules-" + (_shell.Session?.Process.Name ?? "target"), extension),
            InitialDirectory = PageExports.SuggestedDirectory(_shell),
        };

        try
        {
            if (dialog.ShowDialog(Application.Current?.MainWindow) != true)
                return;

            PageExports.WriteText(dialog.FileName, content, utf8Bom);
            int count = View.Cast<object>().Count();
            _shell.Toast("Module list exported", $"{count} row(s) written to {Path.GetFileName(dialog.FileName)}", ToastKind.Success);
            _shell.Log.Success($"Exported {count} module row(s) to {dialog.FileName}", "workspace");
        }
        catch (Exception ex)
        {
            _shell.Log.Error("Module export failed: " + ex.Message, "workspace");
            _shell.Toast("Export failed", ex.Message, ToastKind.Error);
        }
    }

    private void OnSettingsChanged()
    {
        // The Settings page can toggle "include framework modules" while the
        // workspace is loaded; honour it without requiring a re-attach.
        bool hide = _shell.SettingsStore.Current.HideFrameworkModules;
        if (hide != HideFramework)
            HideFramework = hide;

        Raise(nameof(DumpNativeModules), nameof(RawImage), nameof(VerifyMetadata));
    }
}
