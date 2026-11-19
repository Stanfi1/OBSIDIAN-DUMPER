using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Obsidian.Common;
using Obsidian.Dumper.Core;
using Obsidian.Dumper.Core.Inspection;
using Obsidian.Dumper.Core.Model;
using Obsidian.Services;

namespace Obsidian.ViewModels;

/// <summary>
/// Coalesces rapid filter/search changes into a single <see cref="ICollectionView"/>
/// refresh. Refreshing on every keystroke is cheap for small lists but visibly janky
/// for a 400-module process list and a 10 000-type assembly, so all page view models
/// funnel text filters through this helper.
/// </summary>
internal sealed class ViewRefreshDebouncer
{
    private readonly Action _refresh;
    private readonly DispatcherTimer _timer;

    public ViewRefreshDebouncer(Action refresh, int delayMilliseconds = 180)
    {
        _refresh = refresh;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(30, delayMilliseconds)),
        };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _refresh();
        };
    }

    /// <summary>Schedule a refresh after the debounce interval.</summary>
    public void Request()
    {
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Refresh immediately (e.g. when a command needs the current view).</summary>
    public void Flush()
    {
        _timer.Stop();
        _refresh();
    }
}

/// <summary>
/// <summary>
/// File exporters shared by the Targets, Workspace, Inspector and Activity pages.
/// All helpers are pure (string in / string out) so they can be verified without the
/// UI; the commands only add the SaveFileDialog and error reporting around them.
/// </summary>
internal static class PageExports
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string SuggestedDirectory(ShellViewModel shell)
    {
        try
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents))
                documents = shell.SettingsStore.Current.EffectiveOutputDirectory();
            string directory = Path.Combine(documents, "OBSIDIAN", "exports");
            Directory.CreateDirectory(directory);
            return directory;
        }
        catch
        {
            return shell.SettingsStore.Current.EffectiveOutputDirectory();
        }
    }

    public static string SuggestedFileName(string stem, string extension)
        => $"{stem}-{DateTime.Now:yyyyMMdd-HHmmss}.{extension.TrimStart('.')}";

    public static void WriteText(string path, string content, bool utf8Bom = false)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(path, content, new UTF8Encoding(utf8Bom));
    }

    // ---------------------------------------------------------------- targets

    public static string TargetsCsv(IEnumerable<ProcessRecord> processes, Func<int, string?>? commandLineLookup = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("pid,name,display_name,image_path,architecture,runtime,integrity,elevated,session,working_set_bytes,start_time,company,file_version,clr,command_line");
        foreach (ProcessRecord process in processes)
        {
            var fields = new[]
            {
                process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                process.Name,
                process.DisplayName,
                process.ImagePath ?? string.Empty,
                process.ArchitectureLabel,
                process.RuntimeLabel,
                process.IntegrityLabel,
                process.IsElevated ? "yes" : "no",
                process.SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                process.WorkingSet?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                process.StartTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty,
                process.Company ?? string.Empty,
                process.FileVersion ?? string.Empty,
                process.Clr?.DisplayName ?? string.Empty,
                commandLineLookup?.Invoke(process.Id) ?? process.CommandLine ?? string.Empty,
            };

            builder.AppendLine(string.Join(",", fields.Select(EscapeCsv)));
        }

        return builder.ToString();
    }

    public static string TargetsJson(IEnumerable<ProcessRecord> processes, Func<int, string?>? commandLineLookup = null)
        => JsonSerializer.Serialize(processes.Select(process => new
        {
            pid = process.Id,
            name = process.Name,
            displayName = process.DisplayName,
            imagePath = process.ImagePath,
            architecture = process.ArchitectureLabel,
            runtime = process.RuntimeLabel,
            integrity = process.IntegrityLabel,
            elevated = process.IsElevated,
            session = process.SessionId,
            workingSetBytes = process.WorkingSet,
            startTime = process.StartTime?.ToString("O"),
            company = process.Company,
            fileVersion = process.FileVersion,
            clr = process.Clr?.DisplayName,
            commandLine = commandLineLookup?.Invoke(process.Id) ?? process.CommandLine,
        }), JsonOptions);

    // ---------------------------------------------------------------- workspace

    public static string ModulesCsv(IEnumerable<ModuleRecord> modules)
    {
        var builder = new StringBuilder();
        builder.AppendLine("name,path,base_address,size_bytes,managed,framework,in_memory,status,metadata,dumped_path,note");
        foreach (ModuleRecord module in modules)
        {
            var fields = new[]
            {
                module.Name,
                module.Path ?? string.Empty,
                module.AddressLabel,
                module.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
                module.IsManaged ? "yes" : "no",
                module.IsFramework ? "yes" : "no",
                module.IsInMemory ? "yes" : "no",
                module.Status.ToString(),
                module.MetadataLabel,
                module.DumpedPath ?? string.Empty,
                module.Note ?? string.Empty,
            };
            builder.AppendLine(string.Join(",", fields.Select(EscapeCsv)));
        }

        return builder.ToString();
    }

    public static string ModulesJson(IEnumerable<ModuleRecord> modules)
        => JsonSerializer.Serialize(modules.Select(module => new
        {
            name = module.Name,
            path = module.Path,
            baseAddress = module.BaseAddress,
            sizeBytes = module.Size,
            managed = module.IsManaged,
            framework = module.IsFramework,
            inMemory = module.IsInMemory,
            status = module.Status.ToString(),
            metadata = module.MetadataLabel,
            dumpedPath = module.DumpedPath,
            note = module.Note,
        }), JsonOptions);

    // ---------------------------------------------------------------- inspector

    public static string InspectorMarkdown(
        AssemblySummary summary,
        IEnumerable<TypeEntry> types,
        IEnumerable<StringEntry> strings)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# " + (string.IsNullOrWhiteSpace(summary.FullName) ? summary.Name : summary.FullName));
        builder.AppendLine();
        builder.AppendLine("| Field | Value |");
        builder.AppendLine("|---|---|");
        void Row(string name, string? value) => builder.AppendLine($"| {name} | {EscapeMarkdown(value)} |");
        Row("Name", summary.Name);
        Row("Version", summary.Version);
        Row("Kind", summary.Kind);
        Row("Machine", summary.Machine);
        Row("Runtime", summary.RuntimeVersion);
        Row("Target framework", summary.TargetFramework);
        Row("Entry point", summary.EntryPoint);
        Row("Strong named", summary.IsStrongNamed ? "yes" : "no");
        Row("IL only", summary.IsIlOnly ? "yes" : "no");
        Row("MVID", summary.Mvid);
        Row("Compile timestamp", summary.CompileTimestamp?.ToString("yyyy-MM-dd HH:mm:ss") ?? "deterministic");
        Row("Company", summary.Company);
        Row("Source path", summary.SourcePath);
        Row("SHA-256", summary.Sha256);
        builder.AppendLine();
        builder.AppendLine("## Metrics");
        builder.AppendLine();
        builder.AppendLine("| Types | Methods | Fields | Properties | Events | Namespaces | Resources | Assembly refs | IL bytes | Size |");
        builder.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        builder.AppendLine($"| {summary.TypeCount} | {summary.MethodCount} | {summary.FieldCount} | {summary.PropertyCount} | {summary.EventCount} | {summary.NamespaceCount} | {summary.ResourceCount} | {summary.AssemblyRefCount} | {summary.IlBytes} | {summary.SizeLabel} |");
        builder.AppendLine();

        if (summary.Sections.Count > 0)
        {
            builder.AppendLine("## Sections");
            builder.AppendLine();
            builder.AppendLine("| Name | Virtual size | Raw size | Flags | Entropy |");
            builder.AppendLine("|---|---|---|---|---|");
            foreach (PeSectionInfo section in summary.Sections)
                builder.AppendLine($"| {EscapeMarkdown(section.Name)} | 0x{section.VirtualSize:X} | {section.SizeLabel} | {section.FlagsLabel} | {section.EntropyLabel} |");
            builder.AppendLine();
        }

        if (summary.Protections.Count > 0)
        {
            builder.AppendLine("## Protection fingerprints");
            builder.AppendLine();
            builder.AppendLine("| Hint | Severity | Evidence |");
            builder.AppendLine("|---|---|---|");
            foreach (ProtectionHint protection in summary.Protections)
                builder.AppendLine($"| {EscapeMarkdown(protection.Name)} | {protection.SeverityLabel} | {EscapeMarkdown(protection.Evidence)} |");
            builder.AppendLine();
        }

        if (summary.References.Count > 0)
        {
            builder.AppendLine("## References");
            builder.AppendLine();
            builder.AppendLine("| Assembly | Version | Culture | Kind |");
            builder.AppendLine("|---|---|---|---|");
            foreach (ReferenceEntry reference in summary.References)
                builder.AppendLine($"| {EscapeMarkdown(reference.Name)} | {EscapeMarkdown(reference.Version)} | {EscapeMarkdown(reference.Culture)} | {reference.Kind} |");
            builder.AppendLine();
        }

        List<TypeEntry> typeList = types.ToList();
        if (typeList.Count > 0)
        {
            builder.AppendLine("## Types");
            builder.AppendLine();
            builder.AppendLine("| Type | Kind | Methods | Fields |");
            builder.AppendLine("|---|---|---|---|");
            foreach (TypeEntry type in typeList)
                builder.AppendLine($"| `{EscapeMarkdown(type.FullName)}` | {type.KindLabel} | {type.MethodCount} | {type.FieldCount} |");
            builder.AppendLine();
        }

        List<StringEntry> stringList = strings.ToList();
        builder.AppendLine("## Literal strings");
        builder.AppendLine();
        builder.AppendLine($"{stringList.Count} unique literal(s) extracted.");
        builder.AppendLine();
        int limit = Math.Min(stringList.Count, 500);
        for (int i = 0; i < limit; i++)
            builder.AppendLine("- `" + EscapeMarkdown(stringList[i].Display) + "` (" + stringList[i].Kind + ")");
        if (stringList.Count > limit)
            builder.AppendLine($"- … {stringList.Count - limit} more (export JSON for the full list)");

        return builder.ToString();
    }

    public static string InspectorJson(
        AssemblySummary summary,
        IEnumerable<TypeEntry> types,
        IEnumerable<StringEntry> strings)
        => JsonSerializer.Serialize(new
        {
            summary = new
            {
                summary.Name,
                summary.FullName,
                summary.Version,
                summary.Culture,
                summary.PublicKeyToken,
                summary.ModuleName,
                summary.RuntimeVersion,
                summary.Kind,
                summary.EntryPoint,
                summary.TargetFramework,
                summary.IsStrongNamed,
                summary.IsIlOnly,
                summary.IsMixedMode,
                summary.IsReadyToRun,
                summary.Machine,
                summary.Subsystem,
                compileTimestamp = summary.CompileTimestamp?.ToString("O"),
                summary.Mvid,
                summary.Company,
                summary.Product,
                summary.Description,
                summary.Copyright,
                summary.FileVersion,
                summary.TypeCount,
                summary.MethodCount,
                summary.FieldCount,
                summary.PropertyCount,
                summary.EventCount,
                summary.NamespaceCount,
                summary.ResourceCount,
                summary.AssemblyRefCount,
                summary.TypeRefCount,
                summary.MemberRefCount,
                summary.CustomAttributeCount,
                summary.IlBytes,
                summary.FileSize,
                summary.Sha256,
                summary.SourcePath,
            },
            sections = summary.Sections.Select(section => new
            {
                section.Name,
                section.VirtualAddress,
                section.VirtualSize,
                section.RawSize,
                section.Characteristics,
                section.Entropy,
                section.FlagsLabel,
            }),
            protections = summary.Protections.Select(protection => new
            {
                protection.Name,
                protection.Description,
                severity = protection.Severity.ToString(),
                protection.Evidence,
            }),
            references = summary.References.Select(reference => new
            {
                reference.Name,
                reference.Version,
                reference.Culture,
                reference.PublicKeyToken,
                reference.Kind,
                reference.UsageCount,
            }),
            resources = summary.Resources.Select(resource => new
            {
                resource.Name,
                resource.Size,
                resource.Kind,
                resource.IsPublic,
            }),
            types = types.Select(type => new
            {
                type.FullName,
                type.Namespace,
                type.Name,
                kind = type.Kind.ToString(),
                type.Visibility,
                type.MethodCount,
                type.FieldCount,
                type.PropertyCount,
                type.NestedLevel,
                type.Token,
                type.IsCompilerGenerated,
                type.IsObfuscated,
                methods = type.Methods.Select(method => new
                {
                    method.Name,
                    method.Signature,
                    method.Token,
                    method.Rva,
                    method.IlSize,
                    method.HasBody,
                    method.Flags,
                }),
            }),
            strings = strings.Select(text => new
            {
                text.Value,
                text.Kind,
                text.Count,
                text.Length,
            }),
        }, JsonOptions);

    // ---------------------------------------------------------------- helpers

    private static string EscapeCsv(string value)
    {
        if (value.Length == 0)
            return string.Empty;
        bool quote = value.IndexOfAny([',', '"', '\r', '\n']) >= 0;
        return quote ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    private static string EscapeMarkdown(string? value)
            => (value ?? string.Empty).Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}

