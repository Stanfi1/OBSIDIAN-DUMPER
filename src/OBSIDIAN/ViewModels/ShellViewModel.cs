using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Obsidian.Common;
using Obsidian.Dumper.Core;
using Obsidian.Dumper.Core.Diagnostics;
using Obsidian.Dumper.Core.Model;
using Obsidian.Services;

namespace Obsidian.ViewModels;

public sealed class NavItem : ObservableObject
{
    private string _badge = string.Empty;

    public NavItem(string key, string title, string subtitle, string glyph, object page)
    {
        Key = key;
        Title = title;
        Subtitle = subtitle;
        Glyph = glyph;
        Page = page;
    }

    public string Key { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string Glyph { get; }
    public object Page { get; }

    public string Badge
    {
        get => _badge;
        set
        {
            if (Set(ref _badge, value))
                Raise(nameof(HasBadge));
        }
    }

    public bool HasBadge => !string.IsNullOrEmpty(Badge);
}

public enum ToastKind
{
    Success,
    Info,
    Warning,
    Error,
}

public sealed class Toast : ObservableObject
{
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public ToastKind Kind { get; init; }

    public string Glyph => Kind switch
    {
        ToastKind.Success => "\uE73E",
        ToastKind.Warning => "\uE7BA",
        ToastKind.Error => "\uE783",
        _ => "\uE946",
    };

    /// <summary>Palette token carrying the semantic colour for this toast kind.</summary>
    public string AccentToken => Kind switch
    {
        ToastKind.Success => "Green",
        ToastKind.Warning => "Amber",
        ToastKind.Error => "Red",
        _ => "Cyan",
    };

    /// <summary>
    /// Resolved theme brush (not a literal colour) so toasts follow dark/light
    /// and the accent presets; the shell refreshes it on ThemeChanged.
    /// </summary>
    public Brush Accent => ThemeService.GetBrush(AccentToken) ?? Brushes.Gray;

    internal void RefreshTheme() => Raise(nameof(Accent));
}

/// <summary>
/// Shell state: navigation, the attached target, global busy state and the
/// toast queue. Every page view model is created here so they can talk to each
/// other without a container.
/// </summary>
public sealed class ShellViewModel : ObservableObject
{
    private const int MaxVisibleToasts = 4;
    private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(6);

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _clock;
    private readonly Dictionary<Toast, DispatcherTimer> _toastTimers = new();
    private readonly NavItem _workspaceNav;
    private readonly NavItem _injectNav;
    private NavItem? _selectedNav;
    private TargetSession? _session;
    private DateTime _attachedAt;
    private bool _isBusy;
    private string _statusText = "Ready. Select a process to begin.";
    private bool _isProgressVisible;
    private double _progressValue;
    private double _progressMax = 1;
    private string _clockText = DateTime.Now.ToString("HH:mm:ss");
    private string _targetName = "No target";
    private string _targetRuntime = "—";
    private string _targetPid = "—";
    private string _targetArch = "—";
    private string _targetIntegrity = "—";
    private string _targetPath = string.Empty;
    private string _targetCommandLine = string.Empty;
    private string _sessionUptime = "—";

    public ShellViewModel()
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        SettingsStore = new SettingsStore();
        Log = new LogBus(SettingsStore.Current.MaxLogEntries);

        LogPage = new LogViewModel(this, Log);
        SettingsPage = new SettingsViewModel(this, SettingsStore);
        Targets = new TargetsViewModel(this);
        Workspace = new WorkspaceViewModel(this);
        Inspector = new InspectorViewModel(this);
        InjectPage = new InjectViewModel(this);
        MemoryPage = new MemoryViewModel(this);

        _workspaceNav = new NavItem("workspace", "Workspace", "Attached process modules", "\uE8B7", Workspace);
        _injectNav = new NavItem("inject", "Injection", "Load a library into the target", "\uE896", InjectPage);

