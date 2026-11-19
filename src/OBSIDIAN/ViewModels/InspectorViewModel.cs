using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;
using Obsidian.Common;
using Obsidian.Dumper.Core;
using Obsidian.Dumper.Core.Inspection;
using Obsidian.Dumper.Core.Model;
using Obsidian.Dumper.Core.PE;
using PeSectionInfo = Obsidian.Dumper.Core.PE.PeSectionInfo;
using Obsidian.Dumper.Core.Resources;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Obsidian.ViewModels;

public sealed class CapturedAssembly : ObservableObject
{
    private int _typeCount;
    private int _methodCount;
    private long _size;
    private bool _isLoaded;
    private bool _isActive;

    public string Path { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Origin { get; init; } = "dumped";
    public DateTime CapturedAt { get; init; } = DateTime.Now;

    public string OriginLabel => Origin switch
    {
        "deep" => "deep dump",
        "file" => "opened",
        _ => "memory dump",
    };

    public long Size
    {
        get => _size;
        internal set
        {
            if (Set(ref _size, value))
                Raise(nameof(Detail));
        }
    }

    public int TypeCount
    {
        get => _typeCount;
        private set
        {
            if (Set(ref _typeCount, value))
                Raise(nameof(Detail));
        }
    }

    public int MethodCount
    {
        get => _methodCount;
        private set => Set(ref _methodCount, value);
    }

    public bool IsLoaded
    {
        get => _isLoaded;
        private set => Set(ref _isLoaded, value);
    }

    /// <summary>True for the capture currently displayed by the inspector (chip checked state).</summary>
    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    public string Filename => System.IO.Path.GetFileName(Path);

    public string Detail => _typeCount > 0
        ? $"{OriginLabel} · {_typeCount} types · {AssemblySummary.FormatBytes(_size)}"
        : $"{OriginLabel} · {AssemblySummary.FormatBytes(_size)}";

    public void Apply(AssemblySummary summary)
    {
        Size = summary.FileSize;
        TypeCount = summary.TypeCount;
        MethodCount = summary.MethodCount;
        IsLoaded = true;
    }
}

/// <summary>
/// Assembly exploration: metadata, type/method tree, IL, decompiled C#, strings,
/// references, sections and protector fingerprints.
/// </summary>
public sealed class InspectorViewModel : ObservableObject
{
    /// <summary>
    /// Decode width for large-but-allowed resource images. The preview is shown at
    /// a few hundred pixels, so decoding a 4000 px wide bitmap at full resolution
    /// only wastes memory. The hard dimension/byte guard lives in ResourceExtractor.
    /// </summary>
    private const int MaxResourcePreviewDecodeWidth = 1024;

    private readonly ShellViewModel _shell;
    private readonly ViewRefreshDebouncer _typeFilterDebouncer;
    private readonly ViewRefreshDebouncer _stringFilterDebouncer;
    private readonly ViewRefreshDebouncer _codeSearchDebouncer;
    private AssemblyInspector? _inspector;
    private CapturedAssembly? _selectedCapture;
    private AssemblySummary? _summary;
    private byte[]? _fileBytes;
    private PeHeaderInfo? _peHeader;
    private byte[]? _hexBytes;
    private ResourceEntry? _selectedResource;
    private ResourceContent? _resourceContent;
    private ImageSource? _resourceImage;
    private TypeEntry? _selectedType;
    private MethodEntry? _selectedMethod;
    private string _sourceCode = string.Empty;
    private string _ilCode = string.Empty;
    private string _typeFilter = string.Empty;
    private string _stringFilter = string.Empty;
    private string? _errorMessage;
    private bool _isLoading;
    private bool _isDecompiling;
    private bool _showShortStrings;
    private bool _showNestedTypes = true;
    private string _codeSearchInput = string.Empty;
    private string _codeSearchTerm = string.Empty;
    private int _selectedTab;
    private int _loadGeneration;

    public InspectorViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Captures = new ObservableCollection<CapturedAssembly>();
        Types = new ObservableCollection<TypeEntry>();
        Methods = new ObservableCollection<MethodEntry>();
        Strings = new ObservableCollection<StringEntry>();

