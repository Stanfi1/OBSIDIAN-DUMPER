using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Obsidian.Common;
using Obsidian.Dumper.Core;
using Obsidian.Dumper.Core.Dumping;
using Obsidian.Dumper.Core.Memory;
using Obsidian.Dumper.Core.Runtime;
using Obsidian.Views;

namespace Obsidian.ViewModels;

/// <summary>
/// Live memory page: virtual address map, read-only pattern scanner and a ClrMD
/// runtime snapshot. Everything operates on the shell's current target session.
/// </summary>
public sealed class MemoryViewModel : ObservableObject
{
    private const int HexReadLimit = 4 * 1024 * 1024;
    private const int DumpReadLimit = 256 * 1024 * 1024;

    private readonly ShellViewModel _shell;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _runtimeCancellation;
    private CancellationTokenSource? _carveCancellation;
    private bool _isCarving;
    private string _carveStatus = "Walk memory for valid .NET assemblies, including manually mapped ones.";
    private string? _carveError;
    private double _carveProgress;
    private IReadOnlyList<CarvedAssembly> _carved = Array.Empty<CarvedAssembly>();
    private bool _includeFrameworkAssemblies;

    private IReadOnlyList<MemoryRegion> _allRegions = Array.Empty<MemoryRegion>();
    private IReadOnlyList<MemoryRegion> _regions = Array.Empty<MemoryRegion>();
    private MemoryRegion? _selectedRegion;
    private string _regionFilter = string.Empty;
    private bool _suspiciousOnly;
    private bool _showReserved;
    private bool _modulesOnly;
    private bool _executableOnly;
    private bool _isLoadingRegions;
    private string _regionStatus = "Attach a target to read its address space.";
    private bool _hasRegionError;
    private int _selectedTab;

    private string _scanPattern = string.Empty;
    private string _selectedScanKind = "Text";
    private bool _scanCaseInsensitive = true;
    private bool _scanExecutableOnly;
    private bool _isScanning;
    private bool _scanHasRun;
    private string _scanStatus = "Read-only scan of committed, readable regions.";
    private string? _scanError;
    private double _scanProgress;
    private IReadOnlyList<MemoryScanHit> _hits = Array.Empty<MemoryScanHit>();
    private MemoryScanHit? _selectedHit;

    private RuntimeSnapshot? _snapshot;
    private bool _isCapturingRuntime;
    private string _runtimeStatus = "Capture a snapshot of the managed heap.";
    private string? _runtimeError;