        Navigation = new ObservableCollection<NavItem>
        {
            new("targets", "Targets", "Live process inventory", "\uE8FD", Targets),
            _workspaceNav,
            new("inspector", "Inspector", "Assemblies and decompiled code", "\uE721", Inspector),
            _injectNav,
            new("memory", "Memory", "Address space, scanner and runtime", "\uE950", MemoryPage),
            new("log", "Activity", "Engine log", "\uE7C3", LogPage),
            new("settings", "Settings", "Appearance, output and behaviour", "\uE713", SettingsPage),
        };

        SelectedNav = Navigation[0];

        RefreshCommand = new AsyncRelayCommand(RefreshCurrentPageAsync);
        DetachCommand = new RelayCommand(Detach, () => _session != null);
        ClearToastsCommand = new RelayCommand(ClearToasts, () => Toasts.Count > 0);
        OpenSettingsCommand = new RelayCommand(() => NavigateTo("settings"));
        OpenActivityCommand = new RelayCommand(() => NavigateTo("log"));
        GoToWorkspaceCommand = new RelayCommand(() => NavigateTo("workspace"));
        GoToInjectionCommand = new RelayCommand(() => NavigateTo("inject"));

        _clock = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _clock.Tick += (_, _) => OnClockTick();
        _clock.Start();

        Log.Info($"{AppInfo.ProductTitle} {AppInfo.Version} initialised", "core");
        Log.Info($"Output directory: {SettingsStore.Current.EffectiveOutputDirectory()}", "core");
        if (!string.IsNullOrEmpty(SettingsStore.LoadError))
            Log.Warn("Settings could not be loaded (" + SettingsStore.LoadError + "); defaults restored", "settings");

        // Apply the persisted Activity preferences to the log page view model.
        ApplyLogLevelFilter(SettingsStore.Current.LogMinLevel);
        LogPage.AutoScroll = SettingsStore.Current.LogAutoScroll;

        // Restore the last filters only when the operator asked for it.
        if (SettingsStore.Current.RememberFilters)
        {
            if (!string.IsNullOrEmpty(SettingsStore.Current.LastTargetsFilter))
                Targets.Filter = SettingsStore.Current.LastTargetsFilter!;
            if (!string.IsNullOrEmpty(SettingsStore.Current.LastTargetsSearch))
                Targets.Search = SettingsStore.Current.LastTargetsSearch!;
            if (!string.IsNullOrEmpty(SettingsStore.Current.LastWorkspaceSearch))
                Workspace.Search = SettingsStore.Current.LastWorkspaceSearch!;
            if (SettingsStore.Current.LastSelectedPid > 0)
                _ = RestoreLastSelectionAsync(SettingsStore.Current.LastSelectedPid);
        }

        SettingsStore.SaveFailed += OnSettingsSaveFailed;
        SettingsStore.Changed += OnSettingsStoreChanged;
        ThemeService.ThemeChanged += OnThemeChanged;
    }

    public LogBus Log { get; }
    public SettingsStore SettingsStore { get; }
    public TargetsViewModel Targets { get; }
    public WorkspaceViewModel Workspace { get; }
    public InspectorViewModel Inspector { get; }
    public InjectViewModel InjectPage { get; }
    public MemoryViewModel MemoryPage { get; }
    public LogViewModel LogPage { get; }
    public SettingsViewModel SettingsPage { get; }

    public ObservableCollection<NavItem> Navigation { get; }
    public ObservableCollection<Toast> Toasts { get; } = new();

    public string VersionText => AppInfo.VersionLabel;
    public string BuildLabel => AppInfo.ProductName + " // DUMPER";
    public string DnlibVersion => AppInfo.DnlibVersion;
    public string DecompilerVersion => AppInfo.DecompilerVersion;
    public string RuntimeLabel => AppInfo.RuntimeLabel;

    /// <summary>Toggles the ENGINE block in the sidebar (Settings → Appearance).</summary>
    public bool ShowEngineVersions => SettingsStore.Current.ShowEngineVersions;

    public void RefreshEngineVersionsVisibility() => Raise(nameof(ShowEngineVersions));

    public string WindowTitle => HasTarget
        ? $"{AppInfo.ProductTitle} — {TargetName} (pid {TargetPid})"
        : AppInfo.ProductTitle;