        TypesView = CollectionViewSource.GetDefaultView(Types);
        TypesView.Filter = FilterType;
        TypesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TypeEntry.NamespaceLabel)));

        StringsView = CollectionViewSource.GetDefaultView(Strings);
        StringsView.Filter = FilterString;

        OpenFileCommand = new RelayCommand(OpenFile);
        ReloadCommand = new AsyncRelayCommand(ReloadAsync, () => SelectedCapture != null && !IsLoading);
        SelectCaptureCommand = new RelayCommand(parameter =>
        {
            if (parameter is CapturedAssembly capture)
                SelectedCapture = capture;
        });
        CopySourceCommand = new RelayCommand(CopySource, () => IsSourceReady);
        CopyIlCommand = new RelayCommand(() => Copy(IlCode, "IL"), () => !string.IsNullOrWhiteSpace(IlCode));
        RevealCommand = new RelayCommand(RevealCapture, () => SelectedCapture != null);
        CopyIdentityCommand = new RelayCommand(CopyIdentity, () => _summary != null);
        SaveSourceCommand = new RelayCommand(SaveSource, () => IsSourceReady);
        ExportReportMarkdownCommand = new RelayCommand(ExportReportMarkdown, () => _summary != null);
        ExportReportJsonCommand = new RelayCommand(ExportReportJson, () => _summary != null);
        CopyMetadataCommand = new RelayCommand(CopyMetadata, () => _summary != null);
        CopyMvidCommand = new RelayCommand(CopyMvid, () => _summary != null);
        CopyStrongNameCommand = new RelayCommand(CopyStrongName, () => _summary != null);
        CopyAllStringsCommand = new RelayCommand(CopyAllStrings, () => Strings.Count > 0);
        ClearCapturesCommand = new RelayCommand(ClearCaptures, () => Captures.Count > 0);
        RemoveCaptureCommand = new RelayCommand(RemoveCapture, parameter => parameter is CapturedAssembly);
        CopyCapturePathCommand = new RelayCommand(CopyCapturePath, () => SelectedCapture != null);
        CollapseAllTypesCommand = new RelayCommand(() => ShowNestedTypes = false);
        ExpandAllTypesCommand = new RelayCommand(() => ShowNestedTypes = true);
        SaveResourceCommand = new RelayCommand(SaveResource, () => _resourceContent is { Data.Length: > 0 });
        CopyResourceCommand = new RelayCommand(CopyResource, () => _resourceContent is { Data.Length: > 0 });

        _typeFilterDebouncer = new ViewRefreshDebouncer(ApplyTypeFilter);
        _stringFilterDebouncer = new ViewRefreshDebouncer(ApplyStringFilter);
        _codeSearchDebouncer = new ViewRefreshDebouncer(() => CodeSearchTerm = _codeSearchInput, 220);
    }

    public ObservableCollection<CapturedAssembly> Captures { get; }
    public ObservableCollection<TypeEntry> Types { get; }
    public ObservableCollection<MethodEntry> Methods { get; }
    public ObservableCollection<StringEntry> Strings { get; }
    public ICollectionView TypesView { get; }
    public ICollectionView StringsView { get; }

    public RelayCommand OpenFileCommand { get; }
    public AsyncRelayCommand ReloadCommand { get; }
    public RelayCommand SelectCaptureCommand { get; }
    public RelayCommand CopySourceCommand { get; }
    public RelayCommand CopyIlCommand { get; }
    public RelayCommand RevealCommand { get; }
    public RelayCommand CopyIdentityCommand { get; }
    public RelayCommand SaveSourceCommand { get; }
    public RelayCommand ExportReportMarkdownCommand { get; }
    public RelayCommand ExportReportJsonCommand { get; }
    public RelayCommand CopyMetadataCommand { get; }
    public RelayCommand CopyMvidCommand { get; }
    public RelayCommand CopyStrongNameCommand { get; }
    public RelayCommand CopyAllStringsCommand { get; }
    public RelayCommand SaveResourceCommand { get; }
    public RelayCommand CopyResourceCommand { get; }
    public RelayCommand ClearCapturesCommand { get; }
    public RelayCommand RemoveCaptureCommand { get; }
    public RelayCommand CopyCapturePathCommand { get; }
    public RelayCommand CollapseAllTypesCommand { get; }
    public RelayCommand ExpandAllTypesCommand { get; }

    public bool HasCaptures => Captures.Count > 0;
    public bool HasAssembly => _summary != null;

    public CapturedAssembly? SelectedCapture
    {
        get => _selectedCapture;
        set
        {
            if (ReferenceEquals(_selectedCapture, value))
                return;

            if (_selectedCapture != null)
                _selectedCapture.IsActive = false;
            _selectedCapture = value;
            if (value != null)
                value.IsActive = true;

            Raise(nameof(SelectedCapture), nameof(HasCaptures));
            RaiseCaptureCommands();

            if (value != null)
                _ = LoadCaptureAsync(value);
        }
    }

    private void RaiseCaptureCommands()
    {
        ReloadCommand.RaiseCanExecuteChanged();
        RevealCommand.RaiseCanExecuteChanged();
        CopyCapturePathCommand.RaiseCanExecuteChanged();
        ClearCapturesCommand.RaiseCanExecuteChanged();
    }

    public AssemblySummary? Summary
    {
        get => _summary;
        private set
        {
            if (!Set(ref _summary, value))
                return;
            ResetAssemblyTools();
            if (_selectedTab is 5 or 6 or 7)
                EnsureTabData();
            Raise(nameof(HasAssembly), nameof(IdentityLine), nameof(TypeSummaryLine),
                nameof(StrongNameText), nameof(IlOnlyText), nameof(CompileTimestampText),
                nameof(EmptyStateTitle), nameof(EmptyStateHint));
            CopyIdentityCommand.RaiseCanExecuteChanged();
            CopyMetadataCommand.RaiseCanExecuteChanged();
            CopyMvidCommand.RaiseCanExecuteChanged();
            CopyStrongNameCommand.RaiseCanExecuteChanged();
            ExportReportMarkdownCommand.RaiseCanExecuteChanged();
            ExportReportJsonCommand.RaiseCanExecuteChanged();
        }
    }

    public string IdentityLine => _summary == null
        ? "No assembly loaded"
        : $"{_summary.Name} {_summary.Version}".Trim();

    public string TypeSummaryLine => _summary == null
        ? string.Empty
        : $"{_summary.TypeCount} types · {_summary.MethodCount} methods · {_summary.FieldCount} fields · {_summary.NamespaceCount} namespaces";

    public string StrongNameText => _summary == null ? "—" : _summary.IsStrongNamed ? "yes" : "no";
    public string IlOnlyText => _summary == null ? "—" : _summary.IsIlOnly ? "yes" : "no";
    public string CompileTimestampText => _summary?.CompileTimestamp is { } compiled
        ? compiled.ToString("yyyy-MM-dd HH:mm:ss")
        : "deterministic";

    public string TypeFilter
    {
        get => _typeFilter;
        set
        {
            if (Set(ref _typeFilter, value))
                _typeFilterDebouncer.Request();
        }
    }

    public int VisibleTypeCount => TypesView.Cast<object>().Count();

    public bool IsTypesEmpty => HasAssembly && !IsLoading && TypesView.IsEmpty;

    /// <summary>False hides nested/compiler-generated types: the "collapse all" view.</summary>
    public bool ShowNestedTypes
    {
        get => _showNestedTypes;
        set
        {
            if (!Set(ref _showNestedTypes, value))
                return;
            ApplyTypeFilter();
        }
    }

    /// <summary>Raw text box value; highlighted code updates after the debounce.</summary>
    public string CodeSearchInput
    {
        get => _codeSearchInput;
        set
        {
            if (Set(ref _codeSearchInput, value))
                _codeSearchDebouncer.Request();
        }
    }

    /// <summary>Text highlighted inside the decompiled C# and disassembly panes.</summary>
    public string CodeSearchTerm
    {
        get => _codeSearchTerm;
        set
        {
            if (!Set(ref _codeSearchTerm, value))
                return;
            Raise(nameof(CodeSearchMatches), nameof(CodeSearchLabel), nameof(HasCodeSearch));
        }
    }

    public bool HasCodeSearch => !string.IsNullOrWhiteSpace(_codeSearchTerm);

    public int CodeSearchMatches => CountOccurrences(SourceCode, _codeSearchTerm) + CountOccurrences(IlCode, _codeSearchTerm);

    public string CodeSearchLabel => string.IsNullOrWhiteSpace(_codeSearchTerm)
        ? "search C# and IL"
        : CodeSearchMatches == 0 ? "no matches" : $"{CodeSearchMatches} match(es)";

    private static int CountOccurrences(string? text, string? term)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(term))
            return 0;

        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += term.Length;
        }

        return count;
    }

    public TypeEntry? SelectedType
    {
        get => _selectedType;
        set
        {
            if (ReferenceEquals(_selectedType, value))
                return;

            _selectedType = value;
            Methods.Clear();
            // Do not leave the previous type's IL / C# visible while the new one loads.
            IlCode = string.Empty;
            SourceCode = string.Empty;
            if (value != null)
            {
                foreach (MethodEntry method in value.Methods)
                    Methods.Add(method);
                SourceCode = "// decompiling " + value.FullName + " …";
                SelectedMethod = Methods.FirstOrDefault(m => m.HasBody) ?? Methods.FirstOrDefault();
                Raise(nameof(SelectedTypeHeader), nameof(SelectedTypeSummary));
                _ = LoadSourceAsync(value);
            }
            else
            {
                SelectedMethod = null;
            }
        }
    }

    public string SelectedTypeHeader => _selectedType?.FullName ?? "Select a type";
    public string SelectedTypeSummary => _selectedType == null
        ? string.Empty
        : $"{_selectedType.KindLabel} · {_selectedType.Visibility} · {_selectedType.MethodCount} methods · {_selectedType.FieldCount} fields";

    public MethodEntry? SelectedMethod
    {
        get => _selectedMethod;
        set
        {
            if (ReferenceEquals(_selectedMethod, value))
                return;

            _selectedMethod = value;
            IlCode = string.Empty;
            if (value != null)
            {
                _ = LoadIlAsync(value);
            }
        }
    }

    public string SourceCode
    {
        get => _sourceCode;
        private set
        {
            if (!Set(ref _sourceCode, value))
                return;
            CopySourceCommand.RaiseCanExecuteChanged();
            SaveSourceCommand.RaiseCanExecuteChanged();
            Raise(nameof(IsSourceReady), nameof(CodeSearchMatches), nameof(CodeSearchLabel));
        }
    }

    public string IlCode
    {
        get => _ilCode;
        private set
        {
            if (!Set(ref _ilCode, value))
                return;
            CopyIlCommand.RaiseCanExecuteChanged();
            Raise(nameof(CodeSearchMatches), nameof(CodeSearchLabel));
        }
    }

    /// <summary>True when the current C# pane holds real decompiled code (not a status placeholder).</summary>
    public bool IsSourceReady => !string.IsNullOrWhiteSpace(_sourceCode)
        && !_sourceCode.StartsWith("//", StringComparison.Ordinal);

    public string StringFilter
    {
        get => _stringFilter;
        set
        {
            if (Set(ref _stringFilter, value))
                _stringFilterDebouncer.Request();
        }
    }

    public int VisibleStringCount => StringsView.Cast<object>().Count();

    public bool IsStringsEmpty => HasAssembly && !IsLoading && StringsView.IsEmpty;

    public bool ShowShortStrings
    {
        get => _showShortStrings;
        set
        {
            if (Set(ref _showShortStrings, value))
                ApplyStringFilter();
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (!Set(ref _isLoading, value))
                return;
            ReloadCommand.RaiseCanExecuteChanged();
            Raise(nameof(EmptyStateTitle), nameof(EmptyStateHint), nameof(IsTypesEmpty), nameof(IsStringsEmpty));
        }
    }

    public bool IsDecompiling
    {
        get => _isDecompiling;
        private set => Set(ref _isDecompiling, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (Set(ref _errorMessage, value))
                Raise(nameof(HasError), nameof(EmptyStateTitle), nameof(EmptyStateHint));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    public string EmptyStateTitle => IsLoading
        ? "Reading assembly metadata…"
        : HasError
            ? "This assembly could not be read"
            : "No assembly loaded";

    public string EmptyStateHint => IsLoading
        ? "Types, strings and references are collected in the background."
        : HasError
            ? _errorMessage ?? string.Empty
            : "Dump a module from the workspace, or open any managed assembly from disk.";

    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (Set(ref _selectedTab, value))
                EnsureTabData();
        }
    }

    public void SeedFromSession(TargetSession session)
    {
        // Nothing is pre-loaded: captures are explicit artefacts. The session is
        // only used to label where a capture came from.
        _ = session;
    }

    public void ClearSession()
    {
        // Captures survive a detach on purpose: they are files on disk.
    }

    public void AddCapture(string path, string origin)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        CapturedAssembly? existing = Captures.FirstOrDefault(c =>
            string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            SelectedCapture = existing;
            return;
        }

        var capture = new CapturedAssembly
        {
            Path = path,
            Name = System.IO.Path.GetFileNameWithoutExtension(path),
            Origin = origin,
            Size = new FileInfo(path).Length,
        };

        Captures.Insert(0, capture);
        Raise(nameof(HasCaptures));
        ClearCapturesCommand.RaiseCanExecuteChanged();
        if (SelectedCapture == null)
            SelectedCapture = capture;
        _shell.Log.Info($"Inspector queued {capture.Filename} ({capture.OriginLabel})", "inspect");
    }

    private async Task LoadCaptureAsync(CapturedAssembly capture)
    {
        int generation = ++_loadGeneration;
        IsLoading = true;
        ErrorMessage = null;
        SourceCode = string.Empty;
        IlCode = string.Empty;
        Types.Clear();
        Methods.Clear();
        Strings.Clear();
        SelectedType = null;
        Raise(nameof(VisibleTypeCount), nameof(VisibleStringCount), nameof(IsTypesEmpty), nameof(IsStringsEmpty));

        try
        {
            AssemblyInspector inspector = await Task.Run(() => AssemblyInspector.Open(capture.Path));
            if (generation != _loadGeneration)
            {
                inspector.Dispose();
                return;
            }

            _inspector?.Dispose();
            _inspector = inspector;
            Summary = inspector.Summary;
            capture.Apply(inspector.Summary);

            foreach (TypeEntry type in inspector.Types)
                Types.Add(type);
            foreach (StringEntry text in inspector.Strings)
                Strings.Add(text);

            TypesView.Refresh();
            StringsView.Refresh();
            Raise(nameof(VisibleTypeCount), nameof(VisibleStringCount), nameof(IsTypesEmpty), nameof(IsStringsEmpty));

            SelectedType = Types.FirstOrDefault(t => !t.IsCompilerGenerated && t.MethodCount > 0)
                ?? Types.FirstOrDefault();
            _shell.Log.Success($"Inspected {capture.Filename}: {inspector.Summary.TypeCount} types, {inspector.Summary.MethodCount} methods", "inspect");
        }
        catch (Exception ex) when (generation == _loadGeneration)
        {
            ErrorMessage = ex is BadImageFormatException
                ? "This file does not contain readable .NET metadata. Protected or native module?"
                : ex.Message;
            Summary = null;
            _inspector?.Dispose();
            _inspector = null;
            _shell.Log.Error($"Inspect failed for {capture.Filename}: {ex.Message}", "inspect");
        }
        catch (Exception)
        {
            // A newer capture superseded this load; its error is reported instead.
        }
        finally
        {
            if (generation == _loadGeneration)
                IsLoading = false;
        }
    }

    private void ApplyTypeFilter()
    {
        TypeEntry? keep = _selectedType;
        TypesView.Refresh();
        if (keep != null && TypesView.Contains(keep))
        {
            // WPF clears the ListBox selection on CollectionView.Reset and the clear
            // travels back through SelectedItem, which would close the decompiled
            // type. Restore through the property so the method list / C# / IL come
            // back as well.
            if (!ReferenceEquals(_selectedType, keep))
                SelectedType = keep;
        }
        else if (keep != null)
        {
            SelectedType = null;
        }

        Raise(nameof(VisibleTypeCount), nameof(IsTypesEmpty));
    }

    private void ApplyStringFilter()
    {
        StringsView.Refresh();
        Raise(nameof(VisibleStringCount), nameof(IsStringsEmpty));
    }

    private async Task LoadSourceAsync(TypeEntry type)
    {
        if (_inspector == null)
            return;

        AssemblyInspector inspector = _inspector;
        IsDecompiling = true;
        SourceCode = "// decompiling " + type.FullName + " …";

        try
        {
            string code = await Task.Run(() => inspector.DecompileType(type));
            if (!ReferenceEquals(_inspector, inspector) || !ReferenceEquals(SelectedType, type))
                return;

            SourceCode = code;
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_inspector, inspector) && ReferenceEquals(SelectedType, type))
                SourceCode = "// decompilation failed: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(SelectedType, type))
                IsDecompiling = false;
        }
    }

    private async Task LoadIlAsync(MethodEntry method)
    {
        if (_inspector == null)
            return;

        AssemblyInspector inspector = _inspector;
        try
        {
            string il = await Task.Run(() => inspector.Disassemble(method));
            // A slower previous disassembly must not overwrite the method the user
            // selected meanwhile (stale IL bug).
            if (!ReferenceEquals(_inspector, inspector) || !ReferenceEquals(SelectedMethod, method))
                return;

            IlCode = il;
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_inspector, inspector) && ReferenceEquals(SelectedMethod, method))
                IlCode = "// IL unavailable: " + ex.Message;
        }
    }

    private bool FilterType(object item)
    {
        if (item is not TypeEntry type)
            return false;
        if (!ShowNestedTypes && type.NestedLevel > 0)
            return false;
        if (string.IsNullOrWhiteSpace(TypeFilter))
            return true;

        return type.Name.Contains(TypeFilter, StringComparison.OrdinalIgnoreCase)
            || type.Namespace.Contains(TypeFilter, StringComparison.OrdinalIgnoreCase)
            || type.FullName.Contains(TypeFilter, StringComparison.OrdinalIgnoreCase);
    }

    private bool FilterString(object item)
    {
        if (item is not StringEntry text)
            return false;
        if (!ShowShortStrings && text.Length < 6)
            return false;
        if (string.IsNullOrWhiteSpace(StringFilter))
            return true;
        return text.Value.Contains(StringFilter, StringComparison.OrdinalIgnoreCase);
    }

    private void OpenFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a managed assembly",
            Filter = "Managed assemblies (*.dll;*.exe)|*.dll;*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        try
        {
            if (dialog.ShowDialog(Application.Current?.MainWindow) == true)
                AddCapture(dialog.FileName, "file");
        }
        catch (Exception ex)
        {
            _shell.Toast("Could not open file", ex.Message, ToastKind.Warning);
        }
    }

    private async Task ReloadAsync()
    {
        if (SelectedCapture is { } capture)
            await LoadCaptureAsync(capture);
    }

    private void CopyIdentity()
    {
        if (_summary != null)
            Copy(_summary.FullName, "assembly identity");
    }

    private void CopySource()
    {
        if (IsSourceReady)
            Copy(SourceCode, "decompiled C#");
    }

    private void SaveSource()
    {
        if (!IsSourceReady)
            return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save decompiled C#",
            Filter = "C# source (*.cs)|*.cs|Text file (*.txt)|*.txt",
            DefaultExt = ".cs",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = PageExports.SuggestedFileName(
                (_summary?.Name ?? "assembly") + "-" + (_selectedType?.Name ?? "type"), "cs"),
            InitialDirectory = PageExports.SuggestedDirectory(_shell),
        };

        try
        {
            if (dialog.ShowDialog(Application.Current?.MainWindow) != true)
                return;

            PageExports.WriteText(dialog.FileName, SourceCode);
            _shell.Toast("C# saved", Path.GetFileName(dialog.FileName), ToastKind.Success);
            _shell.Log.Success($"Saved decompiled C# to {dialog.FileName}", "inspect");
        }
        catch (Exception ex)
        {
            _shell.Toast("Save failed", ex.Message, ToastKind.Error);
        }
    }

    private void ExportReportMarkdown() => ExportReport("Markdown", "md", "Markdown (*.md)|*.md",
        summary => PageExports.InspectorMarkdown(summary, Types, Strings));

    private void ExportReportJson() => ExportReport("JSON", "json", "JSON file (*.json)|*.json",
        summary => PageExports.InspectorJson(summary, Types, Strings));

    private void ExportReport(string format, string extension, string filter, Func<AssemblySummary, string> build)
    {
        if (_summary is not { } summary || SelectedCapture is not { } capture)
            return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export assembly report as " + format,
            Filter = filter,
            DefaultExt = "." + extension,
            AddExtension = true,
            OverwritePrompt = true,
            FileName = PageExports.SuggestedFileName(
                (_summary.Name.Length > 0 ? _summary.Name : "assembly") + "-report", extension),
            InitialDirectory = PageExports.SuggestedDirectory(_shell),
        };

        try
        {
            if (dialog.ShowDialog(Application.Current?.MainWindow) != true)
                return;

            string content = build(summary);
            PageExports.WriteText(dialog.FileName, content, utf8Bom: extension == "md");
            _shell.Toast("Report exported", $"{capture.Filename} → {Path.GetFileName(dialog.FileName)}", ToastKind.Success);
            _shell.Log.Success($"Exported {format} report to {dialog.FileName}", "inspect");
        }
        catch (Exception ex)
        {
            _shell.Log.Error("Report export failed: " + ex.Message, "inspect");
            _shell.Toast("Export failed", ex.Message, ToastKind.Error);
        }
    }

    private void CopyMetadata()
    {
        if (_summary is not { } summary)
            return;

        string text = string.Join(Environment.NewLine,
            $"Name: {summary.Name}",
            $"Full name: {summary.FullName}",
            $"Version: {summary.Version}",
            $"Culture: {summary.Culture}",
            $"Public key token: {summary.PublicKeyToken}",
            $"Module: {summary.ModuleName}",
            $"Runtime: {summary.RuntimeVersion}",
            $"Kind: {summary.Kind}",
            $"Machine: {summary.Machine}",
            $"Target framework: {summary.TargetFramework}",
            $"Entry point: {summary.EntryPoint}",
            $"Strong named: {(summary.IsStrongNamed ? "yes" : "no")}",
            $"IL only: {(summary.IsIlOnly ? "yes" : "no")}",
            $"ReadyToRun: {(summary.IsReadyToRun ? "yes" : "no")}",
            $"Mixed mode: {(summary.IsMixedMode ? "yes" : "no")}",
            $"MVID: {summary.Mvid}",
            $"Compiled: {CompileTimestampText}",
            $"Company: {summary.Company}",
            $"Source: {summary.SourcePath}",
            $"SHA-256: {summary.Sha256}",
            $"Types: {summary.TypeCount}",
            $"Methods: {summary.MethodCount}",
            $"Fields: {summary.FieldCount}",
            $"References: {summary.AssemblyRefCount}");

        Copy(text, "assembly metadata");
    }

    private void CopyMvid()
    {
        if (!string.IsNullOrWhiteSpace(_summary?.Mvid))
            Copy(_summary!.Mvid!, "MVID");
    }

    private void CopyStrongName()
    {
        if (_summary is not { } summary)
            return;

        string token = string.IsNullOrWhiteSpace(summary.PublicKeyToken) || summary.PublicKeyToken == "null"
            ? "unsigned"
            : summary.PublicKeyToken;
        Copy($"{summary.FullName}{Environment.NewLine}PublicKeyToken={token}", "assembly identity");
    }

    private void CopyAllStrings()
    {
        List<StringEntry> strings = StringsView.Cast<StringEntry>().ToList();
        if (strings.Count == 0)
        {
            _shell.Toast("Nothing to copy", "No literal strings match the current filter.", ToastKind.Info);
            return;
        }

        Copy(string.Join(Environment.NewLine, strings.Select(text => text.Value)), $"{strings.Count} literal string(s)");
    }

    private void CopyCapturePath()
    {
        if (SelectedCapture is { } capture)
            Copy(capture.Path, "capture path");
    }

    private void RemoveCapture(object? parameter)
    {
        if (parameter is not CapturedAssembly capture)
            return;

        int index = Captures.IndexOf(capture);
        if (index < 0)
            return;

        bool wasActive = ReferenceEquals(_selectedCapture, capture);
        Captures.Remove(capture);
        Raise(nameof(HasCaptures));
        ClearCapturesCommand.RaiseCanExecuteChanged();
        _shell.Log.Info($"Removed capture {capture.Filename} from the recent list", "inspect");

        if (!wasActive)
            return;

        _selectedCapture = null;
        capture.IsActive = false;
        Raise(nameof(SelectedCapture), nameof(HasCaptures));
        RaiseCaptureCommands();

        if (Captures.Count > 0)
        {
            SelectedCapture = Captures[Math.Min(index, Captures.Count - 1)];
        }
        else
        {
            ResetAssembly();
        }
    }

    private void ClearCaptures()
    {
        foreach (CapturedAssembly capture in Captures)
            capture.IsActive = false;
        Captures.Clear();
        _selectedCapture = null;
        Raise(nameof(SelectedCapture), nameof(HasCaptures));
        RaiseCaptureCommands();
        ResetAssembly();
        _shell.Log.Info("Cleared the inspector capture list", "inspect");
    }

    private void ResetAssembly()
    {
        _inspector?.Dispose();
        _inspector = null;
        Summary = null;
        Types.Clear();
        Methods.Clear();
        Strings.Clear();
        SelectedType = null;
        SourceCode = string.Empty;
        IlCode = string.Empty;
        ErrorMessage = null;
        Raise(nameof(VisibleTypeCount), nameof(VisibleStringCount), nameof(IsTypesEmpty), nameof(IsStringsEmpty));
    }

    private void RevealCapture()
    {
        if (SelectedCapture?.Path is not { } path)
            return;

        if (!File.Exists(path))
        {
            _shell.Toast("File not found", "The capture no longer exists at " + path, ToastKind.Warning);
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

    private void Copy(string text, string label)
    {
        if (string.IsNullOrEmpty(text))
            return;

        try
        {
            Clipboard.SetText(text);
            _shell.Toast("Copied", label + " is on the clipboard", ToastKind.Info);
        }
        catch (Exception ex)
        {
            _shell.Toast("Copy failed", ex.Message, ToastKind.Warning);
        }
    }

    // ------------------------------------------------------------ PE / resources / hex

    public PeHeaderInfo? PeHeader
    {
        get => _peHeader;
        private set
        {
            if (!Set(ref _peHeader, value))
                return;
            Raise(nameof(HasPeHeader), nameof(HasPeError), nameof(PeError), nameof(PeSummary),
                nameof(PeFields), nameof(PeSections), nameof(PeDirectories));
        }
    }

    public bool HasPeHeader => _peHeader is { IsValid: true };
    public bool HasPeError => _peHeader is { IsValid: false };
    public string? PeError => _peHeader is { IsValid: false } ? _peHeader.Error : null;
    public string PeSummary => _peHeader?.SummaryLabel ?? "PE headers load when this tab opens.";
    public IReadOnlyList<PeField> PeFields => _peHeader?.Fields ?? Array.Empty<PeField>();
    public IReadOnlyList<PeSectionInfo> PeSections => _peHeader?.Sections ?? Array.Empty<PeSectionInfo>();
    public IReadOnlyList<PeDirectoryInfo> PeDirectories => _peHeader?.Directories ?? Array.Empty<PeDirectoryInfo>();

    public byte[]? HexBytes
    {
        get => _hexBytes;
        private set
        {
            if (Set(ref _hexBytes, value))
                Raise(nameof(HexSourceLabel));
        }
    }

    public string HexSourceLabel => _summary?.SourcePath ?? string.Empty;

    public ResourceEntry? SelectedResource
    {
        get => _selectedResource;
        set
        {
            if (Set(ref _selectedResource, value))
                LoadResource(value);
        }
    }

    public ResourceContent? ResourceContent
    {
        get => _resourceContent;
        private set
        {
            if (!Set(ref _resourceContent, value))
                return;
            Raise(nameof(HasResourceContent), nameof(HasResourceImage), nameof(HasResourceText),
                nameof(HasResourceHex), nameof(ResourceText), nameof(ResourceHex), nameof(ResourceNote),
                nameof(ResourceHeader));
            SaveResourceCommand.RaiseCanExecuteChanged();
            CopyResourceCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasResourceContent => _resourceContent != null;
    public bool HasResourceImage => _resourceContent is { IsImage: true } && _resourceImage != null;
    public bool HasResourceText => _resourceContent is { IsText: true };
    public bool HasResourceHex => _resourceContent is { IsText: false, IsImage: false };
    public string ResourceText => _resourceContent?.TextPreview ?? string.Empty;
    public string ResourceHex => _resourceContent?.HexPreview ?? string.Empty;
    public string ResourceNote => _resourceContent?.Note ?? string.Empty;
    public string ResourceHeader => _resourceContent == null
        ? "Select a resource to preview it."
        : $"{_resourceContent.Name} · {_resourceContent.Kind} · {_resourceContent.SizeLabel}";

    public ImageSource? ResourceImage
    {
        get => _resourceImage;
        private set
        {
            if (Set(ref _resourceImage, value))
                Raise(nameof(HasResourceImage));
        }
    }

    private void ResetAssemblyTools()
    {
        _fileBytes = null;
        PeHeader = null;
        HexBytes = null;
        _selectedResource = null;
        Raise(nameof(SelectedResource));
        ResourceContent = null;
        ResourceImage = null;
        Raise(nameof(HexSourceLabel));
    }

    private void EnsureTabData()
    {
        if (_summary == null)
            return;

        switch (_selectedTab)
        {
            case 5:
                LoadPe();
                break;
            case 6:
                if (_resourceContent == null)
                    LoadResource(_selectedResource);
                break;
            case 7:
                LoadHex();
                break;
        }
    }

    private byte[]? EnsureFileBytes()
    {
        if (_fileBytes != null)
            return _fileBytes;

        string? path = _summary?.SourcePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        try
        {
            _fileBytes = File.ReadAllBytes(path);
            return _fileBytes;
        }
        catch (Exception ex)
        {
            _shell.Log.Warn("Could not read the assembly file: " + ex.Message, "inspect");
            return null;
        }
    }

    private void LoadPe()
    {
        byte[]? bytes = EnsureFileBytes();
        PeHeader = bytes == null
            ? new PeHeaderInfo { IsValid = false, Error = "The capture file is no longer available." }
            : PeHeaderReader.Read(bytes);
    }

    private void LoadHex()
    {
        HexBytes = EnsureFileBytes();
        if (HexBytes == null)
            _shell.Log.Warn("Hex view: the capture file is no longer available.", "inspect");
    }

    private void LoadResource(ResourceEntry? entry)
    {
        if (entry == null || _summary == null)
        {
            ResourceContent = null;
            ResourceImage = null;
            return;
        }

        string? path = _summary.SourcePath;
        ResourceContent? content = (string.IsNullOrEmpty(path) ? null : ResourceExtractor.ReadOne(path, entry.Name))
            ?? new ResourceContent { Name = entry.Name, Kind = entry.Kind, Note = "The resource could not be read." };

        ResourceContent = content;
        ResourceImage = content.IsImage ? CreateImage(content.Data) : null;
    }

    private static ImageSource? CreateImage(byte[] data)
    {
        if (data.Length == 0)
            return null;

        try
        {
            var image = new BitmapImage();
            using var stream = new MemoryStream(data);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            // Bound the decoded bitmap for large (but allowed) images. Oversized
            // "decompression bomb" payloads are already rejected by ResourceExtractor.
            if (ResourceExtractor.TryGetImageDimensions(data, out int width, out _) &&
                width > MaxResourcePreviewDecodeWidth)
            {
                image.DecodePixelWidth = MaxResourcePreviewDecodeWidth;
            }
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private void SaveResource()
    {
        if (_resourceContent is not { Data.Length: > 0 } content)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "Save resource",
            FileName = SanitizeResourceName(content.Name),
            Filter = "All files (*.*)|*.*",
            InitialDirectory = PageExports.SuggestedDirectory(_shell),
        };
        if (dialog.ShowDialog() != true)
            return;

        if (ResourceExtractor.TrySave(content, dialog.FileName, out string? error))
        {
            _shell.Toast("Resource saved", Path.GetFileName(dialog.FileName), ToastKind.Success);
            _shell.Log.Success($"Resource saved: {content.Name} → {dialog.FileName}", "inspect");
        }
        else
        {
            _shell.Toast("Resource not saved", error ?? "Unknown error", ToastKind.Error);
        }
    }

    private void CopyResource()
    {
        if (_resourceContent == null)
            return;

        string text = _resourceContent.IsText ? _resourceContent.TextPreview ?? string.Empty : _resourceContent.HexPreview ?? string.Empty;
        Copy(text, _resourceContent.IsText ? "Resource text" : "Resource hex");
    }

    private static string SanitizeResourceName(string name)
    {
        string cleaned = name.Replace('/', '_').Replace('\\', '_');
        foreach (char invalid in Path.GetInvalidFileNameChars())
            cleaned = cleaned.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(cleaned) ? "resource.bin" : cleaned;
    }

}