public sealed class TargetsViewModel : ObservableObject
{
    private static readonly int[] AutoRefreshIntervals = [2000, 5000, 10000, 30000];
    private static readonly (string Column, string Property, bool Ascending)[] SortColumns =
    [
        ("Pid", nameof(ProcessRecord.Id), true),
        ("Process", nameof(ProcessRecord.DisplayName), true),
        ("Runtime", nameof(ProcessRecord.RuntimeLabel), true),
        ("Arch", nameof(ProcessRecord.ArchitectureLabel), true),
        ("Integrity", nameof(ProcessRecord.IntegrityLabel), true),
        ("WorkingSet", nameof(ProcessRecord.WorkingSet), false),
        ("Started", nameof(ProcessRecord.StartTime), false),
    ];

    private readonly ShellViewModel _shell;
    private readonly ViewRefreshDebouncer _searchDebouncer;
    private readonly DispatcherTimer _autoRefreshTimer;
    private string _search = string.Empty;
    private string _filter = "all";
    private ProcessRecord? _selected;
    private string? _selectedCommandLine;
    private bool _isScanning;
    private string _scanSummary = "not scanned yet";
    private string _scanStatusText = string.Empty;
    private string _scanError = string.Empty;
    private int _scanTotal;
    private int _managedCount;
    private int _elevatedCount;
    private int _x86Count;
    private int _x64Count;
    private string _lastCompletedScan = "not scanned yet";
    private string _sortColumn = "Runtime";
    private bool _sortAscending = true;
    private int _autoRefreshIntervalMs;
    private readonly Dictionary<int, string?> _commandLineCache = new();
    private CancellationTokenSource? _prefetchCts;
    private CancellationTokenSource? _scanCts;