    public MemoryViewModel(ShellViewModel shell)
    {
        _shell = shell;
        ScanKinds = new[] { "Text", "UTF-16", "Hex" };

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => HasTarget);
        DumpRegionCommand = new AsyncRelayCommand(DumpRegionAsync, () => SelectedRegion != null);
        HexRegionCommand = new RelayCommand(ShowRegionHex, () => SelectedRegion != null);
        CopyRegionCommand = new RelayCommand(CopyRegionAddress, () => SelectedRegion != null);
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => HasTarget && !IsScanning && ScanPattern.Length > 0);
        CancelScanCommand = new RelayCommand(CancelScan, () => IsScanning);
        OpenHitHexCommand = new RelayCommand(ShowHitHex, () => SelectedHit != null);
        CopyHitCommand = new RelayCommand(CopyHitAddress, () => SelectedHit != null);
        CaptureRuntimeCommand = new AsyncRelayCommand(CaptureRuntimeAsync, () => HasTarget && !IsCapturingRuntime);
        CancelRuntimeCommand = new RelayCommand(CancelRuntime, () => IsCapturingRuntime);
        GoToTargetsCommand = new RelayCommand(() => _shell.NavigateTo("targets"));
        CarveCommand = new AsyncRelayCommand(CarveAsync, () => HasTarget && !IsCarving);
        CancelCarveCommand = new RelayCommand(CancelCarve, () => IsCarving);
        OpenCarveFolderCommand = new RelayCommand(OpenCarveFolder, () => Directory.Exists(CarveOutputDirectory));
    }

    public bool HasTarget => _shell.Session != null;
    public string TargetLabel => _shell.Session is { } session
        ? $"{session.Process.DisplayName} · pid {session.Id} · {session.Process.ArchitectureLabel}"
        : "No target attached";

    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            Set(ref _selectedTab, value);
        }
    }

    // ------------------------------------------------------------ map

    public IReadOnlyList<MemoryRegion> Regions => _regions;
    public MemoryRegion? SelectedRegion
    {
        get => _selectedRegion;
        set
        {
            if (Set(ref _selectedRegion, value))
                RaiseRegionSelection();
        }
    }

    public string RegionFilter
    {
        get => _regionFilter;
        set
        {
            if (Set(ref _regionFilter, value))
                ApplyRegionFilter();
        }
    }

    public bool SuspiciousOnly
    {
        get => _suspiciousOnly;
        set
        {
            if (Set(ref _suspiciousOnly, value))
                ApplyRegionFilter();
        }
    }

    /// <summary>Only regions that belong to a loaded module.</summary>
    public bool ModulesOnly
    {
        get => _modulesOnly;
        set
        {
            if (Set(ref _modulesOnly, value))
                ApplyRegionFilter();
        }
    }

    /// <summary>Only executable regions.</summary>
    public bool ExecutableOnly
    {
        get => _executableOnly;
        set
        {
            if (Set(ref _executableOnly, value))
                ApplyRegionFilter();
        }
    }

    /// <summary>Reserved (not committed) regions are hidden unless asked for.</summary>
    public bool ShowReserved
    {
        get => _showReserved;
        set
        {
            if (Set(ref _showReserved, value))
                ApplyRegionFilter();
        }
    }

    public bool IsLoadingRegions
    {
        get => _isLoadingRegions;
        private set
        {
            if (Set(ref _isLoadingRegions, value))
                Raise(nameof(IsRegionListEmpty), nameof(RegionSummary));
        }
    }

    public string RegionStatus
    {
        get => _regionStatus;
        private set => Set(ref _regionStatus, value);
    }

    /// <summary>True when the last address-space read failed (status line shown in the error colour).</summary>
    public bool HasRegionError
    {
        get => _hasRegionError;
        private set => Set(ref _hasRegionError, value);
    }

    public string RegionSummary
    {
        get
        {
            if (!HasTarget)
                return "No target";
            if (IsLoadingRegions)
                return "Reading the address space…";

            long committedBytes = _allRegions.Where(region => region.IsCommitted)
                .Aggregate(0L, (sum, region) => sum + (long)region.Size);
            int committed = _allRegions.Count(region => region.IsCommitted);
            int reserved = _allRegions.Count(region => region.StateKind == MemoryRegionState.Reserve);
            int executable = _allRegions.Count(region => region.IsExecutable);
            int suspicious = _allRegions.Count(region => region.IsSuspicious);
            return $"{_regions.Count:N0} of {_allRegions.Count:N0} regions · {MemoryRegion.FormatBytes(committedBytes)} committed · " +
                   $"{committed:N0} commit / {reserved:N0} reserve · {executable} exec · {suspicious} suspicious";
        }
    }

    public bool IsRegionListEmpty => !IsLoadingRegions && _regions.Count == 0;

    public string SelectedRegionDetail => SelectedRegion is not { } region
        ? "Select a region to dump it or open it in the hex viewer."
        : $"{region.BaseLabel} · {region.SizeLabel} · {region.ProtectLabel} · {region.TypeLabel} · {region.ModuleLabel}";

    public bool HasSelectedRegion => SelectedRegion != null;

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand DumpRegionCommand { get; }
    public RelayCommand HexRegionCommand { get; }
    public RelayCommand CopyRegionCommand { get; }
    public RelayCommand GoToTargetsCommand { get; }

    // ------------------------------------------------------------ scan

    public IReadOnlyList<string> ScanKinds { get; }
    public string SelectedScanKind
    {
        get => _selectedScanKind;
        set
        {
            if (Set(ref _selectedScanKind, value))
                Raise(nameof(IsHexKind), nameof(IsTextKind), nameof(IsUtf16Kind));
        }
    }

    public bool IsHexKind => SelectedScanKind == "Hex";
    public bool IsUtf16Kind => SelectedScanKind == "UTF-16";
    public bool IsTextKind => SelectedScanKind == "Text";

    public string ScanPattern
    {
        get => _scanPattern;
        set
        {
            if (Set(ref _scanPattern, value))
            {
                // A different pattern invalidates the previous run's empty-state hint.
                ScanHasRun = false;
                Raise(nameof(ScanEmptyMessage));
                ScanCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool ScanCaseInsensitive
    {
        get => _scanCaseInsensitive;
        set => Set(ref _scanCaseInsensitive, value);
    }

    public bool ScanExecutableOnly
    {
        get => _scanExecutableOnly;
        set => Set(ref _scanExecutableOnly, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (Set(ref _isScanning, value))
            {
                ScanCommand.RaiseCanExecuteChanged();
                CancelScanCommand.RaiseCanExecuteChanged();
                Raise(nameof(ScanHasResults), nameof(ScanSummary), nameof(ShowScanEmptyState));
            }
        }
    }

    /// <summary>True once a scan has completed for the current pattern (drives the empty-state hint).</summary>
    public bool ScanHasRun
    {
        get => _scanHasRun;
        private set
        {
            if (Set(ref _scanHasRun, value))
                Raise(nameof(ScanEmptyMessage));
        }
    }

    public string ScanStatus
    {
        get => _scanStatus;
        private set => Set(ref _scanStatus, value);
    }

    public string? ScanError
    {
        get => _scanError;
        private set
        {
            if (Set(ref _scanError, value))
                Raise(nameof(HasScanError), nameof(ShowScanEmptyState));
        }
    }

    public bool HasScanError => !string.IsNullOrEmpty(ScanError);

    public double ScanProgress
    {
        get => _scanProgress;
        private set => Set(ref _scanProgress, value);
    }

    public IReadOnlyList<MemoryScanHit> Hits
    {
        get => _hits;
        private set
        {
            _hits = value;
            Raise(nameof(Hits), nameof(ScanHasResults), nameof(ScanSummary), nameof(ShowScanEmptyState));
        }
    }

    public bool ScanHasResults => _hits.Count > 0;

    /// <summary>
    /// True only when the result list is empty and no scan is running or failed;
    /// the centered placeholder must never be visible at the same time as the
    /// centered "Scanning memory…" panel or the error text, or the strings overlap.
    /// </summary>
    public bool ShowScanEmptyState => !ScanHasResults && !IsScanning && !HasScanError;

    /// <summary>Hint shown in the empty result area; changes once a scan has run for the pattern.</summary>
    public string ScanEmptyMessage => ScanHasRun
        ? $"No matches for '{ScanPattern}'."
        : "Enter a pattern above and press Scan.";

    public MemoryScanHit? SelectedHit
    {
        get => _selectedHit;
        set
        {
            if (Set(ref _selectedHit, value))
            {
                OpenHitHexCommand.RaiseCanExecuteChanged();
                CopyHitCommand.RaiseCanExecuteChanged();
                Raise(nameof(SelectedHitDetail));
            }
        }
    }

    public string SelectedHitDetail => SelectedHit is not { } hit
        ? "Double-click a hit to open it in the hex viewer."
        : $"{hit.AddressLabel} · {hit.ModuleLabel} · {hit.Preview}";

    public string ScanSummary
    {
        get
        {
            if (!HasTarget)
                return "No target";
            if (IsScanning)
                return "Scanning…";
            if (_hits.Count == 0)
                return "No results yet";
            return $"{_hits.Count:N0} hits";
        }
    }

    public AsyncRelayCommand ScanCommand { get; }
    public RelayCommand CancelScanCommand { get; }
    public RelayCommand OpenHitHexCommand { get; }
    public RelayCommand CopyHitCommand { get; }

    // ------------------------------------------------------------ runtime

    public RuntimeSnapshot? Snapshot
    {
        get => _snapshot;
        private set
        {
            if (Set(ref _snapshot, value))
            {
                Raise(nameof(HasSnapshot), nameof(SnapshotTypes), nameof(SnapshotWarnings),
                    nameof(SnapshotRuntime), nameof(SnapshotThreads), nameof(SnapshotModules),
                    nameof(SnapshotObjects), nameof(SnapshotHeap), nameof(SnapshotDuration),
                    nameof(SnapshotWalkable), nameof(HasSnapshotError), nameof(ShowSnapshotEmptyState));
            }
        }
    }

    public bool HasSnapshot => _snapshot is { Success: true };
    public bool HasSnapshotError => _snapshot is { Success: false };

    /// <summary>
    /// True only while there is nothing to list and no capture is running; the
    /// centered placeholder must not stay visible during the capture.
    /// </summary>
    public bool ShowSnapshotEmptyState => !HasSnapshot && !IsCapturingRuntime;
    public IReadOnlyList<RuntimeTypeStat> SnapshotTypes => _snapshot?.Types ?? Array.Empty<RuntimeTypeStat>();
    public IReadOnlyList<string> SnapshotWarnings => _snapshot?.Warnings ?? Array.Empty<string>();
    public string SnapshotRuntime => _snapshot?.Success == true ? _snapshot.RuntimeLabel : "—";
    public string SnapshotThreads => _snapshot?.Success == true ? _snapshot.ThreadCount.ToString() : "—";
    public string SnapshotModules => _snapshot?.Success == true ? _snapshot.ModuleCount.ToString() : "—";
    public string SnapshotObjects => _snapshot?.Success == true ? _snapshot.ObjectCountLabel : "—";
    public string SnapshotHeap => _snapshot?.Success == true ? _snapshot.HeapLabel : "—";
    public string SnapshotDuration => _snapshot?.Success == true ? _snapshot.DurationLabel : "—";
    public string SnapshotWalkable => _snapshot == null
        ? "—"
        : _snapshot.CanWalkHeap ? "walkable" : "not walkable";

    public bool IsCapturingRuntime
    {
        get => _isCapturingRuntime;
        private set
        {
            if (Set(ref _isCapturingRuntime, value))
            {
                CaptureRuntimeCommand.RaiseCanExecuteChanged();
                CancelRuntimeCommand.RaiseCanExecuteChanged();
                Raise(nameof(ShowSnapshotEmptyState));
            }
        }
    }

    public string RuntimeStatus
    {
        get => _runtimeStatus;
        private set => Set(ref _runtimeStatus, value);
    }

    public string? RuntimeError
    {
        get => _runtimeError;
        private set
        {
            if (Set(ref _runtimeError, value))
                Raise(nameof(HasRuntimeError));
        }
    }

    public bool HasRuntimeError => !string.IsNullOrEmpty(RuntimeError);

    public AsyncRelayCommand CaptureRuntimeCommand { get; }
    public RelayCommand CancelRuntimeCommand { get; }

    // ------------------------------------------------------------ carve

    public bool IsCarving
    {
        get => _isCarving;
        private set
        {
            if (Set(ref _isCarving, value))
            {
                CarveCommand.RaiseCanExecuteChanged();
                CancelCarveCommand.RaiseCanExecuteChanged();
                Raise(nameof(CarveSummary), nameof(ShowCarveEmptyState));
            }
        }
    }

    public string CarveStatus
    {
        get => _carveStatus;
        private set => Set(ref _carveStatus, value);
    }

    public string? CarveError
    {
        get => _carveError;
        private set
        {
            if (Set(ref _carveError, value))
                Raise(nameof(HasCarveError));
        }
    }

    public bool HasCarveError => !string.IsNullOrEmpty(CarveError);

    public double CarveProgress
    {
        get => _carveProgress;
        private set => Set(ref _carveProgress, value);
    }

    public IReadOnlyList<CarvedAssembly> CarvedAssemblies
    {
        get => _carved;
        private set
        {
            _carved = value;
            Raise(nameof(CarvedAssemblies), nameof(HasCarveResults), nameof(CarveSummary),
                nameof(ShowCarveEmptyState), nameof(OpenCarveFolderCommand));
            OpenCarveFolderCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasCarveResults => _carved.Count > 0;

    /// <summary>
    /// True only when the result list is empty and no carve is running; the
    /// centered placeholder must never be visible at the same time as the
    /// centered "Walking memory for assemblies…" panel, or the two strings overlap.
    /// </summary>
    public bool ShowCarveEmptyState => !HasCarveResults && !IsCarving;

    public bool IncludeFrameworkAssemblies
    {
        get => _includeFrameworkAssemblies;
        set => Set(ref _includeFrameworkAssemblies, value);
    }

    public string CarveOutputDirectory => Path.Combine(_shell.SettingsStore.Current.OutputDirectory, "carved");

    public string CarveSummary
    {
        get
        {
            if (!HasTarget)
                return "No target";
            if (IsCarving)
                return "Walking memory…";
            if (_carved.Count == 0)
                return "No carved assemblies yet";
            return $"{_carved.Count:N0} assemblies · {CarveOutputDirectory}";
        }
    }

    public AsyncRelayCommand CarveCommand { get; }
    public RelayCommand CancelCarveCommand { get; }
    public RelayCommand OpenCarveFolderCommand { get; }

    // ------------------------------------------------------------ lifecycle

    /// <summary>Called by the shell after attach/detach.</summary>
    public void OnSessionChanged()
    {
        Raise(nameof(HasTarget), nameof(TargetLabel), nameof(RegionSummary), nameof(ScanSummary));
        RefreshCommand.RaiseCanExecuteChanged();
        ScanCommand.RaiseCanExecuteChanged();
        CaptureRuntimeCommand.RaiseCanExecuteChanged();
        DumpRegionCommand.RaiseCanExecuteChanged();
        HexRegionCommand.RaiseCanExecuteChanged();
        CopyRegionCommand.RaiseCanExecuteChanged();
        CarveCommand.RaiseCanExecuteChanged();

        SelectedRegion = null;
        SelectedHit = null;
        ScanHasRun = false;

        if (!HasTarget)
        {
            _allRegions = Array.Empty<MemoryRegion>();
            _regions = Array.Empty<MemoryRegion>();
            Raise(nameof(Regions), nameof(IsRegionListEmpty), nameof(RegionSummary));
            RegionStatus = "Attach a target to read its address space.";
            HasRegionError = false;
            Hits = Array.Empty<MemoryScanHit>();
            Snapshot = null;
            RuntimeStatus = "Capture a snapshot of the managed heap.";
            RuntimeError = null;
            CarvedAssemblies = Array.Empty<CarvedAssembly>();
            CarveError = null;
            CarveStatus = "Walk memory for valid .NET assemblies, including manually mapped ones.";
            return;
        }

        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (!HasTarget || IsLoadingRegions)
            return;

        TargetSession? session = _shell.Session;
        if (session == null)
            return;

        IsLoadingRegions = true;
        HasRegionError = false;
        RegionStatus = "Reading the virtual address space…";
        try
        {
            IReadOnlyList<MemoryRegion> regions = await Task.Run(
                () => MemoryMapReader.Read(session.Target, session.Modules));
            _allRegions = regions;
            ApplyRegionFilter();
            RegionStatus = $"Scanned {regions.Count:N0} regions at {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception ex)
        {
            HasRegionError = true;
            RegionStatus = "Could not read the address space: " + ex.Message;
            _shell.Log.Warn("Memory map failed: " + ex.Message, "memory");
        }
        finally
        {
            IsLoadingRegions = false;
            Raise(nameof(IsRegionListEmpty));
        }
    }

    private void ApplyRegionFilter()
    {
        IEnumerable<MemoryRegion> query = _showReserved
            ? _allRegions
            : _allRegions.Where(region => region.IsCommitted);
        if (_suspiciousOnly)
            query = query.Where(region => region.IsSuspicious);
        if (_modulesOnly)
            query = query.Where(region => region.HasModule);
        if (_executableOnly)
            query = query.Where(region => region.IsExecutable);

        string filter = _regionFilter.Trim();
        if (filter.Length > 0)
        {
            query = query.Where(region =>
                region.ModuleLabel.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                region.BaseLabel.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                region.ProtectLabel.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                region.TypeLabel.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                region.FlagsLabel.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        _regions = query.ToList();
        Raise(nameof(Regions), nameof(RegionSummary), nameof(IsRegionListEmpty));
    }

    private void RaiseRegionSelection()
    {
        Raise(nameof(SelectedRegionDetail), nameof(HasSelectedRegion));
        DumpRegionCommand.RaiseCanExecuteChanged();
        HexRegionCommand.RaiseCanExecuteChanged();
        CopyRegionCommand.RaiseCanExecuteChanged();
        CarveCommand.RaiseCanExecuteChanged();
    }

    private async Task DumpRegionAsync()
    {
        TargetSession? session = _shell.Session;
        MemoryRegion? region = SelectedRegion;
        if (session == null || region == null)
            return;

        if (region.Size > DumpReadLimit)
        {
            _shell.Toast("Region too large",
                $"{region.SizeLabel} exceeds the 256 MB region dump limit.", ToastKind.Warning);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Dump memory region",
            FileName = $"region_{region.BaseAddress:X}_{region.Size:X}.bin",
            Filter = "Binary dump (*.bin)|*.bin|All files (*.*)|*.*",
            InitialDirectory = _shell.SettingsStore.Current.OutputDirectory,
        };
        if (dialog.ShowDialog() != true)
            return;

        int count = (int)region.Size;
        try
        {
            byte[] data = await Task.Run(() => session.Target.ReadBytes(region.BaseAddress, count, out _));
            File.WriteAllBytes(dialog.FileName, data);
            _shell.Toast("Region dumped", $"{region.BaseLabel} · {region.SizeLabel} → {Path.GetFileName(dialog.FileName)}",
                ToastKind.Success);
            _shell.Log.Success($"Dumped region {region.BaseLabel} ({region.SizeLabel})", "memory");
        }
        catch (Exception ex)
        {
            _shell.Toast("Region dump failed", ex.Message, ToastKind.Error);
            _shell.Log.Error("Region dump failed: " + ex.Message, "memory");
        }
    }

    private void ShowRegionHex()
    {
        TargetSession? session = _shell.Session;
        MemoryRegion? region = SelectedRegion;
        if (session == null || region == null)
            return;

        int count = (int)Math.Min(region.Size, (ulong)HexReadLimit);
        if (count <= 0)
            return;

        try
        {
            byte[] data = session.Target.ReadBytes(region.BaseAddress, count, out _);
            string suffix = region.Size > (ulong)HexReadLimit ? " · first 4 MB" : string.Empty;
            var window = new HexWindow(
                $"Region {region.BaseLabel} · {region.SizeLabel} · {region.ProtectLabel}{suffix}",
                data,
                region.ModuleLabel == "—" ? $"private region · {region.BaseLabel}" : region.ModulePath ?? region.ModuleLabel)
            {
                Owner = Application.Current?.MainWindow,
            };
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            _shell.Toast("Hex view failed", ex.Message, ToastKind.Error);
        }
    }

    private void CopyRegionAddress()
    {
        if (SelectedRegion is { } region)
            Clipboard.SetText(region.BaseLabel);
    }

    // ------------------------------------------------------------ scan

    private async Task ScanAsync()
    {
        TargetSession? session = _shell.Session;
        if (session == null || IsScanning || ScanPattern.Length == 0)
            return;

        _scanCancellation = new CancellationTokenSource();
        IsScanning = true;
        ScanError = null;
        ScanProgress = 0;
        Hits = Array.Empty<MemoryScanHit>();
        SelectedHit = null;
        ScanStatus = "Scanning readable regions…";

        MemoryScanKind kind = SelectedScanKind switch
        {
            "UTF-16" => MemoryScanKind.Utf16,
            "Hex" => MemoryScanKind.Hex,
            _ => MemoryScanKind.Utf8,
        };

        var progress = new Progress<MemoryScanProgress>(value =>
        {
            ScanProgress = value.RegionsTotal == 0 ? 0 : value.RegionsScanned * 100.0 / value.RegionsTotal;
            ScanStatus = $"{value.RegionsScanned:N0}/{value.RegionsTotal:N0} regions · " +
                         $"{MemoryRegion.FormatBytes(value.BytesScanned)} scanned · {value.Hits:N0} hits";
        });

        var options = new MemoryScanOptions
        {
            Kind = kind,
            Pattern = ScanPattern,
            CaseInsensitive = kind != MemoryScanKind.Utf16 && ScanCaseInsensitive,
            ExecutableOnly = ScanExecutableOnly,
        };

        try
        {
            MemoryScanResult result = await Task.Run(
                () => MemoryScanner.Scan(session.Target, session.Modules, options, progress, _scanCancellation.Token),
                _scanCancellation.Token);

            if (result.Error != null)
            {
                ScanError = result.Error;
                ScanStatus = "Scan failed.";
            }
            else
            {
                Hits = result.Hits;
                ScanStatus = $"{result.Hits.Count:N0} hits · {MemoryRegion.FormatBytes(result.BytesScanned)} scanned · " +
                             $"{result.RegionsScanned:N0} regions" + (result.Truncated ? " · stopped at the result/byte limit" : string.Empty);
                _shell.Log.Info($"Memory scan '{ScanPattern}' ({options.Kind}): {result.Hits.Count} hits", "memory");
            }

            ScanHasRun = true;
        }
        catch (OperationCanceledException)
        {
            ScanStatus = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            ScanError = ex.Message;
            ScanStatus = "Scan failed.";
            _shell.Log.Error("Memory scan failed: " + ex.Message, "memory");
        }
        finally
        {
            IsScanning = false;
            ScanProgress = 100;
            _scanCancellation?.Dispose();
            _scanCancellation = null;
        }
    }

    private void CancelScan()
    {
        _scanCancellation?.Cancel();
        ScanStatus = "Cancelling…";
    }

    private void ShowHitHex()
    {
        TargetSession? session = _shell.Session;
        if (session == null || SelectedHit is not { } hit)
            return;

        try
        {
            ulong start = hit.Address > 256 ? hit.Address - 256 : hit.Address;
            byte[] data = session.Target.ReadBytes(start, 8192, out _);
            var window = new HexWindow(
                $"Hit {hit.AddressLabel} · {hit.ModuleLabel} · {hit.Preview}",
                data,
                $"match at 0x{hit.Address:X} (buffer starts at 0x{start:X})")
            {
                Owner = Application.Current?.MainWindow,
            };
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            _shell.Toast("Hex view failed", ex.Message, ToastKind.Error);
        }
    }

    private void CopyHitAddress()
    {
        if (SelectedHit is { } hit)
            Clipboard.SetText(hit.AddressLabel);
    }

    // ------------------------------------------------------------ runtime

    private async Task CaptureRuntimeAsync()
    {
        TargetSession? session = _shell.Session;
        if (session == null || IsCapturingRuntime)
            return;

        _runtimeCancellation = new CancellationTokenSource();
        IsCapturingRuntime = true;
        RuntimeError = null;
        RuntimeStatus = "Attaching to the CLR…";

        var progress = new Progress<string>(message => RuntimeStatus = message);
        try
        {
            RuntimeSnapshot snapshot = await Task.Run(
                () => RuntimeSnapshotService.Capture(session.Id, 100, TimeSpan.FromSeconds(10), progress,
                    _runtimeCancellation.Token),
                _runtimeCancellation.Token);

            Snapshot = snapshot;
            RuntimeError = snapshot.Success ? null : snapshot.Error;
            RuntimeStatus = snapshot.Success
                ? $"Snapshot in {snapshot.DurationLabel}."
                : "Snapshot unavailable.";
            if (snapshot.Success)
                _shell.Log.Success($"Runtime snapshot: {snapshot.ObjectCountLabel} objects, {snapshot.HeapLabel}", "memory");
            else
                _shell.Log.Warn("Runtime snapshot: " + snapshot.Error, "memory");
        }
        catch (OperationCanceledException)
        {
            RuntimeStatus = "Snapshot cancelled.";
        }
        catch (Exception ex)
        {
            RuntimeError = ex.Message;
            RuntimeStatus = "Snapshot failed.";
        }
        finally
        {
            IsCapturingRuntime = false;
            _runtimeCancellation?.Dispose();
            _runtimeCancellation = null;
        }
    }

    private void CancelRuntime()
    {
        _runtimeCancellation?.Cancel();
        RuntimeStatus = "Cancelling…";
    }

    private async Task CarveAsync()
    {
        TargetSession? session = _shell.Session;
        if (session == null || IsCarving)
            return;

        _carveCancellation = new CancellationTokenSource();
        IsCarving = true;
        CarveError = null;
        CarveProgress = 0;
        CarvedAssemblies = Array.Empty<CarvedAssembly>();
        CarveStatus = "Scanning committed memory for MZ images…";

        var options = new CarveOptions
        {
            OutputDirectory = CarveOutputDirectory,
            IncludeFrameworkAssemblies = IncludeFrameworkAssemblies,
            MaxCandidates = 4096,
            MaxScanBytes = 512L * 1024 * 1024,
        };

        var progress = new Progress<CarveProgress>(value =>
        {
            // Byte-based progress: the bar mirrors the page walk instead of the
            // (growing, non-linear) number of MZ candidates found so far.
            CarveProgress = value.BytesScanned <= 0
                ? 0
                : Math.Min(99, value.BytesScanned * 100.0 / Math.Max(1, options.MaxScanBytes));
            CarveStatus = $"{value.Candidates:N0} MZ candidates · {MemoryRegion.FormatBytes(value.BytesScanned)} scanned · " +
                          $"{value.Saved:N0} saved";
        });

        try
        {
            CarveResult result = await Task.Run(
                () => new AssemblyCarver().Carve(session.Target, session.Modules, options, progress,
                    _carveCancellation.Token),
                _carveCancellation.Token);

            CarvedAssemblies = result.Assemblies;
            CarveProgress = 100;
            CarveStatus = result.Error != null
                ? "Carve stopped: " + result.Error
                : $"{result.Assemblies.Count:N0} saved · {result.Candidates:N0} candidates · " +
                  $"{MemoryRegion.FormatBytes(result.BytesScanned)} scanned" +
                  (result.SkippedFramework > 0 ? $" · {result.SkippedFramework} framework skipped" : string.Empty) +
                  (result.Duplicates > 0 ? $" · {result.Duplicates} duplicates" : string.Empty);
            CarveError = result.Error;

            if (result.Assemblies.Count > 0)
            {
                _shell.Toast("Assemblies carved",
                    $"{result.Assemblies.Count} file(s) → {Path.GetFileName(CarveOutputDirectory)}",
                    ToastKind.Success);
                _shell.Log.Success(
                    $"Carved {result.Assemblies.Count} assemblies from memory → {CarveOutputDirectory}", "dump");
            }
            else
            {
                _shell.Toast("No new assemblies", "The page walk did not find a dumpable managed image.", ToastKind.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            CarveStatus = "Carve cancelled.";
        }
        catch (Exception ex)
        {
            CarveError = ex.Message;
            CarveStatus = "Carve failed.";
            _shell.Log.Error("Carve failed: " + ex.Message, "dump");
        }
        finally
        {
            IsCarving = false;
            _carveCancellation?.Dispose();
            _carveCancellation = null;
        }
    }

    private void CancelCarve()
    {
        _carveCancellation?.Cancel();
        CarveStatus = "Cancelling…";
    }

    private void OpenCarveFolder()
    {
        try
        {
            Directory.CreateDirectory(CarveOutputDirectory);
            Process.Start("explorer.exe", CarveOutputDirectory);
        }
        catch (Exception ex)
        {
            _shell.Toast("Could not open the folder", ex.Message, ToastKind.Warning);
        }
    }

}
