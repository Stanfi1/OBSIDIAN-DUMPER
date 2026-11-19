using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using Obsidian.Common;
using Obsidian.Dumper.Core;
using Obsidian.Dumper.Core.Injection;
using Obsidian.Dumper.Core.Model;

namespace Obsidian.ViewModels;

public enum InjectionStatusKind
{
    Neutral,
    Running,
    Success,
    Warning,
    Error,
}

/// <summary>One injection technique with operator-facing copy.</summary>
public sealed class InjectionMethodOption
{
    public InjectionMethod Method { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string Badge { get; init; } = string.Empty;

    /// <summary>Only the 64-bit techniques; cross-bitness delivery is refused by the engine.</summary>
    public bool RequiresX64 { get; init; }
}

/// <summary>One completed injection attempt, kept in the page history.</summary>
public sealed class InjectionRecord
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public int TargetPid { get; init; }
    public string DllPath { get; init; } = string.Empty;
    public string DllName => Path.GetFileName(DllPath);
    public string TargetLabel { get; init; } = string.Empty;
    public string MethodLabel { get; init; } = string.Empty;
    public bool Success { get; init; }
    public bool IsManaged { get; init; }
    public string Message { get; init; } = string.Empty;
    public string RemoteBaseLabel { get; init; } = "—";
    public string Win32Label { get; init; } = "—";
    public string NtStatusLabel { get; init; } = "—";
    public string DurationLabel { get; init; } = "—";
    public string NotesLabel { get; init; } = "—";
    public string OutcomeLabel => Success ? "loaded" : "failed";
    public string TimeLabel => Timestamp.ToString("HH:mm:ss");
}

