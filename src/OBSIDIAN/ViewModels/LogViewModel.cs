using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using Obsidian.Common;
using Obsidian.Dumper.Core.Diagnostics;
using Obsidian.Services;

namespace Obsidian.ViewModels;

public sealed class LogViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly LogBus _log;
    private readonly ViewRefreshDebouncer _searchDebouncer;
    private readonly List<LogEntry> _selectedEntries = new();
    private string _search = string.Empty;
    private string _level = "all";
    private bool _autoScroll = true;
    private bool _showTimestamps = true;
    private LogLevel _lastMinimumLevel = LogLevel.Trace;
    private int _unreadErrors;
    private int _unreadEntries;

    public LogViewModel(ShellViewModel shell, LogBus log)
    {
        _shell = shell;
        _log = log;
        Entries = new ObservableCollection<LogEntry>();
        View = CollectionViewSource.GetDefaultView(Entries);
        View.Filter = FilterEntry;

        foreach (LogEntry entry in log.Snapshot())
            Entries.Add(entry);

        log.EntryWritten += OnEntryWritten;

        ClearCommand = new RelayCommand(Clear);
        CopyCommand = new RelayCommand(Copy, () => !View.IsEmpty || _selectedEntries.Count > 0);
        ExportCommand = new RelayCommand(Export, () => !View.IsEmpty);
        SetLevelCommand = new RelayCommand(SetLevel);
        JumpToLatestCommand = new RelayCommand(JumpToLatest);
        RefreshCommand = new RelayCommand(() => View.Refresh());

        _showTimestamps = _shell.SettingsStore.Current.LogShowTimestamps;
        _autoScroll = _shell.SettingsStore.Current.LogAutoScroll;
        // The page owns the persisted minimum-level filter (AppSettings.LogMinLevel);
        // LogMinLevel is a *minimum* severity, matching FilterEntry below.
        _lastMinimumLevel = _shell.SettingsStore.Current.LogMinLevel;
        _level = MapMinimumLevel(_lastMinimumLevel);
        _shell.SettingsStore.Changed += OnSettingsChanged;

        _searchDebouncer = new ViewRefreshDebouncer(ApplySearchFilter);
    }

    public ObservableCollection<LogEntry> Entries { get; }
    public ICollectionView View { get; }

    public RelayCommand ClearCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand SetLevelCommand { get; }
    public RelayCommand JumpToLatestCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value))
                _searchDebouncer.Request();
        }
    }

    public string Level
    {
        get => _level;
        set
        {
            string normalized = string.IsNullOrWhiteSpace(value) ? "all" : value;
            if (string.Equals(_level, normalized, StringComparison.Ordinal))
                return;

            _level = normalized;
            RaiseLevelFlags();
            RefreshView();
        }
    }

    public bool IsAllLevel => Level == "all";
    public bool IsTraceLevel => Level == "trace";
    public bool IsInfoLevel => Level == "info";
    public bool IsWarningLevel => Level == "warning";
    public bool IsErrorLevel => Level == "error";

    public bool AutoScroll
    {
        get => _autoScroll;
        set
        {
            if (!Set(ref _autoScroll, value))
                return;

            if (value)
            {
                _unreadEntries = 0;
                Raise(nameof(UnreadEntries), nameof(HasUnreadEntries));
                EntryAdded?.Invoke(); // jump to the newest entry when follow mode is re-enabled
            }

            AppSettings settings = _shell.SettingsStore.Current;
            if (settings.LogAutoScroll != value)
            {
                settings.LogAutoScroll = value;
                _shell.SettingsStore.Save();
            }
        }
    }

    /// <summary>Activity row timestamps (persisted as <c>AppSettings.LogShowTimestamps</c>).</summary>
    public bool ShowTimestamps
    {
        get => _showTimestamps;
        set
        {
            if (!Set(ref _showTimestamps, value))
                return;

            AppSettings settings = _shell.SettingsStore.Current;
            if (settings.LogShowTimestamps != value)
            {
                settings.LogShowTimestamps = value;
                _shell.SettingsStore.Save();
            }
        }
    }

    public int ErrorCount => Entries.Count(e => e.Level == LogLevel.Error);
    public int WarningCount => Entries.Count(e => e.Level == LogLevel.Warning);
    public bool HasErrors => _unreadErrors > 0;

    public int UnreadEntries => _unreadEntries;
    public bool HasUnreadEntries => _unreadEntries > 0;
    public string UnreadLabel => _unreadEntries == 1 ? "1 new entry" : $"{_unreadEntries} new entries";

    public string CopyHint => _selectedEntries.Count > 0
        ? $"Copy the {_selectedEntries.Count} selected entry/entries to the clipboard"
        : "Copy the entries currently visible to the clipboard";

    public string CountLabel => $"{View.Cast<object>().Count()} of {Entries.Count} entries";

    /// <summary>Empty state: true when the log itself is empty, or when the filter hides everything.</summary>
    public bool IsViewEmpty => View.IsEmpty;

    public string EmptyStateTitle => Entries.Count == 0
        ? "No activity yet"
        : "No entries match this filter";

    public string EmptyStateHint => Entries.Count == 0
        ? "Engine, dump and agent messages appear here."
        : "Change the level filter or clear the search box.";

    public event Action? EntryAdded;

    private void OnEntryWritten(LogEntry entry)
    {
        // LogBus is written from worker threads (dump/deep-dump); mirror to the
        // collection only through the dispatcher.
        Application? app = Application.Current;
        if (app == null)
            return;

        try
        {
            app.Dispatcher.BeginInvoke(() =>
            {
                Entries.Add(entry);
                int max = Math.Clamp(_shell.SettingsStore.Current.MaxLogEntries, 500, 50_000);
                while (Entries.Count > max)
                    Entries.RemoveAt(0);

                if (entry.Level == LogLevel.Error && _shell.SelectedNav?.Page != this)
                    _unreadErrors++;

                RaiseLogMeta();
                if (AutoScroll)
                {
                    EntryAdded?.Invoke();
                }
                else
                {
                    _unreadEntries++;
                    Raise(nameof(UnreadEntries), nameof(HasUnreadEntries), nameof(UnreadLabel));
                }
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException)
        {
            // The application is shutting down; dropping the final UI update is fine.
        }
    }

    public void ClearUnread()
    {
        _unreadErrors = 0;
        Raise(nameof(HasErrors));
    }

    /// <summary>Called by the view whenever the ListBox selection changes.</summary>
    public void SetSelectedEntries(IEnumerable<LogEntry?> entries)
    {
        _selectedEntries.Clear();
        foreach (LogEntry? entry in entries)
        {
            if (entry != null)
                _selectedEntries.Add(entry);
        }

        Raise(nameof(CopyHint));
        CopyCommand.RaiseCanExecuteChanged();
    }

    private void JumpToLatest()
    {
        _unreadEntries = 0;
        Raise(nameof(UnreadEntries), nameof(HasUnreadEntries), nameof(UnreadLabel));
        AutoScroll = true;
    }

    private void OnSettingsChanged()
    {
        // Another surface can change the timestamp/follow preferences, the persisted
        // minimum level or lower the entry cap.
        LogLevel minimum = _shell.SettingsStore.Current.LogMinLevel;
        if (minimum != _lastMinimumLevel)
        {
            _lastMinimumLevel = minimum;
            Level = MapMinimumLevel(minimum);
        }

        bool showTimestamps = _shell.SettingsStore.Current.LogShowTimestamps;
        if (showTimestamps != _showTimestamps)
        {
            _showTimestamps = showTimestamps;
            Raise(nameof(ShowTimestamps));
        }

        bool autoScroll = _shell.SettingsStore.Current.LogAutoScroll;
        if (autoScroll != _autoScroll)
        {
            _autoScroll = autoScroll;
            Raise(nameof(AutoScroll));
        }

        int max = Math.Clamp(_shell.SettingsStore.Current.MaxLogEntries, 500, 50_000);
        bool trimmed = false;
        while (Entries.Count > max)
        {
            Entries.RemoveAt(0);
            trimmed = true;
        }

        if (trimmed)
            RaiseLogMeta();
    }

    /// <summary>
    /// Maps the persisted minimum severity (AppSettings.LogMinLevel) onto the page
    /// filter. Trace = everything; Info = everything except trace; Warning = warnings
    /// and errors; Error = errors only.
    /// </summary>
    private static string MapMinimumLevel(LogLevel level) => level switch
    {
        LogLevel.Info => "info",
        LogLevel.Warning => "warning",
        LogLevel.Error => "error",
        _ => "all",
    };

    private void SetLevel(object? parameter)
    {
        string value = parameter as string ?? "all";
        if (string.Equals(_level, value, StringComparison.Ordinal))
        {
            // Clicking the checked chip unchecks it locally before the command runs;
            // re-raise the flags so exactly one chip stays checked.
            RaiseLevelFlags();
            RefreshView();
            return;
        }

        Level = value;
    }

    private void RaiseLevelFlags()
    {
        Raise(nameof(Level), nameof(IsAllLevel), nameof(IsTraceLevel), nameof(IsInfoLevel),
            nameof(IsWarningLevel), nameof(IsErrorLevel));
    }

    private void ApplySearchFilter()
    {
        RefreshView();
        Raise(nameof(EmptyStateTitle), nameof(EmptyStateHint));
    }

    private void RefreshView()
    {
        View.Refresh();
        RaiseLogMeta();
    }

    private void RaiseLogMeta()
    {
        Raise(nameof(CountLabel), nameof(IsViewEmpty), nameof(EmptyStateTitle), nameof(EmptyStateHint),
            nameof(ErrorCount), nameof(WarningCount), nameof(HasErrors));
        CopyCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();
    }
    private bool FilterEntry(object item)
    {
        if (item is not LogEntry entry)
            return false;

        if (Level == "trace" && entry.Level != LogLevel.Trace)
            return false;
        // "info" and "warning" are minimum-level filters (AppSettings.LogMinLevel),
        // matching the persisted minimum-severity semantics: info shows everything
        // above trace; warning shows warnings and errors.
        if (Level == "info" && entry.Level == LogLevel.Trace)
            return false;
        if (Level == "warning" && entry.Level is not (LogLevel.Warning or LogLevel.Error))
            return false;
        if (Level == "error" && entry.Level != LogLevel.Error)
            return false;

        if (string.IsNullOrWhiteSpace(Search))
            return true;

        return entry.Message.Contains(Search, StringComparison.OrdinalIgnoreCase)
            || (entry.Scope?.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void Clear()
    {
        Entries.Clear();
        _log.Clear();
        _unreadErrors = 0;
        _unreadEntries = 0;
        _selectedEntries.Clear();
        _shell.Toast("Activity cleared", "The visible entries and the engine log buffer were cleared.", ToastKind.Info);
        RaiseLogMeta();
        Raise(nameof(UnreadEntries), nameof(HasUnreadEntries), nameof(UnreadLabel), nameof(CopyHint));
    }

    private void Copy()
    {
        List<string> lines = _selectedEntries.Count > 0
            ? _selectedEntries.Select(entry => entry.ToString()).ToList()
            : View.Cast<LogEntry>().Select(entry => entry.ToString()).ToList();

        if (lines.Count == 0)
        {
            _shell.Toast("Nothing to copy", "No entries match the current filter.", ToastKind.Info);
            return;
        }

        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, lines));
            _shell.Toast("Log copied", _selectedEntries.Count > 0
                ? $"{lines.Count} selected entries on the clipboard"
                : $"{lines.Count} visible entries on the clipboard", ToastKind.Info);
        }
        catch (Exception ex)
        {
            _shell.Toast("Copy failed", ex.Message, ToastKind.Warning);
        }
    }

    private void Export()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export activity log",
            Filter = "Log file (*.log)|*.log|Text file (*.txt)|*.txt",
            FileName = $"obsidian-log-{DateTime.Now:yyyyMMdd-HHmmss}.log",
            DefaultExt = ".log",
            AddExtension = true,
            OverwritePrompt = true,
        };

        try
        {
            if (dialog.ShowDialog(Application.Current?.MainWindow) != true)
                return;

            List<string> lines = View.Cast<LogEntry>().Select(e => e.ToString()).ToList();
            File.WriteAllLines(dialog.FileName, lines);
            _shell.Toast("Log exported", $"{lines.Count} entries written to {Path.GetFileName(dialog.FileName)}", ToastKind.Success);
        }
        catch (Exception ex)
        {
            _shell.Toast("Export failed", ex.Message, ToastKind.Error);
            _shell.Log.Error("Log export failed: " + ex.Message, "log");
        }
    }
}