    public NavItem? SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (!Set(ref _selectedNav, value))
                return;

            Raise(nameof(CurrentPage), nameof(PageTitle), nameof(PageSubtitle));
            if (value?.Page is LogViewModel log)
                log.ClearUnread();
            if (value?.Page is InspectorViewModel inspector)
                inspector.ReloadCommand.RaiseCanExecuteChanged();
        }
    }

    public object? CurrentPage => _selectedNav?.Page;
    public string PageTitle => _selectedNav?.Title ?? "Targets";
    public string PageSubtitle => _selectedNav?.Subtitle ?? string.Empty;

    public string ClockText
    {
        get => _clockText;
        private set => Set(ref _clockText, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public bool IsProgressVisible
    {
        get => _isProgressVisible;
        private set => Set(ref _isProgressVisible, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => Set(ref _progressValue, value);
    }

    public double ProgressMax
    {
        get => _progressMax;
        private set => Set(ref _progressMax, value);
    }

    public bool HasTarget => _session != null;
    public TargetSession? Session => _session;

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

    public string SessionUptime
    {
        get => _sessionUptime;
        private set => Set(ref _sessionUptime, value);
    }

    public RelayCommand DetachCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand ClearToastsCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand OpenActivityCommand { get; }
    public RelayCommand GoToWorkspaceCommand { get; }
    public RelayCommand GoToInjectionCommand { get; }

    public void NavigateTo(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        NavItem? item = Navigation.FirstOrDefault(n =>
            string.Equals(n.Key, key, StringComparison.OrdinalIgnoreCase));
        if (item != null)
            SelectedNav = item;
    }

    private async Task RefreshCurrentPageAsync()
    {
        switch (_selectedNav?.Page)
        {
            case TargetsViewModel targets:
                await targets.RefreshAsync();
                break;
            case WorkspaceViewModel workspace:
                workspace.Rescan();
                break;
            case LogViewModel log:
                log.RefreshCommand.Execute(null);
                break;
            case SettingsViewModel settings:
                settings.RefreshAll();
                break;
        }
    }

    public async Task AttachAsync(ProcessRecord process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (IsBusy)
        {
            Toast("Busy", "Another operation is already running.", ToastKind.Info);
            return;
        }

        IsBusy = true;
        StatusText = $"Attaching to {process.Name} (pid {process.Id})…";
        try
        {
            (TargetSession? session, string? error) = await Task.Run(() =>
            {
                TargetSession? attached = TargetSession.Attach(process.Id, out string? attachError);
                return (attached, attachError);
            });

            if (session == null)
            {
                Log.Error($"Attach to pid {process.Id} failed: {error}", "attach");
                Toast("Attach failed", error ?? "The process could not be attached.", ToastKind.Error);
                StatusText = "Attach failed.";
                return;
            }

            TargetSession? previous = _session;
            _session = session;
            _attachedAt = DateTime.Now;
            MemoryPage.OnSessionChanged();
            if (previous != null)
            {
                try
                {
                    previous.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warn("Previous session did not shut down cleanly: " + ex.Message, "attach");
                }
            }

            TargetName = session.Process.DisplayName;
            TargetRuntime = session.Process.RuntimeLabel;
            TargetPid = session.Id.ToString();
            TargetArch = session.Process.ArchitectureLabel;
            TargetIntegrity = process.IntegrityLabel;
            TargetPath = session.Process.ImagePath ?? "—";
            TargetCommandLine = session.CommandLine ?? "—";
            SessionUptime = "00:00:00";

            Workspace.Load(session);
            Inspector.SeedFromSession(session);
            InjectPage.Refresh();

            Raise(nameof(HasTarget), nameof(Session), nameof(WindowTitle));
            DetachCommand.RaiseCanExecuteChanged();
            _workspaceNav.Badge = session.Modules.Count(m => m.IsManaged).ToString();

            Log.Success($"Attached to {session.Process.Name} (pid {session.Id}) · {session.Clr?.DisplayName ?? "native"}", "attach");
            NavigateTo("workspace");
            Toast("Target attached", $"{session.Process.Name} · {session.Clr?.DisplayName ?? "native"} · {session.ManagedModules.Count} managed modules", ToastKind.Success);
            StatusText = $"Attached to {session.Process.Name}.";
        }
        catch (Exception ex)
        {
            Log.Error("Attach failed: " + ex.Message, "attach");
            Toast("Attach failed", ex.Message, ToastKind.Error);
            StatusText = "Attach failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Detach()
    {
        TargetSession? session = _session;
        if (session == null)
            return;

        // The page still needs the live session to resolve the target pid, so the
        // automatic unload must run before the shell clears its own state.
        if (SettingsStore.Current.AutoUnloadOnDetach)
        {
            try
            {
                StatusText = "Unloading injected modules…";
                InjectPage.UnloadAllOnDetach();
            }
            catch (Exception ex)
            {
                Log.Warn("Automatic unload on detach failed: " + ex.Message, "inject");
            }
        }

        _session = null;
        _attachedAt = default;
        MemoryPage.OnSessionChanged();
        Log.Info($"Detached from {session.Process.Name}", "attach");
        try
        {
            session.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Session did not shut down cleanly: " + ex.Message, "attach");
        }

        Workspace.Clear();
        Inspector.ClearSession();
        InjectPage.Refresh();

        TargetName = "No target";
        TargetRuntime = "—";
        TargetPid = "—";
        TargetArch = "—";
        TargetIntegrity = "—";
        TargetPath = string.Empty;
        TargetCommandLine = string.Empty;
        SessionUptime = "—";
        HideProgress();

        Raise(nameof(HasTarget), nameof(Session), nameof(WindowTitle));
        DetachCommand.RaiseCanExecuteChanged();
        _workspaceNav.Badge = string.Empty;
        NavigateTo("targets");
        StatusText = "Detached.";
    }

    public void SetStatus(string text) => StatusText = text;

    /// <summary>
    /// Maps the persisted minimum log level onto the Activity page filter. The
    /// page filter uses minimum-severity semantics: "info" keeps everything but
    /// Trace, "warning" keeps warnings and errors, "error" only errors.
    /// </summary>
    public void ApplyLogLevelFilter(LogLevel level) => LogPage.Level = level switch
    {
        LogLevel.Error => "error",
        LogLevel.Warning => "warning",
        LogLevel.Info or LogLevel.Success => "info",
        _ => "all",
    };

    private async Task RestoreLastSelectionAsync(int pid)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            ProcessRecord? process = Targets.Processes.FirstOrDefault(p => p.Id == pid);
            if (process != null)
            {
                Targets.Selected = process;
                return;
            }

            await Task.Delay(150);
        }
    }

    public void SetProgress(double value, double max)
    {
        ProgressMax = Math.Max(1, max);
        ProgressValue = Math.Clamp(value, 0, ProgressMax);
        IsProgressVisible = true;
    }

    public void HideProgress() => IsProgressVisible = false;

    public void Toast(string title, string message, ToastKind kind = ToastKind.Info)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => Toast(title, message, kind));
            return;
        }

        var toast = new Toast { Title = title, Message = message, Kind = kind };
        Toasts.Insert(0, toast);
        while (Toasts.Count > MaxVisibleToasts)
            DismissToast(Toasts[^1]);

        var timer = new DispatcherTimer(ToastDuration, DispatcherPriority.Background, (_, _) => DismissToast(toast), _dispatcher);
        _toastTimers[toast] = timer;
        timer.Start();
    }

    /// <summary>Removes a toast and stops its expiry timer (dismiss or timeout).</summary>
    public void DismissToast(Toast toast)
    {
        if (_toastTimers.Remove(toast, out DispatcherTimer? timer))
            timer.Stop();
        Toasts.Remove(toast);
    }

    public void ClearToasts()
    {
        foreach (DispatcherTimer timer in _toastTimers.Values)
            timer.Stop();
        _toastTimers.Clear();
        Toasts.Clear();
    }

    /// <summary>
    /// Asks the operator to confirm an action. <paramref name="confirmLabel"/>
    /// is reserved for a future in-app dialog; the stock Windows prompt only
    /// offers OK/Cancel, which is why the primary button reads "OK".
    /// </summary>
    public bool Confirm(string title, string message, string confirmLabel = "Continue", bool destructive = false)
    {
        _ = confirmLabel;
        MessageBoxResult result = MessageBox.Show(
            Application.Current?.MainWindow,
            message,
            title,
            MessageBoxButton.OKCancel,
            destructive ? MessageBoxImage.Warning : MessageBoxImage.Question,
            MessageBoxResult.Cancel);

        bool confirmed = result == MessageBoxResult.OK;
        Log.Info($"{title}: {(confirmed ? "confirmed" : "cancelled by the operator")}", "ui");
        return confirmed;
    }

    public void NotifyDumped(IReadOnlyList<DumpResult> results)
    {
        var successful = results.Where(r => r.Success).ToList();
        var failed = results.Where(r => !r.Success).ToList();

        if (successful.Count > 0)
        {
            foreach (string path in successful.Select(r => r.OutputPath).Where(p => p != null)!)
                Inspector.AddCapture(path!, "dumped");

            string folder = Path.GetFileName(SettingsStore.Current.EffectiveOutputDirectory());
            Toast("Dump complete",
                $"{successful.Count} module(s) written to {(string.IsNullOrEmpty(folder) ? "the output folder" : folder)}",
                ToastKind.Success);
        }

        if (failed.Count > 0)
            Toast("Dump issues", $"{failed.Count} module(s) could not be dumped. See Activity.", ToastKind.Warning);

        if (successful.Count > 0 && SettingsStore.Current.AutoOpenOutputFolder)
        {
            try
            {
                System.Diagnostics.Process.Start("explorer.exe",
                    $"\"{SettingsStore.Current.EffectiveOutputDirectory()}\"");
            }
            catch (Exception ex)
            {
                Log.Warn("Could not open the output folder: " + ex.Message, "dump");
            }
        }
    }

    /// <summary>
    /// Stops background timers and releases the attached session. Called by
    /// <see cref="App"/> on exit so no timer or toast continuation survives the
    /// window (they would otherwise post to a shut-down dispatcher).
    /// </summary>
    public void Shutdown()
    {
        try
        {
            _clock.Stop();
        }
        catch
        {
            // Shutdown is best effort.
        }

        ClearToasts();
        SettingsStore.SaveFailed -= OnSettingsSaveFailed;
        SettingsStore.Changed -= OnSettingsStoreChanged;
        ThemeService.ThemeChanged -= OnThemeChanged;

        // Remember the page filters (Settings → Processes → Remember filters).
        if (SettingsStore.Current.RememberFilters)
        {
            SettingsStore.Current.LastTargetsFilter = Targets.Filter;
            SettingsStore.Current.LastTargetsSearch = Targets.Search;
            SettingsStore.Current.LastWorkspaceSearch = Workspace.Search;
            SettingsStore.Current.LastSelectedPid = Targets.Selected?.Id ?? 0;
        }

        TargetSession? session = _session;
        _session = null;
        if (session != null)
        {
            try
            {
                session.Dispose();
            }
            catch
            {
                // Shutdown is best effort.
            }
        }

        SettingsStore.Save();
    }

    private void OnClockTick()
    {
        ClockText = DateTime.Now.ToString("HH:mm:ss");
        if (_session == null || _attachedAt == default)
        {
            if (!string.Equals(SessionUptime, "—", StringComparison.Ordinal))
                SessionUptime = "—";
            return;
        }

        TimeSpan uptime = DateTime.Now - _attachedAt;
        if (uptime < TimeSpan.Zero)
            uptime = TimeSpan.Zero;
        SessionUptime = $"{(int)uptime.TotalHours:00}:{uptime.Minutes:00}:{uptime.Seconds:00}";
    }

    private void OnSettingsSaveFailed(string error)
        => Toast("Settings not saved", error, ToastKind.Warning);

    private void OnSettingsStoreChanged() => Raise(nameof(ShowEngineVersions));

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        foreach (Toast toast in Toasts)
            toast.RefreshTheme();
    }
}