    public TargetsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Processes = new ObservableCollection<ProcessRecord>();
        View = CollectionViewSource.GetDefaultView(Processes);
        View.Filter = FilterProcess;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsScanning);
        AttachCommand = new AsyncRelayCommand(AttachSelectedAsync, () => Selected != null && Selected.CanRead);
        AttachAndDumpAllCommand = new AsyncRelayCommand(AttachAndDumpAllAsync, () => CanControlSelected);
        CancelScanCommand = new RelayCommand(CancelScan, () => IsScanning);
        SetFilterCommand = new RelayCommand(SetFilter);
        CopyPathCommand = new RelayCommand(CopyPath, () => Selected?.ImagePath != null);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => Selected?.ImagePath != null);
        CopyPidCommand = new RelayCommand(CopyPid, () => Selected != null);
        CopyNameCommand = new RelayCommand(CopyName, () => Selected != null);
        CopyCommandLineCommand = new RelayCommand(CopyCommandLine, () => !string.IsNullOrWhiteSpace(SelectedCommandLine));
        SuspendProcessCommand = new RelayCommand(SuspendSelected, () => CanControlSelected);
        ResumeProcessCommand = new RelayCommand(ResumeSelected, () => CanControlSelected);
        TerminateProcessCommand = new RelayCommand(TerminateSelected, () => CanControlSelected);
        SortByCommand = new RelayCommand(parameter => SortBy(parameter as string));
        ToggleAutoRefreshCommand = new RelayCommand(ToggleAutoRefresh);
        CycleAutoRefreshCommand = new RelayCommand(CycleAutoRefresh);
        ExportCsvCommand = new RelayCommand(ExportCsv, () => !View.IsEmpty);
        ExportJsonCommand = new RelayCommand(ExportJson, () => !View.IsEmpty);

        _searchDebouncer = new ViewRefreshDebouncer(ApplySearchFilter);

        ApplySort();

        _autoRefreshTimer = new DispatcherTimer(DispatcherPriority.Background);
        _autoRefreshTimer.Tick += (_, _) => OnAutoRefreshTick();
        SetAutoRefreshInterval(_shell.SettingsStore.Current.AutoRefreshIntervalMs, persist: false);
        _shell.PropertyChanged += OnShellPropertyChanged;
        _shell.SettingsStore.Changed += OnSettingsChanged;

        _ = SafeInitialScanAsync();
    }

    public ObservableCollection<ProcessRecord> Processes { get; }
    public ICollectionView View { get; }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand AttachCommand { get; }
    public AsyncRelayCommand AttachAndDumpAllCommand { get; }
    public RelayCommand CancelScanCommand { get; }
    public RelayCommand SetFilterCommand { get; }
    public RelayCommand CopyPathCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand CopyPidCommand { get; }
    public RelayCommand CopyNameCommand { get; }
    public RelayCommand CopyCommandLineCommand { get; }
    public RelayCommand SuspendProcessCommand { get; }
    public RelayCommand ResumeProcessCommand { get; }
    public RelayCommand TerminateProcessCommand { get; }
    public RelayCommand SortByCommand { get; }
    public RelayCommand ToggleAutoRefreshCommand { get; }
    public RelayCommand CycleAutoRefreshCommand { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand ExportJsonCommand { get; }

    // ------------------------------------------------------------------ filters

    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value))
                _searchDebouncer.Request();
        }
    }

    public string Filter
    {
        get => _filter;
        set
        {
            string normalized = string.IsNullOrWhiteSpace(value) ? "all" : value;
            if (string.Equals(_filter, normalized, StringComparison.Ordinal))
                return;

            _filter = normalized;
            RaiseFilterFlags();
            RefreshView();
        }
    }

    public bool IsAllFilter => Filter == "all";
    public bool IsManagedFilter => Filter == "managed";
    public bool IsX86Filter => Filter == "x86";
    public bool IsX64Filter => Filter == "x64";
    public bool IsElevatedFilter => Filter == "elevated";

    // ------------------------------------------------------------------ selection

    public ProcessRecord? Selected
    {
        get => _selected;
        set
        {
            // ProcessRecord is a record: default equality compares values, so a
            // rescan would consider the fresh instance "the same" and never raise
            // SelectedChanged (the ListBox then points at an object that is no longer
            // in the list). Selection is deliberately reference-identity based.
            if (ReferenceEquals(_selected, value))
                return;

            _selected = value;
            _selectedCommandLine = null;
            if (value != null && TryGetCachedCommandLine(value.Id, out string? cached))
                _selectedCommandLine = cached;
            Raise(nameof(Selected), nameof(HasSelection), nameof(SelectedCommandLine),
                nameof(SelectedAccessText), nameof(AttachHint), nameof(SelectedStartText),
                nameof(CanControlSelected), nameof(SelectedWorkingSetText), nameof(SelectedRuntimeText),
                nameof(SelectedElevatedText));
            RaiseSelectionCommands();

            if (value != null && value.CommandLine == null)
                _ = LoadCommandLineAsync(value);
        }
    }

    public bool HasSelection => Selected != null;
    public string SelectedCommandLine => _selectedCommandLine
        ?? (Selected != null && TryGetCachedCommandLine(Selected.Id, out string? cached) ? cached : null)
        ?? Selected?.CommandLine
        ?? "not available";
    public string SelectedAccessText => Selected == null
        ? string.Empty
        : Selected.CanRead ? "readable" : Selected.AccessProblem ?? "not readable";
    public string AttachHint => Selected == null
        ? "Select a process first."
        : Selected.CanRead
            ? "Attach, map its modules and switch to the Workspace page."
            : SelectedAccessText;
    public string SelectedStartText => Selected?.StartTime is { } start
        ? start.ToString("yyyy-MM-dd HH:mm:ss")
        : "—";
    public string SelectedWorkingSetText => Selected?.SizeLabel ?? "—";
    public string SelectedRuntimeText => Selected?.RuntimeLabel ?? "—";
    public string SelectedElevatedText => Selected == null ? "—" : Selected.IsElevated ? "yes" : "no";

    /// <summary>Process-control commands are allowed for live, non-self processes; the
    /// engine reports access-denied honestly instead of the UI guessing.</summary>
    public bool CanControlSelected => Selected != null
        && Selected.Id > 4
        && Selected.Id != Environment.ProcessId;

    /// <summary>Mirrors <c>AppSettings.ShowIntegrity</c> (integrity column visibility).</summary>
    public bool ShowIntegrityColumn => _shell.SettingsStore.Current.ShowIntegrity;

    /// <summary>Mirrors <c>AppSettings.HighlightElevated</c> (amber elevated marker).</summary>
    public bool HighlightElevated => _shell.SettingsStore.Current.HighlightElevated;

    // ------------------------------------------------------------------ scanning

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!Set(ref _isScanning, value))
                return;

            RefreshCommand.RaiseCanExecuteChanged();
            CancelScanCommand.RaiseCanExecuteChanged();
            Raise(nameof(ShowEmptyState), nameof(ShowScanningState), nameof(EmptyStateTitle), nameof(EmptyStateHint),
                nameof(ScanStatusText), nameof(IsAutoRefreshActive));
        }
    }

    /// <summary>"scanned 14:32:07" / "not scanned yet" caption under the process total.</summary>
    public string ScanSummary
    {
        get => _scanSummary;
        private set => Set(ref _scanSummary, value);
    }

    /// <summary>Live progress line in the toolbar; also used by the empty scanning state.</summary>
    public string ScanStatusText
    {
        get => _scanStatusText;
        private set => Set(ref _scanStatusText, value);
    }

    public string ScanError
    {
        get => _scanError;
        private set
        {
            if (Set(ref _scanError, value))
                Raise(nameof(HasScanError));
        }
    }

    public bool HasScanError => !string.IsNullOrEmpty(_scanError);

    public int TotalCount => _scanTotal;

    public int ManagedCount
    {
        get => _managedCount;
        private set => Set(ref _managedCount, value);
    }

    public int ElevatedCount
    {
        get => _elevatedCount;
        private set => Set(ref _elevatedCount, value);
    }

    public int X86Count
    {
        get => _x86Count;
        private set => Set(ref _x86Count, value);
    }

    public int X64Count
    {
        get => _x64Count;
        private set => Set(ref _x64Count, value);
    }

    public string VisibleCountLabel => View.Cast<object>().Count().ToString();

    /// <summary>Empty-state panel: only when a completed scan/filter produced no rows.</summary>
    public bool ShowEmptyState => !IsScanning && View.IsEmpty;

    /// <summary>Initial scan feedback: no rows yet but the scan is running.</summary>
    public bool ShowScanningState => IsScanning && View.IsEmpty;

    public string EmptyStateTitle => HasFilterOrSearch
        ? "No processes match this view"
        : "No processes found";

    public string EmptyStateHint => HasFilterOrSearch
        ? "Adjust the filters or rescan the machine."
        : "Run a rescan to enumerate the machine again.";

    private bool HasFilterOrSearch => Filter != "all" || !string.IsNullOrWhiteSpace(Search);

    // ------------------------------------------------------------------ sorting

    public string PidHeader => Header("PID", "Pid");
    public string ProcessHeader => Header("PROCESS", "Process");
    public string RuntimeHeader => Header("RUNTIME", "Runtime");
    public string ArchHeader => Header("ARCH", "Arch");
    public string IntegrityHeader => Header("INTEGRITY", "Integrity");
    public string WorkingSetHeader => Header("WORKING SET", "WorkingSet");
    public string StartedHeader => Header("STARTED", "Started");

    private string Header(string label, string column)
        => string.Equals(_sortColumn, column, StringComparison.Ordinal)
            ? label + (_sortAscending ? "  ▲" : "  ▼")
            : label;

    private void SortBy(string? column)
    {
        if (string.IsNullOrEmpty(column))
            return;

        if (string.Equals(_sortColumn, column, StringComparison.Ordinal))
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortColumn = column;
            _sortAscending = SortColumns.FirstOrDefault(s => s.Column == column).Ascending;
        }

        ApplySort();
    }

    private void ApplySort()
    {
        (string Column, string Property, bool Ascending) definition =
            SortColumns.FirstOrDefault(s => s.Column == _sortColumn, SortColumns[2]);
        if (definition.Column == null)
            definition = SortColumns[2];

        ProcessRecord? keep = _selected;
        View.SortDescriptions.Clear();
        View.SortDescriptions.Add(new SortDescription(definition.Property,
            _sortAscending ? ListSortDirection.Ascending : ListSortDirection.Descending));

        if (keep != null && View.Contains(keep))
        {
            if (!ReferenceEquals(_selected, keep))
                Selected = keep;
        }
        else if (keep != null)
        {
            Selected = null;
        }

        Raise(nameof(PidHeader), nameof(ProcessHeader), nameof(RuntimeHeader), nameof(ArchHeader),
            nameof(IntegrityHeader), nameof(WorkingSetHeader), nameof(StartedHeader),
            nameof(VisibleCountLabel), nameof(ShowEmptyState));
    }

    // ------------------------------------------------------------------ auto refresh

    public bool IsAutoRefreshEnabled => _autoRefreshIntervalMs > 0;

    public bool IsAutoRefreshActive => IsAutoRefreshEnabled && !IsScanning
        && _shell.SelectedNav?.Page is TargetsViewModel;

    public string AutoRefreshLabel => _autoRefreshIntervalMs switch
    {
        <= 0 => "off",
        < 1000 => $"{_autoRefreshIntervalMs} ms",
        _ => $"{_autoRefreshIntervalMs / 1000} s",
    };

    public string AutoRefreshHint => IsAutoRefreshEnabled
        ? $"The process list rescans every {AutoRefreshLabel} while this page is open. Click to change the interval."
        : "Enable automatic rescans (2 s / 5 s / 10 s / 30 s).";

    private void ToggleAutoRefresh()
    {
        SetAutoRefreshInterval(IsAutoRefreshEnabled ? 0 : 5000);
    }

    private void CycleAutoRefresh()
    {
        int index = Array.IndexOf(AutoRefreshIntervals, _autoRefreshIntervalMs);
        SetAutoRefreshInterval(index >= 0 && index + 1 < AutoRefreshIntervals.Length
            ? AutoRefreshIntervals[index + 1]
            : AutoRefreshIntervals[0]);
    }

    private void SetAutoRefreshInterval(int milliseconds, bool persist = true)
    {
        if (milliseconds < 0)
            milliseconds = 0;
        if (milliseconds > 0 && Array.IndexOf(AutoRefreshIntervals, milliseconds) < 0)
            milliseconds = 5000;

        _autoRefreshIntervalMs = milliseconds;
        if (milliseconds > 0)
            _autoRefreshTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        UpdateAutoRefreshTimer();

        if (persist)
        {
            AppSettings settings = _shell.SettingsStore.Current;
            if (settings.AutoRefreshIntervalMs != milliseconds)
            {
                settings.AutoRefreshIntervalMs = milliseconds;
                _shell.SettingsStore.Save();
            }
        }

        Raise(nameof(IsAutoRefreshEnabled), nameof(IsAutoRefreshActive), nameof(AutoRefreshLabel), nameof(AutoRefreshHint));
    }

    private void UpdateAutoRefreshTimer()
    {
        bool shouldRun = _autoRefreshIntervalMs > 0
            && _shell.SelectedNav?.Page is TargetsViewModel
            && !IsScanning;
        if (shouldRun)
        {
            _autoRefreshTimer.Interval = TimeSpan.FromMilliseconds(_autoRefreshIntervalMs);
            _autoRefreshTimer.Start();
        }
        else
        {
            _autoRefreshTimer.Stop();
        }
    }

    private void OnAutoRefreshTick()
    {
        if (!IsAutoRefreshEnabled || IsScanning)
            return;
        if (_shell.SelectedNav?.Page is not TargetsViewModel)
            return;
        _ = RefreshAsync();
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.SelectedNav))
            UpdateAutoRefreshTimer();
    }

    private void OnSettingsChanged()
    {
        int interval = _shell.SettingsStore.Current.AutoRefreshIntervalMs;
        if (interval != _autoRefreshIntervalMs)
            SetAutoRefreshInterval(interval, persist: false);

        Raise(nameof(ShowIntegrityColumn), nameof(HighlightElevated));
        RefreshView();
    }

    // ------------------------------------------------------------------ lazy command lines

    private bool TryGetCachedCommandLine(int processId, out string? commandLine)
    {
        lock (_commandLineCache)
            return _commandLineCache.TryGetValue(processId, out commandLine);
    }

    private string? CachedCommandLineLookup(int processId)
        => TryGetCachedCommandLine(processId, out string? commandLine) ? commandLine : null;

    /// <summary>
    /// Honours <c>AppSettings.LazyCommandLine = false</c>: resolve command lines for the
    /// whole scanned list in the background (four at a time, PEB reads only). The UI
    /// only re-renders when the selected process is among the resolved entries.
    /// </summary>
    private void StartCommandLinePrefetch(IReadOnlyList<ProcessRecord> records)
    {
        _prefetchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _prefetchCts = cts;
        _ = PrefetchCommandLinesAsync(records, cts);
    }

    private async Task PrefetchCommandLinesAsync(
        IReadOnlyList<ProcessRecord> records,
        CancellationTokenSource cts)
    {
        try
        {
            using var gate = new SemaphoreSlim(4);
            var tasks = new List<Task>(records.Count);
            foreach (ProcessRecord process in records)
            {
                if (cts.IsCancellationRequested)
                    break;
                if (!process.CanRead || process.CommandLine != null || TryGetCachedCommandLine(process.Id, out _))
                    continue;

                await gate.WaitAsync(cts.Token);
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        using var remote = RemoteProcess.TryOpen(process.Id, out _);
                        string? line = remote?.TryReadCommandLine();
                        if (string.IsNullOrWhiteSpace(line))
                            return;

                        lock (_commandLineCache)
                            _commandLineCache[process.Id] = line;

                        if (process.Id == Selected?.Id)
                        {
                            Application.Current?.Dispatcher.BeginInvoke(
                                () => Raise(nameof(SelectedCommandLine)));
                        }
                    }
                    catch
                    {
                        // The process may exit or be protected; the lazy path reports the
                        // same condition when the row is selected.
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, cts.Token));
            }

            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // A new scan superseded the prefetch.
        }
        catch (Exception ex)
        {
            _shell.Log.Warn("Command-line prefetch stopped: " + ex.Message, "scan");
        }
    }

    // ------------------------------------------------------------------ scanning implementation

    public async Task RefreshAsync()
    {
        if (IsScanning)
            return;

        _scanCts?.Dispose();
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        _prefetchCts?.Cancel();

        IsScanning = true;
        ScanError = string.Empty;
        ScanStatusText = "Scanning…";
        ScanSummary = "scanning…";
        _shell.SetStatus("Scanning processes…");

        try
        {
            var progress = new Progress<int>(done =>
            {
                if (cts.IsCancellationRequested)
                    return;
                ScanStatusText = $"Scanning… {done} processes";
            });

            (IReadOnlyList<ProcessRecord>? records, string error) = await Task.Run(() =>
            {
                try
                {
                    return (ProcessDiscovery.Enumerate(cts.Token, progress), string.Empty);
                }
                catch (OperationCanceledException)
                {
                    return ((IReadOnlyList<ProcessRecord>?)null, string.Empty);
                }
                catch (Exception ex)
                {
                    return ((IReadOnlyList<ProcessRecord>?)null, ex.Message);
                }
            });

            if (cts.IsCancellationRequested)
            {
                ScanStatusText = "Scan cancelled.";
                ScanSummary = _lastCompletedScan;
                return;
            }

            if (records == null)
            {
                ScanError = string.IsNullOrWhiteSpace(error) ? "The process scan failed." : error;
                ScanStatusText = "Scan failed — keeping the previous list.";
                ScanSummary = _scanTotal > 0 ? $"scan failed · last {_lastCompletedScan}" : "scan failed";
                _shell.Log.Error("Process scan failed: " + ScanError, "scan");
                _shell.Toast("Scan failed", ScanError, ToastKind.Error);
                return;
            }

            ReplaceProcesses(records);

            // Preserve the selection across the rescan: the collection now holds
            // fresh ProcessRecord instances, so match on the PID rather than on
            // reference/value equality.
            int? keepPid = _selected?.Id;
            ProcessRecord? restored = keepPid is int id
                ? Processes.FirstOrDefault(p => p.Id == id)
                : null;
            Selected = restored;

            _scanTotal = records.Count;
            ManagedCount = records.Count(p => p.IsManaged);
            ElevatedCount = records.Count(p => p.IsElevated);
            X86Count = records.Count(p => p.Architecture == ProcessArchitecture.X86);
            X64Count = records.Count(p => p.Architecture == ProcessArchitecture.X64);
            Raise(nameof(TotalCount));

            ScanSummary = "scanned " + DateTime.Now.ToString("HH:mm:ss");
            _lastCompletedScan = ScanSummary;
            ScanStatusText = $"{records.Count} processes · {ManagedCount} managed CLR hosts";
            ScanError = string.Empty;

            RefreshView();

            if (!_shell.SettingsStore.Current.LazyCommandLine)
                StartCommandLinePrefetch(records);

            _shell.Log.Info($"Scan complete: {records.Count} processes, {ManagedCount} managed", "scan");
            _shell.SetStatus($"{records.Count} processes · {ManagedCount} managed CLR hosts");
        }
        catch (Exception ex)
        {
            ScanError = ex.Message;
            ScanStatusText = "Scan failed.";
            _shell.Log.Error("Process scan failed: " + ex.Message, "scan");
            _shell.Toast("Scan failed", ex.Message, ToastKind.Error);
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cts))
                _scanCts = null;
            cts.Dispose();
            IsScanning = false;
            UpdateAutoRefreshTimer();
        }
    }

    private async Task SafeInitialScanAsync()
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            // An unobserved exception from the fire-and-forget startup scan would
            // otherwise only surface on the finalizer thread.
            _shell.Log.Error("Startup scan failed: " + ex.Message, "scan");
        }
    }

    private void ReplaceProcesses(IReadOnlyList<ProcessRecord> records)
    {
        // Keep the same ObservableCollection instance so the ICollectionView and the
        // ListBox binding survive a refresh.
        Processes.Clear();
        foreach (ProcessRecord record in records)
            Processes.Add(record);
    }

    private void SetFilter(object? parameter)
    {
        string value = parameter as string ?? "all";
        if (string.Equals(_filter, value, StringComparison.Ordinal))
        {
            // Clicking the already active chip must not leave every chip unchecked:
            // WPF writes the local IsChecked before the command runs, so re-raise the
            // flags to push the bound state back.
            RaiseFilterFlags();
            RefreshView();
            return;
        }

        Filter = value;
    }

    private void RaiseFilterFlags()
    {
        Raise(nameof(Filter), nameof(IsAllFilter), nameof(IsManagedFilter), nameof(IsX86Filter),
            nameof(IsX64Filter), nameof(IsElevatedFilter));
    }

    private void ApplySearchFilter()
    {
        RefreshView();
        Raise(nameof(EmptyStateTitle), nameof(EmptyStateHint));
    }

    /// <summary>
    /// Refresh the filtered view without losing the selected process: WPF clears the
    /// ListBox selection when a CollectionView raises Reset, and the clear travels back
    /// through the TwoWay SelectedItem binding.
    /// </summary>
    private void RefreshView()
    {
        ProcessRecord? keep = _selected;
        View.Refresh();

        if (keep != null && View.Contains(keep))
        {
            // Use the property setter: the ListBox may have pushed null through the
            // TwoWay SelectedItem binding during View.Refresh(), which also cleared
            // the lazily loaded command line.
            if (!ReferenceEquals(_selected, keep))
                Selected = keep;
        }
        else if (keep != null)
        {
            Selected = null;
        }

        Raise(nameof(VisibleCountLabel), nameof(ShowEmptyState), nameof(ShowScanningState),
            nameof(EmptyStateTitle), nameof(EmptyStateHint));
        ExportCsvCommand.RaiseCanExecuteChanged();
        ExportJsonCommand.RaiseCanExecuteChanged();
    }

    private void RaiseSelectionCommands()
    {
        AttachCommand.RaiseCanExecuteChanged();
        AttachAndDumpAllCommand.RaiseCanExecuteChanged();
        CopyPathCommand.RaiseCanExecuteChanged();
        OpenFolderCommand.RaiseCanExecuteChanged();
        CopyPidCommand.RaiseCanExecuteChanged();
        CopyNameCommand.RaiseCanExecuteChanged();
        CopyCommandLineCommand.RaiseCanExecuteChanged();
        SuspendProcessCommand.RaiseCanExecuteChanged();
        ResumeProcessCommand.RaiseCanExecuteChanged();
        TerminateProcessCommand.RaiseCanExecuteChanged();
    }

    private void CancelScan()
    {
        try
        {
            _scanCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The scan finished between the click and this call.
        }
    }

    private bool FilterProcess(object item)
    {
        if (item is not ProcessRecord process)
            return false;

        if (!_shell.SettingsStore.Current.ShowInaccessibleProcesses && !process.CanRead)
            return false;
        if (Filter == "managed" && !process.IsManaged)
            return false;
        if (Filter == "x86" && process.Architecture != ProcessArchitecture.X86)
            return false;
        if (Filter == "x64" && process.Architecture != ProcessArchitecture.X64)
            return false;
        if (Filter == "elevated" && !process.IsElevated)
            return false;

        if (string.IsNullOrWhiteSpace(Search))
            return true;

        return process.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
            || process.Id.ToString().Contains(Search, StringComparison.Ordinal)
            || (process.ImagePath?.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false)
            || (process.Company?.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false)
            || (process.Clr?.DisplayName.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    // ------------------------------------------------------------------ actions

    private async Task AttachSelectedAsync()
    {
        if (Selected != null)
            await _shell.AttachAsync(Selected);
    }

    private async Task AttachAndDumpAllAsync()
    {
        ProcessRecord? target = Selected;
        if (target == null)
            return;

        if (_shell.Session?.Id != target.Id)
            await _shell.AttachAsync(target);

        if (_shell.Session?.Id != target.Id)
            return;

        _shell.NavigateTo("workspace");
        await Task.Delay(150);
        if (_shell.Workspace.DumpAllCommand.CanExecute(null))
            _shell.Workspace.DumpAllCommand.Execute(null);
    }

    private void SuspendSelected()
    {
        if (Selected is not { } target || !CanControlSelected)
            return;

        if (ProcessControl.TrySuspend(target.Id, out string? error))
        {
            _shell.Log.Info($"Suspended {target.DisplayName} (pid {target.Id})", "process");
            _shell.Toast("Process suspended", $"{target.DisplayName} (pid {target.Id}) is paused.", ToastKind.Warning);
            _shell.SetStatus($"Suspended {target.DisplayName} (pid {target.Id}).");
        }
        else
        {
            _shell.Log.Error($"Suspend failed for pid {target.Id}: {error}", "process");
            _shell.Toast("Suspend failed", error ?? "The process could not be suspended.", ToastKind.Error);
        }
    }

    private void ResumeSelected()
    {
        if (Selected is not { } target || !CanControlSelected)
            return;

        if (ProcessControl.TryResume(target.Id, out string? error))
        {
            _shell.Log.Info($"Resumed {target.DisplayName} (pid {target.Id})", "process");
            _shell.Toast("Process resumed", $"{target.DisplayName} (pid {target.Id}) is running again.", ToastKind.Success);
            _shell.SetStatus($"Resumed {target.DisplayName} (pid {target.Id}).");
        }
        else
        {
            _shell.Log.Error($"Resume failed for pid {target.Id}: {error}", "process");
            _shell.Toast("Resume failed", error ?? "The process could not be resumed.", ToastKind.Error);
        }
    }

    private void TerminateSelected()
    {
        if (Selected is not { } target || !CanControlSelected)
            return;

        bool confirm = _shell.SettingsStore.Current.ConfirmDestructiveActions;
        if (confirm && !_shell.Confirm(
                "Terminate process",
                $"Terminate {target.DisplayName} (pid {target.Id})?\n\nUnsaved work in the target is lost and a graceful shutdown is not attempted.",
                "Terminate",
                destructive: true))
        {
            return;
        }

        if (ProcessControl.TryTerminate(target.Id, out string? error))
        {
            _shell.Log.Warn($"Terminated {target.DisplayName} (pid {target.Id})", "process");
            _shell.Toast("Process terminated", $"{target.DisplayName} (pid {target.Id}) was terminated.", ToastKind.Warning);
            _shell.SetStatus($"Terminated {target.DisplayName} (pid {target.Id}).");
            _ = RefreshAfterTerminateAsync(target.Id);
        }
        else
        {
            _shell.Log.Error($"Terminate failed for pid {target.Id}: {error}", "process");
            _shell.Toast("Terminate failed", error ?? "The process could not be terminated.", ToastKind.Error);
        }
    }

    private async Task RefreshAfterTerminateAsync(int terminatedPid)
    {
        if (_shell.Session?.Id == terminatedPid)
            _shell.DetachCommand.Execute(null);

        await Task.Delay(350);
        await RefreshAsync();
    }

    /// <summary>Reads the command line lazily: it needs a PEB read, which is too
    /// expensive for every row during a scan.</summary>
    private async Task LoadCommandLineAsync(ProcessRecord process)
    {
        string? line = await Task.Run(() =>
        {
            try
            {
                using var remote = RemoteProcess.TryOpen(process.Id, out _);
                return remote?.TryReadCommandLine();
            }
            catch
            {
                return null;
            }
        });

        if (Selected?.Id != process.Id)
            return;

        _selectedCommandLine = string.IsNullOrWhiteSpace(line) ? null : line;
        if (!string.IsNullOrWhiteSpace(line))
        {
            lock (_commandLineCache)
                _commandLineCache[process.Id] = line;
        }

        Raise(nameof(SelectedCommandLine));
    }

    private void CopyPath()
    {
        if (Selected?.ImagePath is not { } path)
            return;
        CopyText(path, "Path copied");
    }

    private void CopyPid()
    {
        if (Selected is { } process)
            CopyText(process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), "PID copied");
    }

    private void CopyName()
    {
        if (Selected is { } process)
            CopyText(process.DisplayName, "Process name copied");
    }

    private void CopyCommandLine()
    {
        if (!string.IsNullOrWhiteSpace(SelectedCommandLine))
            CopyText(SelectedCommandLine, "Command line copied");
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

    private void OpenFolder()
    {
        if (Selected?.ImagePath is not { } path)
            return;

        ExpandFileLocation(path);
    }

    private void ExpandFileLocation(string path)
    {
        if (!File.Exists(path))
        {
            _shell.Toast("File not found", "The executable is no longer on disk at " + path, ToastKind.Warning);
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

    // ------------------------------------------------------------------ export

    private void ExportCsv()
    {
        Export("Comma-separated values", "csv", "CSV file (*.csv)|*.csv",
            PageExports.TargetsCsv(View.Cast<ProcessRecord>(), CachedCommandLineLookup), utf8Bom: true);
    }

    private void ExportJson()
    {
        Export("JSON", "json", "JSON file (*.json)|*.json",
            PageExports.TargetsJson(View.Cast<ProcessRecord>(), CachedCommandLineLookup), utf8Bom: false);
    }

    private void Export(string format, string extension, string filter, string content, bool utf8Bom)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export processes as " + format,
            Filter = filter,
            DefaultExt = "." + extension,
            AddExtension = true,
            OverwritePrompt = true,
            FileName = PageExports.SuggestedFileName("obsidian-targets", extension),
            InitialDirectory = PageExports.SuggestedDirectory(_shell),
        };

        try
        {
            if (dialog.ShowDialog(Application.Current?.MainWindow) != true)
                return;

            PageExports.WriteText(dialog.FileName, content, utf8Bom);
            int count = View.Cast<object>().Count();
            _shell.Toast("Processes exported", $"{count} row(s) written to {Path.GetFileName(dialog.FileName)}", ToastKind.Success);
            _shell.Log.Success($"Exported {count} process row(s) to {dialog.FileName}", "targets");
        }
        catch (Exception ex)
        {
            _shell.Log.Error("Process export failed: " + ex.Message, "targets");
            _shell.Toast("Export failed", ex.Message, ToastKind.Error);
        }
    }
}