/// <summary>
/// DLL injection page: pick a library, pick a technique, inject it into the
/// attached target, inspect the outcome and unload it again. Everything is
/// delegated to <see cref="DllInjector"/> in the engine, so the UI never owns
/// process handles or injection logic.
/// </summary>
public sealed class InjectViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly DllInjector _injector;
    private string _lastDllDirectory = string.Empty;

    private string _dllPath = string.Empty;
    private InjectionMethodOption _selectedMethod;
    private bool _isBusy;
    private string _statusText = "Choose a library and a technique, then inject.";
    private InjectionStatusKind _statusKind = InjectionStatusKind.Neutral;
    private InjectionRecord? _lastResult;
    private InjectionRecord? _selectedHistory;
    private bool _hasCompatibilityMessage;
    private bool _isCompatible;
    private string _compatibilityMessage = string.Empty;
    private string _dllInfo = "No library selected.";
    private bool _isManagedAssembly;
    private string _managedAssemblyPath = string.Empty;
    private string _managedTypeName = string.Empty;
    private string _managedMethodName = string.Empty;
    private string _managedArgument = string.Empty;
    private string _managedStatus = "Managed injection runs a .NET Framework 4.x x64 entry point: static int Method(string).";
    private bool _isManagedBusy;
    private string _targetName = "No target";
    private string _targetRuntime = "—";
    private string _targetArch = "—";
    private string _targetPid = "—";

    public InjectViewModel(ShellViewModel shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _injector = new DllInjector(shell.Log);

        Methods =
        [
            new InjectionMethodOption
            {
                Method = InjectionMethod.CreateRemoteThread,
                Title = "CreateRemoteThread",
                Summary = "Most compatible. Spawns a remote thread that calls LoadLibraryW.",
                Detail = "The classic technique. A thread is created in the target with " +
                         "LoadLibraryW as its entry point; the injected DLL is loaded by the " +
                         "normal loader, so dependencies and TLS behave exactly like a " +
                         "legitimate load. Some hardened processes block remote threads.",
                Badge = "recommended",
            },
            new InjectionMethodOption
            {
                Method = InjectionMethod.NtCreateThreadEx,
                Title = "NtCreateThreadEx",
                Summary = "Lower-level thread creation through ntdll; x64 targets only.",
                Detail = "Same effect as CreateRemoteThread but the thread is created through " +
                         "the native NtCreateThreadEx call instead of the kernel32 wrapper. " +
                         "Useful when a security product hooks CreateRemoteThread. " +
                         "Only supported for x64 targets.",
                Badge = "x64 only",
                RequiresX64 = true,
            },
            new InjectionMethodOption
            {
                Method = InjectionMethod.QueueUserApc,
                Title = "QueueUserAPC",
                Summary = "Queues LoadLibraryW to alertable threads; x64 targets only, best effort.",
                Detail = "LoadLibraryW is queued as an asynchronous procedure call on every " +
                         "thread of the target. The library only loads when a thread enters an " +
                         "alertable wait, so this can silently do nothing in a process whose " +
                         "threads never wait alertably. No new thread is created. Cross-bitness " +
                         "APC delivery into a WOW64 target is refused because it destabilises the target.",
                Badge = "x64 only",
                RequiresX64 = true,
            },
        ];

        _selectedMethod = Methods[0];

        BrowseDllCommand = new RelayCommand(BrowseDll);
        OpenDllFolderCommand = new RelayCommand(OpenDllFolder, () => _dllPath.Length > 0);
        ClearHistoryCommand = new RelayCommand(ClearHistory, () => History.Count > 0);
        CopyResultCommand = new RelayCommand(CopyResult, () => _lastResult != null);
        GoToActivityCommand = new RelayCommand(() => _shell.NavigateTo("log"));
        GoToWorkspaceCommand = new RelayCommand(() => _shell.NavigateTo("workspace"));
        RescanModulesCommand = new RelayCommand(RescanModules, () => _shell.HasTarget);
        InjectCommand = new AsyncRelayCommand(InjectAsync, () => CanInject);
        BrowseManagedAssemblyCommand = new RelayCommand(BrowseManagedAssembly);
        InjectManagedCommand = new AsyncRelayCommand(InjectManagedAsync, () => CanInjectManaged);
        UnloadCommand = new AsyncRelayCommand(UnloadAsync, () => CanUnload);
    }

    public ShellViewModel Shell => _shell;

    public IReadOnlyList<InjectionMethodOption> Methods { get; }
    public ObservableCollection<InjectionRecord> History { get; } = new();

    public RelayCommand BrowseDllCommand { get; }
    public RelayCommand OpenDllFolderCommand { get; }
    public RelayCommand ClearHistoryCommand { get; }
    public RelayCommand CopyResultCommand { get; }
    public RelayCommand GoToActivityCommand { get; }
    public RelayCommand GoToWorkspaceCommand { get; }
    public RelayCommand RescanModulesCommand { get; }
    public AsyncRelayCommand InjectCommand { get; }
    public AsyncRelayCommand UnloadCommand { get; }
    public RelayCommand BrowseManagedAssemblyCommand { get; }
    public AsyncRelayCommand InjectManagedCommand { get; }

    /// <summary>Ask for confirmation before every injection (settings-driven).</summary>
    public bool ConfirmBeforeInject { get; set; } = true;

    // ------------------------------------------------------------------ target

    public bool HasTarget => _shell.Session != null;

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

    public string TargetArch
    {
        get => _targetArch;
        private set => Set(ref _targetArch, value);
    }

    public string TargetPid
    {
        get => _targetPid;
        private set => Set(ref _targetPid, value);
    }

    public string TargetSummary => HasTarget
        ? $"{TargetName} · pid {TargetPid} · {TargetArch} · {TargetRuntime}"
        : "Attach to a process first — a library can only be written into a live target.";

    /// <summary>
    /// Called by the shell after a target is attached or detached. Also syncs
    /// the persisted injection preferences (default technique, last DLL folder).
    /// </summary>
    public void Refresh()
    {
        TargetSession? session = _shell.Session;
        if (session == null)
        {
            TargetName = "No target";
            TargetRuntime = "—";
            TargetArch = "—";
            TargetPid = "—";
        }
        else
        {
            TargetName = session.Process.DisplayName;
            TargetRuntime = session.Process.RuntimeLabel;
            TargetArch = session.Process.ArchitectureLabel;
            TargetPid = session.Id.ToString();
        }

        try
        {
            InjectionMethod preferred = _shell.SettingsStore.Current.DefaultInjectionMethod;
            InjectionMethodOption? match = Methods.FirstOrDefault(m => m.Method == preferred);
            if (match != null && !ReferenceEquals(match, _selectedMethod))
                SelectedMethod = match;

            string settingsDirectory = _shell.SettingsStore.Current.InjectionDllDirectory;
            if (_lastDllDirectory.Length == 0 && !string.IsNullOrWhiteSpace(settingsDirectory))
                _lastDllDirectory = settingsDirectory;
        }
        catch
        {
            // Injection preferences are a convenience; never break the page.
        }

        Raise(nameof(HasTarget), nameof(TargetSummary), nameof(ConfirmBeforeInject));
        ValidateDll();
        RaiseManagedAvailability();
        RaiseCommands();
    }

    // --------------------------------------------------------------------- dll

    public string DllPath
    {
        get => _dllPath;
        set
        {
            string normalized = (value ?? string.Empty).Trim().Trim('"');
            if (!Set(ref _dllPath, normalized))
                return;

            Raise(nameof(HasDll), nameof(DllFileName), nameof(InjectButtonLabel), nameof(CanInject));
            OpenDllFolderCommand.RaiseCanExecuteChanged();
            ValidateDll();
        }
    }

    public bool HasDll => _dllPath.Length > 0;
    public string DllFileName => _dllPath.Length == 0 ? "no library selected" : Path.GetFileName(_dllPath);

    public string DllInfo
    {
        get => _dllInfo;
        private set => Set(ref _dllInfo, value);
    }

    public bool IsManagedAssembly
    {
        get => _isManagedAssembly;
        private set => Set(ref _isManagedAssembly, value);
    }

    public InjectionMethodOption SelectedMethod
    {
        get => _selectedMethod;
        set
        {
            if (value == null || !Set(ref _selectedMethod, value))
                return;
            Raise(nameof(MethodDetail), nameof(InjectButtonLabel), nameof(MethodSupportedByTarget),
                nameof(CanInject));
            InjectCommand.RaiseCanExecuteChanged();
            ValidateDll();
        }
    }

    public string MethodDetail => _selectedMethod.Detail;

    /// <summary>
    /// NtCreateThreadEx and QueueUserAPC are x64-only in the engine; cross-bitness
    /// delivery is refused before allocation, so the UI blocks it up front.
    /// </summary>
    public bool MethodSupportedByTarget => !_selectedMethod.RequiresX64
                                           || _shell.Session?.Process.Architecture == ProcessArchitecture.X64;

    public bool HasCompatibilityMessage
    {
        get => _hasCompatibilityMessage;
        private set => Set(ref _hasCompatibilityMessage, value);
    }

    public bool IsCompatibleTarget
    {
        get => _isCompatible;
        private set => Set(ref _isCompatible, value);
    }

    public string CompatibilityMessage
    {
        get => _compatibilityMessage;
        private set => Set(ref _compatibilityMessage, value);
    }

    // ------------------------------------------------------------------ status

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value))
                return;
            Raise(nameof(CanInject), nameof(CanUnload), nameof(InjectButtonLabel));
            RaiseCommands();
        }
    }

    public string ManagedAssemblyPath
    {
        get => _managedAssemblyPath;
        set
        {
            if (Set(ref _managedAssemblyPath, value))
            {
                Raise(nameof(CanInjectManaged), nameof(ManagedAssemblyName));
                InjectManagedCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ManagedAssemblyName => _managedAssemblyPath.Length == 0
        ? "No assembly selected."
        : Path.GetFileName(_managedAssemblyPath);

    public string ManagedTypeName
    {
        get => _managedTypeName;
        set
        {
            if (Set(ref _managedTypeName, value))
                RaiseManagedAvailability();
        }
    }

    public string ManagedMethodName
    {
        get => _managedMethodName;
        set
        {
            if (Set(ref _managedMethodName, value))
                RaiseManagedAvailability();
        }
    }

    public string ManagedArgument
    {
        get => _managedArgument;
        set => Set(ref _managedArgument, value);
    }

    public string ManagedStatus
    {
        get => _managedStatus;
        private set => Set(ref _managedStatus, value);
    }

    public bool IsManagedBusy
    {
        get => _isManagedBusy;
        private set
        {
            if (Set(ref _isManagedBusy, value))
                RaiseManagedAvailability();
        }
    }

    public bool CanInjectManaged => _shell.Session is { } session
                                   && !IsManagedBusy
                                   && session.Process.Architecture == ProcessArchitecture.X64
                                   && session.Clr?.Flavor == ClrFlavor.Framework
                                   && _managedAssemblyPath.Length > 0
                                   && File.Exists(_managedAssemblyPath)
                                   && _managedTypeName.Trim().Length > 0
                                   && _managedMethodName.Trim().Length > 0;

    public string ManagedTargetHint => _shell.Session is { } session
        ? session.Clr?.Flavor == ClrFlavor.Framework && session.Process.Architecture == ProcessArchitecture.X64
            ? "Ready: .NET Framework 4.x x64. The entry point must return int and take one string."
            : "Managed injection needs a .NET Framework 4.x x64 target; this target is " +
              $"{session.Clr?.Flavor.ToString() ?? "no CLR"} / {session.Process.ArchitectureLabel}."
        : "Attach to a .NET Framework 4.x x64 target first.";

    private void RaiseManagedAvailability()
    {
        Raise(nameof(CanInjectManaged), nameof(ManagedTargetHint));
        InjectManagedCommand.RaiseCanExecuteChanged();
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public InjectionStatusKind StatusKind
    {
        get => _statusKind;
        private set => Set(ref _statusKind, value);
    }

    public string InjectButtonLabel => IsBusy
        ? "Injecting…"
        : _selectedMethod.Method switch
        {
            InjectionMethod.NtCreateThreadEx => "Inject via NtCreateThreadEx",
            InjectionMethod.QueueUserApc => "Queue LoadLibraryW (APC)",
            _ => "Inject library",
        };

    public bool CanInject => _shell.Session != null
                             && !IsBusy
                             && _dllPath.Length > 0
                             && File.Exists(_dllPath)
                             && MethodSupportedByTarget
                             && (!_hasCompatibilityMessage || _isCompatible);

    public bool CanUnload => _shell.Session != null
                             && !IsBusy
                             && CanUnloadRecord(SelectedHistory ?? _lastResult);

    private static bool CanUnloadRecord(InjectionRecord? record)
        // Managed injection runs through the native agent host: the managed
        // assembly itself is not loaded as a module, so it can never be unloaded.
        => record is { Success: true, IsManaged: false } && record.DllName.Length > 0;

    // ------------------------------------------------------------------ result

    public InjectionRecord? LastResult
    {
        get => _lastResult;
        private set
        {
            if (!Set(ref _lastResult, value))
                return;
            Raise(nameof(HasResult), nameof(ResultTitle), nameof(ResultDetail), nameof(ResultLevel),
                nameof(CanUnload));
            CopyResultCommand.RaiseCanExecuteChanged();
            UnloadCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasResult => _lastResult != null;

    public string ResultTitle => _lastResult switch
    {
        null => "No injection yet",
        { Success: true, IsManaged: true } => "Managed entry point executed",
        { Success: false, IsManaged: true } => "Managed injection failed",
        { Success: true } => "Library loaded",
        _ => "Injection failed",
    };

    public string ResultDetail => _lastResult?.Message ?? "Run an injection to see the full report here.";

    public InjectionStatusKind ResultLevel => _lastResult switch
    {
        null => InjectionStatusKind.Neutral,
        { Success: true } => InjectionStatusKind.Success,
        _ => InjectionStatusKind.Error,
    };

    public InjectionRecord? SelectedHistory
    {
        get => _selectedHistory;
        set
        {
            if (!Set(ref _selectedHistory, value))
                return;
            Raise(nameof(CanUnload), nameof(SelectedRecordLabel));
            UnloadCommand.RaiseCanExecuteChanged();
        }
    }

    public string SelectedRecordLabel => _selectedHistory == null
        ? "Select a history entry to unload it."
        : $"Selected: {_selectedHistory.DllName} · {_selectedHistory.TimeLabel}";

    public bool HasHistory => History.Count > 0;

    // ---------------------------------------------------------------- commands

    private void BrowseManagedAssembly()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select a .NET Framework assembly",
            Filter = "Managed assemblies (*.dll;*.exe)|*.dll;*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = _lastDllDirectory.Length > 0 ? _lastDllDirectory : null,
        };
        if (dialog.ShowDialog() != true)
            return;

        ManagedAssemblyPath = dialog.FileName;
        _lastDllDirectory = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
        ManagedStatus = "Assembly selected. Enter the static int Method(string) entry point.";
    }

    private async Task InjectManagedAsync()
    {
        TargetSession? session = _shell.Session;
        if (session == null || !CanInjectManaged)
            return;

        if (ConfirmBeforeInject)
        {
            MessageBoxResult confirm = MessageBox.Show(
                "Inject and execute '" + ManagedAssemblyName + "' in " + TargetName + "?\n\n" +
                "The method " + ManagedTypeName + "." + ManagedMethodName +
                " will run inside the target process.",
                "Confirm managed injection",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (confirm != MessageBoxResult.OK)
                return;
        }

        IsManagedBusy = true;
        ManagedStatus = "Staging the managed host…";
        try
        {
            var service = new ManagedInjectionService(_shell.Log);
            ManagedInjectionResult result = await service.InjectAsync(
                session,
                ManagedAssemblyPath,
                ManagedTypeName.Trim(),
                ManagedMethodName.Trim(),
                ManagedArgument,
                new Progress<string>(message => ManagedStatus = message));

            ManagedStatus = result.Message;
            bool success = result.Success;
            History.Insert(0, new InjectionRecord
            {
                TargetPid = session.Id,
                DllPath = ManagedAssemblyPath,
                TargetLabel = TargetName,
                MethodLabel = "managed · " + ManagedTypeName.Trim() + "." + ManagedMethodName.Trim(),
                Success = success,
                IsManaged = true,
                Message = result.Message,
                RemoteBaseLabel = "exit " + result.ExitCode,
                Win32Label = "—",
                NtStatusLabel = result.ResultRaw ?? "—",
                DurationLabel = result.DurationLabel,
                NotesLabel = result.WorkDirectory == null ? "—" : Path.GetFileName(result.WorkDirectory),
            });
            while (History.Count > 25)
                History.RemoveAt(History.Count - 1);
            SelectedHistory = History[0];
            LastResult = History[0];
            Raise(nameof(HasHistory));
            ClearHistoryCommand.RaiseCanExecuteChanged();

            _shell.Toast(success ? "Managed assembly injected" : "Managed injection failed",
                result.Message, success ? ToastKind.Success : ToastKind.Error);
        }
        catch (Exception ex)
        {
            ManagedStatus = "Managed injection failed: " + ex.Message;
            _shell.Log.Error("Managed injection failed: " + ex.Message, "inject");
            _shell.Toast("Managed injection failed", ex.Message, ToastKind.Error);
        }
        finally
        {
            IsManagedBusy = false;
        }
    }

    private void BrowseDll()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select a library to inject",
            Filter = "Native libraries (*.dll)|*.dll|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        string initialDirectory = _lastDllDirectory;
        try
        {
            string persisted = _shell.SettingsStore.Current.InjectionDllDirectory;
            if (!string.IsNullOrWhiteSpace(persisted))
                initialDirectory = persisted;
        }
        catch
        {
            // Preferences are optional.
        }

        if (initialDirectory.Length > 0 && Directory.Exists(initialDirectory))
            dialog.InitialDirectory = initialDirectory;

        if (dialog.ShowDialog() == true)
        {
            DllPath = dialog.FileName;
            string? directory = Path.GetDirectoryName(dialog.FileName);
            if (!string.IsNullOrEmpty(directory))
            {
                _lastDllDirectory = directory;
                try
                {
                    _shell.SettingsStore.Current.InjectionDllDirectory = directory;
                    _shell.SettingsStore.Save();
                }
                catch
                {
                    // Persisting the folder is a convenience only.
                }
            }
        }
    }

    private void OpenDllFolder()
    {
        if (_dllPath.Length == 0)
            return;
        string? directory = Path.GetDirectoryName(_dllPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return;
        try
        {
            var info = new ProcessStartInfo("explorer.exe", $"/select,\"{_dllPath}\"") { UseShellExecute = true };
            Process.Start(info);
        }
        catch (Exception ex)
        {
            _shell.Log.Warn("Could not open the DLL folder: " + ex.Message, "inject");
        }
    }

    private void ClearHistory()
    {
        History.Clear();
        SelectedHistory = null;
        Raise(nameof(HasHistory));
        ClearHistoryCommand.RaiseCanExecuteChanged();
    }

    private void CopyResult()
    {
        InjectionRecord? record = _lastResult;
        if (record == null)
            return;

        string text = string.Join(Environment.NewLine,
            $"Target       : {record.TargetLabel}",
            $"Library      : {record.DllPath}",
            $"Method       : {record.MethodLabel}",
            $"Outcome      : {record.OutcomeLabel}",
            $"Module base  : {record.RemoteBaseLabel}",
            $"Win32 error  : {record.Win32Label}",
            $"NTSTATUS     : {record.NtStatusLabel}",
            $"Duration     : {record.DurationLabel}",
            $"Notes        : {record.NotesLabel}",
            $"Message      : {record.Message}");

        TrySetClipboard(text, "injection report");
    }

    private void RescanModules()
    {
        if (_shell.Session == null)
            return;
        _shell.Workspace.Rescan();
    }

    internal void TrySetClipboard(string text, string what)
    {
        try
        {
            Clipboard.SetText(text);
            _shell.Toast("Copied", $"The {what} is on the clipboard.", ToastKind.Info);
        }
        catch (Exception ex)
        {
            _shell.Log.Warn($"Clipboard is not available ({ex.Message})", "inject");
            _shell.Toast("Clipboard unavailable", "Another process is holding the clipboard.", ToastKind.Warning);
        }
    }

    // ---------------------------------------------------------------- injection

    private async Task InjectAsync()
    {
        TargetSession? session = _shell.Session;
        if (session == null)
        {
            _shell.Toast("No target", "Attach to a process before injecting a library.", ToastKind.Warning);
            return;
        }

        string path = _dllPath;
        if (!File.Exists(path))
        {
            _shell.Toast("Library not found",
                path.Length == 0 ? "Choose a DLL first." : $"The file does not exist:\n{path}", ToastKind.Warning);
            return;
        }

        InjectionMethodOption method = _selectedMethod;

        bool askConfirmation = ConfirmBeforeInject;
        try
        {
            askConfirmation &= _shell.SettingsStore.Current.ConfirmInjection;
        }
        catch
        {
            // Fall back to the page default when settings are unavailable.
        }

        if (askConfirmation)
        {
            string message = $"Inject {Path.GetFileName(path)} into {session.Process.DisplayName} (pid {session.Id}) " +
                             $"using {method.Title}?\n\nThis writes into the target process and loads code in it. " +
                             "Only do this with software you own or are authorised to analyse.";
            if (!Confirm("Confirm DLL injection", message, "Inject"))
                return;
        }

        IsBusy = true;
        StatusKind = InjectionStatusKind.Running;
        StatusText = $"Injecting {Path.GetFileName(path)} into {session.Process.DisplayName}…";
        _shell.SetStatus(StatusText);
        _shell.Log.Info($"Injecting {Path.GetFileName(path)} into pid {session.Id} via {method.Title}", "inject");

        InjectionRecord record;
        try
        {
            InjectionResult result = await Task.Run(() =>
                _injector.Inject(session, path, method.Method)).ConfigureAwait(true);

            record = CreateRecord(path, method, result, session.Id);
            foreach (string note in result.Notes)
                _shell.Log.Trace("inject: " + note, "inject");

            if (result.Success)
            {
                _shell.Log.Success(
                    $"Injected {Path.GetFileName(path)} into pid {session.Id} via {method.Title}" +
                    (result.RemoteModuleBase != 0 ? $" (base 0x{result.RemoteModuleBase:X})" : string.Empty),
                    "inject");
                StatusKind = InjectionStatusKind.Success;
                StatusText = $"Loaded {Path.GetFileName(path)}" +
                             (result.RemoteModuleBase != 0 ? $" at 0x{result.RemoteModuleBase:X}" : string.Empty);
                _shell.Toast("Library injected",
                    $"{Path.GetFileName(path)} is loaded in {session.Process.DisplayName}.", ToastKind.Success);
            }
            else
            {
                _shell.Log.Error($"Injection of {Path.GetFileName(path)} failed: {result.Message}", "inject");
                StatusKind = InjectionStatusKind.Error;
                StatusText = "Injection failed — " + result.Message;
                _shell.Toast("Injection failed", result.Message, ToastKind.Error);
            }

            _shell.SetStatus(StatusText);
        }
        catch (Exception ex)
        {
            record = new InjectionRecord
            {
                DllPath = path,
                TargetPid = session.Id,
                TargetLabel = $"pid {session.Id}",
                MethodLabel = method.Title,
                Success = false,
                Message = $"{ex.GetType().Name}: {ex.Message}",
            };
            StatusKind = InjectionStatusKind.Error;
            StatusText = "Injection failed — " + ex.Message;
            _shell.Log.Error("Injection threw: " + ex, "inject");
            _shell.Toast("Injection failed", ex.Message, ToastKind.Error);
            _shell.SetStatus(StatusText);
        }
        finally
        {
            IsBusy = false;
        }

        History.Insert(0, record);
        while (History.Count > 25)
            History.RemoveAt(History.Count - 1);
        LastResult = record;
        SelectedHistory = record;
        Raise(nameof(HasHistory));
        ClearHistoryCommand.RaiseCanExecuteChanged();
        Raise(nameof(CanUnload), nameof(SelectedRecordLabel));
        UnloadCommand.RaiseCanExecuteChanged();
        InjectCommand.RaiseCanExecuteChanged();
    }

    private async Task UnloadAsync()
    {
        TargetSession? session = _shell.Session;
        InjectionRecord? record = SelectedHistory ?? _lastResult;
        if (session == null || !CanUnloadRecord(record))
            return;

        string moduleName = record!.DllName;
        IsBusy = true;
        try
        {
            InjectionResult result = await Task.Run(() =>
                _injector.Unload(session.Id, moduleName)).ConfigureAwait(true);

            if (result.Success)
            {
                _shell.Log.Success($"Unloaded {moduleName} from pid {session.Id}", "inject");
                _shell.Toast("Library unloaded", $"{moduleName} was released from the target.", ToastKind.Success);
                StatusKind = InjectionStatusKind.Success;
                StatusText = $"Unloaded {moduleName}.";
            }
            else
            {
                _shell.Log.Error($"Unloading {moduleName} failed: {result.Message}", "inject");
                _shell.Toast("Unload failed", result.Message, ToastKind.Error);
                StatusKind = InjectionStatusKind.Error;
                StatusText = "Unload failed — " + result.Message;
            }

            _shell.SetStatus(StatusText);
        }
        catch (Exception ex)
        {
            _shell.Log.Error("Unload threw: " + ex, "inject");
            _shell.Toast("Unload failed", ex.Message, ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Best-effort cleanup used by the shell when the operator detaches with
    /// "auto-unload on detach" enabled: release every library this session
    /// injected into that target, newest first. Never throws; runs synchronously
    /// because <see cref="ShellViewModel.Detach"/> must dispose the session
    /// immediately afterwards.
    /// </summary>
    public void UnloadAllOnDetach()
    {
        TargetSession? session = _shell.Session;
        if (session == null)
            return;

        int pid = session.Id;
        List<string> names = History
            .Where(r => r.Success && !r.IsManaged && r.TargetPid == pid && r.DllName.Length > 0)
            .Select(r => r.DllName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (names.Count == 0)
            return;

        _shell.Log.Info($"Auto-unloading {names.Count} injected librar{(names.Count == 1 ? "y" : "ies")} from pid {pid}", "inject");
        foreach (string name in names)
        {
            try
            {
                InjectionResult result = _injector.Unload(pid, name);
                if (result.Success)
                    _shell.Log.Success($"Unloaded {name} from pid {pid}", "inject");
                else
                    _shell.Log.Warn($"Unloading {name} failed: {result.Message}", "inject");
            }
            catch (Exception ex)
            {
                _shell.Log.Warn($"Unloading {name} threw: {ex.Message}", "inject");
            }
        }
    }

    private static InjectionRecord CreateRecord(string path, InjectionMethodOption method, InjectionResult result, int pid)
        => new()
        {
            DllPath = path,
            TargetPid = pid,
            TargetLabel = $"pid {pid}",
            MethodLabel = method.Title,
            Success = result.Success,
            Message = result.Message,
            RemoteBaseLabel = result.RemoteModuleBase == 0 ? "—" : $"0x{result.RemoteModuleBase:X}",
            Win32Label = result.Win32Error == 0 ? "—" : $"{result.Win32Error} (0x{result.Win32Error:X})",
            NtStatusLabel = result.NtStatus == 0 ? "—" : $"0x{result.NtStatus:X8}",
            DurationLabel = result.DurationMs <= 0 ? "—" : $"{result.DurationMs} ms",
            NotesLabel = result.Notes.Count == 0 ? "—" : string.Join("; ", result.Notes),
        };

    private bool Confirm(string title, string message, string confirmLabel)
    {
        bool confirmed = _shell.Confirm(title, message, confirmLabel, destructive: true);
        if (!confirmed)
            _shell.Log.Info($"Injection cancelled by the operator ({Path.GetFileName(_dllPath)}).", "inject");
        return confirmed;
    }

    // --------------------------------------------------------------- validation

    private void ValidateDll()
    {
        string path = _dllPath;
        if (path.Length == 0)
        {
            HasCompatibilityMessage = false;
            IsCompatibleTarget = false;
            CompatibilityMessage = string.Empty;
            DllInfo = "No library selected.";
            IsManagedAssembly = false;
            Raise(nameof(CanInject));
            InjectCommand.RaiseCanExecuteChanged();
            return;
        }

        if (!File.Exists(path))
        {
            HasCompatibilityMessage = true;
            IsCompatibleTarget = false;
            CompatibilityMessage = "The file does not exist.";
            DllInfo = path;
            IsManagedAssembly = false;
            Raise(nameof(CanInject));
            InjectCommand.RaiseCanExecuteChanged();
            return;
        }

        string info;
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            long size = new FileInfo(path).Length;
            string versionText = string.IsNullOrWhiteSpace(version.FileVersion) ? string.Empty : $" · {version.FileVersion}";
            string company = string.IsNullOrWhiteSpace(version.CompanyName)
                ? Path.GetExtension(path).TrimStart('.').ToUpperInvariant()
                : version.CompanyName;
            info = $"{FormatBytes(size)} · {company}{versionText}";
        }
        catch
        {
            info = path;
        }

        IsManagedAssembly = LooksManaged(path);
        DllInfo = info + (IsManagedAssembly ? " · .NET assembly" : " · native");

        if (IsManagedAssembly)
        {
            HasCompatibilityMessage = true;
            IsCompatibleTarget = false;
            CompatibilityMessage =
                "This is a managed .NET assembly. LoadLibraryW normally refuses assemblies without " +
                "native exports (error 193); use the MANAGED ASSEMBLY card below to run a .NET " +
                "Framework entry point inside the target.";
        }
        else if (_shell.Session != null)
        {
            bool compatible;
            string? reason = null;
            try
            {
                compatible = DllInjector.IsCompatible(_shell.Session.Id, path, out reason);
            }
            catch (Exception ex)
            {
                compatible = false;
                reason = ex.Message;
            }

            HasCompatibilityMessage = true;
            IsCompatibleTarget = compatible;
            CompatibilityMessage = compatible
                ? "The library matches the target architecture."
                : reason ?? "The library cannot be loaded into this target.";
        }
        else
        {
            HasCompatibilityMessage = false;
            IsCompatibleTarget = false;
            CompatibilityMessage = string.Empty;
        }

        if (!IsManagedAssembly && _shell.Session != null && !MethodSupportedByTarget)
        {
            HasCompatibilityMessage = true;
            IsCompatibleTarget = false;
            CompatibilityMessage =
                $"{_selectedMethod.Title} is only supported for x64 targets; this target is " +
                $"{_shell.Session.Process.ArchitectureLabel}. Use CreateRemoteThread instead.";
        }

        Raise(nameof(CanInject), nameof(HasDll), nameof(MethodSupportedByTarget));
        RaiseManagedAvailability();
        InjectCommand.RaiseCanExecuteChanged();
        UnloadCommand.RaiseCanExecuteChanged();
    }

    private static bool LooksManaged(string path)
    {
        try
        {
            AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    private void RaiseCommands()
    {
        InjectCommand.RaiseCanExecuteChanged();
        UnloadCommand.RaiseCanExecuteChanged();
        RescanModulesCommand.RaiseCanExecuteChanged();
        Raise(nameof(CanInject), nameof(CanUnload), nameof(ManagedTargetHint));
    }
}
