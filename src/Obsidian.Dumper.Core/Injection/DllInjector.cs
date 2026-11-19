using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Obsidian.Dumper.Core.Diagnostics;
using Obsidian.Dumper.Core.Model;
using Obsidian.Dumper.Core.Native;
using Obsidian.Dumper.Shared;

namespace Obsidian.Dumper.Core.Injection;

/// <summary>Thread-creation strategy used to run <c>LoadLibraryW</c> inside the target.</summary>
public enum InjectionMethod
{
    /// <summary>
    /// Classic <c>kernel32!CreateRemoteThread</c>. Works for x64 targets and for
    /// WOW64 (x86) targets, where the 32-bit kernel32 export address is used.
    /// </summary>
    CreateRemoteThread = 0,

    /// <summary>
    /// <c>ntdll!NtCreateThreadEx</c>. x64 targets only; the NTSTATUS result is
    /// reported verbatim. Useful when user-mode hooks block CreateRemoteThread.
    /// </summary>
    NtCreateThreadEx = 1,

    /// <summary>
    /// <c>kernel32!QueueUserAPC(LoadLibraryW, ...)</c> queued on every target
    /// thread. x64 targets only: cross-bitness APC delivery into a WOW64 process
    /// can corrupt the target's loader. Delivery depends on a thread entering an
    /// alertable wait, so the result carries explicit caveats and is reported
    /// asynchronously.
    /// </summary>
    QueueUserApc = 2,
}

/// <summary>
/// Outcome of one injection or unload operation. All failures are expressed
/// here — the injector never throws and never leaks stack traces.
/// </summary>
public sealed class InjectionResult
{
    /// <summary>True when the loader reported success (or the APC variant observed the module).</summary>
    public bool Success { get; init; }

    /// <summary>Method that was requested for this operation.</summary>
    public InjectionMethod Method { get; init; }

    /// <summary>Human readable summary suitable for a log line, a toast or a status bar.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Last Win32 error reported by the failing call, or 0.</summary>
    public uint Win32Error { get; init; }

    /// <summary>Raw NTSTATUS from <see cref="InjectionMethod.NtCreateThreadEx"/>, or 0.</summary>
    public uint NtStatus { get; init; }

    /// <summary>
    /// HMODULE reported by the target's <c>LoadLibraryW</c> when known. For x64
    /// targets the value is resolved from the target module list because the
    /// thread exit code only carries the low 32 bits.
    /// </summary>
    public ulong RemoteModuleBase { get; init; }

    /// <summary>Wall-clock duration of the operation in milliseconds.</summary>
    public int DurationMs { get; init; }

    /// <summary>Extra context (architecture notes, allocation ownership, delivery caveats).</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <inheritdoc />
    public override string ToString()
    {
        string status = Success ? "OK" : "FAILED";
        string baseText = RemoteModuleBase == 0 ? string.Empty : $" base=0x{RemoteModuleBase:X}";
        return $"{status} {Method}{baseText} ({DurationMs} ms): {Message}";
    }
}

/// <summary>
/// Loads and unloads DLLs in a live target process using only documented,
/// out-of-process primitives:
///
/// <list type="bullet">
///   <item><description>the path is written into the target with VirtualAllocEx/WriteProcessMemory,</description></item>
///   <item><description><c>LoadLibraryW</c> is resolved from the target's *own* kernel32 base (module list) plus the export RVA parsed from the matching local kernel32 image,</description></item>
///   <item><description>the call runs on a remote thread (CreateRemoteThread or NtCreateThreadEx) or through an APC.</description></item>
/// </list>
///
/// The injector is defensive by design: every path validates its arguments,
/// reports architecture mismatches before allocating anything, always closes
/// handles and (whenever it is safe) frees the remote buffer. No method throws.
/// </summary>
public sealed class DllInjector
{
    private const int RemoteThreadTimeoutMs = 15_000;
    private const int ApcWaitMs = 5_000;
    private const ushort ImageFileMachineI386 = 0x014C;
    private const ushort ImageFileMachineAmd64 = 0x8664;
    private const ushort ImageFileMachineArm64 = 0xAA64;
    private const uint ImageFileDll = 0x2000;

    private static readonly object ExportCacheLock = new();
    private static readonly Dictionary<string, uint> ExportRvaCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly ILogSink _log;

    /// <summary>Creates an injector. Passing <c>null</c> keeps the engine silent.</summary>
    public DllInjector(ILogSink? log = null)
    {
        _log = log ?? NullLogSink.Instance;
    }

    /// <summary>
    /// Loads <paramref name="dllPath"/> into the process with id <paramref name="processId"/>.
    /// Never throws; failures are described by the returned result.
    /// </summary>
    public InjectionResult Inject(int processId, string dllPath,
        InjectionMethod method = InjectionMethod.CreateRemoteThread, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var notes = new List<string>();
        try
        {
            return InjectCore(processId, dllPath, method, stopwatch, notes, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Fail(method, "The injection was cancelled before it completed.", stopwatch, notes);
        }
        catch (Exception ex)
        {
            return Fail(method, "Unexpected injection failure: " + ex.Message, stopwatch, notes);
        }
    }

    /// <summary>
    /// Loads <paramref name="dllPath"/> into the process represented by an attached session.
    /// The session handle is read-only, so a short-lived injection handle is opened for the operation.
    /// </summary>
    public InjectionResult Inject(TargetSession session, string dllPath,
        InjectionMethod method = InjectionMethod.CreateRemoteThread, CancellationToken cancellationToken = default)
    {
        if (session is null)
        {
            var stopwatch = Stopwatch.StartNew();
            var notes = new List<string>();
            return Fail(method, "No target session was supplied.", stopwatch, notes);
        }

        return Inject(session.Id, dllPath, method, cancellationToken);
    }

    /// <summary>
    /// Unloads a module from the process (by module name, file name or full path)
    /// with a remote <c>FreeLibrary</c> call. Never throws.
    /// </summary>
    public InjectionResult Unload(int processId, string moduleName, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var notes = new List<string>();
        const InjectionMethod method = InjectionMethod.CreateRemoteThread;
        try
        {
            if (processId <= 0)
                return Fail(method, $"Invalid process id {processId}.", stopwatch, notes);
            if (string.IsNullOrWhiteSpace(moduleName))
                return Fail(method, "No module name was supplied.", stopwatch, notes);

            cancellationToken.ThrowIfCancellationRequested();

            using RemoteProcess? target = RemoteProcess.TryOpen(processId, out string? openError, forInjection: true);
            if (target == null)
                return Fail(method, $"Cannot open process {processId} for injection: {openError}", stopwatch, notes);

            IReadOnlyList<(string Name, string Path, ulong Base, uint Size)> modules =
                ProcessDiscovery.QueryModuleList(processId);
            (string Name, string Path, ulong Base, uint Size) match = default;
            bool found = false;
            foreach (var module in modules)
            {
                if (!IsModuleForTargetBitness(module.Path, module.Base, target.Architecture))
                    continue;
                if (!ModuleMatches(module, moduleName))
                    continue;
                match = module;
                found = true;
                break;
            }

            if (!found)
            {
                string? similar = FindSimilarModules(modules, moduleName);
                if (similar != null)
                    notes.Add("Loaded modules with a similar name: " + similar);
                return Fail(method, $"The module '{moduleName}' is not loaded in pid {processId}.", stopwatch, notes);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!TryResolveKernel32Export(target, "FreeLibrary", out ulong freeLibrary, out string? resolveError))
                return Fail(method, resolveError ?? "Could not resolve kernel32!FreeLibrary in the target.", stopwatch, notes);

            if (!target.IsAlive)
                return Fail(method, $"The target process {processId} has already exited.", stopwatch, notes);

            IntPtr thread = NativeMethods.CreateRemoteThread(
                target.Handle,
                IntPtr.Zero,
                IntPtr.Zero,
                (IntPtr)(long)freeLibrary,
                (IntPtr)(long)match.Base,
                0,
                out uint threadId);
            if (thread == IntPtr.Zero)
            {
                int code = Marshal.GetLastWin32Error();
                return Fail(method,
                    $"CreateRemoteThread(FreeLibrary) failed: {Win32ErrorText.DescribeWin32(code)} (Win32 error {code}).",
                    stopwatch, notes, win32: (uint)code);
            }

            try
            {
                notes.Add($"Remote FreeLibrary thread {threadId} targets '{match.Name}' at 0x{match.Base:X}.");

                uint wait = WaitForRemoteThread(thread, RemoteThreadTimeoutMs, cancellationToken, out bool cancelled);
                if (cancelled)
                    return Fail(method, "The unload was cancelled while the remote FreeLibrary thread was running.", stopwatch, notes);
                if (wait == NativeMethods.WaitTimeout)
                    return Fail(method,
                        $"FreeLibrary did not finish in pid {processId} within {RemoteThreadTimeoutMs / 1000} s; the target may be unresponsive or the loader lock may be held.",
                        stopwatch, notes);
                if (wait == NativeMethods.WaitFailed)
                {
                    int code = Marshal.GetLastWin32Error();
                    return Fail(method,
                        $"Waiting for the remote FreeLibrary thread failed: {Win32ErrorText.DescribeWin32(code)} (Win32 error {code}).",
                        stopwatch, notes, win32: (uint)code);
                }

                if (!NativeMethods.GetExitCodeThread(thread, out uint exitCode))
                {
                    int code = Marshal.GetLastWin32Error();
                    return Fail(method,
                        $"The remote FreeLibrary thread finished but its exit code could not be read: {Win32ErrorText.DescribeWin32(code)} (Win32 error {code}).",
                        stopwatch, notes, win32: (uint)code);
                }

                if (exitCode == 0)
                    return Fail(method,
                        $"FreeLibrary returned FALSE for '{match.Name}' (0x{match.Base:X}): the module is still loaded. The loader may still be using it, or another reference exists.",
                        stopwatch, notes);

                notes.Add($"FreeLibrary returned {exitCode} for '{match.Name}'.");

                bool stillLoaded = false;
                foreach (var module in ProcessDiscovery.QueryModuleList(processId))
                {
                    if (module.Base == match.Base && module.Name.Equals(match.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        stillLoaded = true;
                        break;
                    }
                }

                if (stillLoaded)
                    notes.Add("The module is still present in a fresh module snapshot; the loader may keep a reference (delay-loaded imports, thread-local storage callbacks).");

                string message = $"Unloaded '{match.Name}' from pid {processId}.";
                _log.Write(LogLevel.Success, message, "inject");
                return new InjectionResult
                {
                    Success = true,
                    Method = method,
                    Message = message,
                    RemoteModuleBase = match.Base,
                    DurationMs = (int)stopwatch.ElapsedMilliseconds,
                    Notes = notes.ToArray(),
                };
            }
            finally
            {
                NativeMethods.CloseHandle(thread);
            }
        }
        catch (OperationCanceledException)
        {
            return Fail(method, "The unload was cancelled before it completed.", stopwatch, notes);
        }
        catch (Exception ex)
        {
            return Fail(method, "Unexpected unload failure: " + ex.Message, stopwatch, notes);
        }
    }

    /// <summary>
    /// Checks whether <paramref name="dllPath"/> can be loaded by the process
    /// with id <paramref name="processId"/> without allocating anything in it.
    /// <paramref name="reason"/> explains a negative answer (and stays null on success).
    /// </summary>
    public static bool IsCompatible(int processId, string dllPath, out string? reason)
    {
        reason = null;
        try
        {
            if (processId <= 0)
            {
                reason = $"Invalid process id {processId}.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(dllPath))
            {
                reason = "No DLL path was supplied.";
                return false;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(dllPath);
            }
            catch (Exception ex)
            {
                reason = $"The DLL path is not valid: {ex.Message}";
                return false;
            }

            if (!File.Exists(fullPath))
            {
                reason = $"The DLL file does not exist: {fullPath}";
                return false;
            }

            if (!TryReadDllImage(fullPath, out DllImageInfo image, out string? imageError))
            {
                reason = $"The file is not a usable PE image: {imageError}";
                return false;
            }

            using RemoteProcess? target = RemoteProcess.TryOpen(processId, out string? openError);
            if (target == null)
            {
                reason = $"The target process cannot be opened: {openError}";
                return false;
            }

            var notes = new List<string>();
            if (!CheckArchitecture(target.Architecture, image, out string? architectureReason, notes))
            {
                reason = architectureReason ?? "The DLL architecture does not match the target process.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            reason = "The compatibility check failed: " + ex.Message;
            return false;
        }
    }

    // ---------------------------------------------------------------- internals

    private InjectionResult InjectCore(int processId, string dllPath, InjectionMethod method,
        Stopwatch stopwatch, List<string> notes, CancellationToken cancellationToken)
    {
        if (processId <= 0)
            return Fail(method, $"Invalid process id {processId}.", stopwatch, notes);
        if (string.IsNullOrWhiteSpace(dllPath))
            return Fail(method, "No DLL path was supplied.", stopwatch, notes);

        cancellationToken.ThrowIfCancellationRequested();

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(dllPath);
        }
        catch (Exception ex)
        {
            return Fail(method, $"The DLL path is not valid: {ex.Message}", stopwatch, notes);
        }

        if (!File.Exists(fullPath))
            return Fail(method, $"The DLL file does not exist: {fullPath}", stopwatch, notes,
                win32: NativeMethods.ErrorFileNotFound);

        if (!TryReadDllImage(fullPath, out DllImageInfo image, out string? imageError))
            return Fail(method, $"The file is not a usable DLL image: {imageError}", stopwatch, notes,
                win32: NativeMethods.ErrorBadExeFormat);

        using RemoteProcess? target = RemoteProcess.TryOpen(processId, out string? openError, forInjection: true);
        if (target == null)
            return Fail(method, $"Cannot open process {processId} for injection: {openError}", stopwatch, notes);

        cancellationToken.ThrowIfCancellationRequested();

        if (!CheckArchitecture(target.Architecture, image, out string? architectureReason, notes))
            return Fail(method, architectureReason ?? "The DLL architecture does not match the target process.",
                stopwatch, notes, win32: NativeMethods.ErrorBadExeFormat);

        // Reject unsupported method/architecture combinations before resolving
        // exports or allocating anything in the target.
        if (method == InjectionMethod.NtCreateThreadEx && target.Architecture != ProcessArchitecture.X64)
        {
            return Fail(method,
                $"NtCreateThreadEx injection requires an x64 target; this target is {ArchitectureLabel(target.Architecture)}. Use {InjectionMethod.CreateRemoteThread} for 32-bit targets.",
                stopwatch, notes);
        }

        if (method == InjectionMethod.QueueUserApc && target.Architecture != ProcessArchitecture.X64)
        {
            return Fail(method,
                $"QueueUserAPC injection is only supported for x64 targets; this target is {ArchitectureLabel(target.Architecture)}. Cross-bitness APC delivery into a WOW64 process is unsafe (the target's loader can be corrupted when the queued LoadLibraryW runs). Use {InjectionMethod.CreateRemoteThread} for 32-bit targets.",
                stopwatch, notes);
        }

        if (!image.IsDll)
            notes.Add("The image does not carry the IMAGE_FILE_DLL characteristic; LoadLibraryW may reject it.");

        if (target.Architecture == ProcessArchitecture.X86 &&
            fullPath.Contains(@"\System32\", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add("The DLL path is under System32 while the target is 32-bit: WOW64 file-system redirection may load a different file from SysWOW64.");
        }

        _log.Write(LogLevel.Info,
            $"Injecting '{Path.GetFileName(fullPath)}' into pid {processId} via {method} ...", "inject");

        if (!TryResolveKernel32Export(target, "LoadLibraryW", out ulong loadLibrary, out string? resolveError))
            return Fail(method, resolveError ?? "Could not resolve kernel32!LoadLibraryW in the target.", stopwatch, notes);

        byte[] pathBytes = Encoding.Unicode.GetBytes(fullPath + "\0");
        IntPtr remoteBuffer = NativeMethods.VirtualAllocEx(
            target.Handle,
            IntPtr.Zero,
            (IntPtr)pathBytes.Length,
            NativeMethods.MemCommit | NativeMethods.MemReserve,
            NativeMethods.PageReadWrite);
        if (remoteBuffer == IntPtr.Zero)
        {
            int code = Marshal.GetLastWin32Error();
            return Fail(method,
                $"VirtualAllocEx failed in pid {processId}: {Win32ErrorText.DescribeWin32(code)} (Win32 error {code}).",
                stopwatch, notes, win32: (uint)code);
        }

        bool bufferSafeToFree = true;
        try
        {
            if (!target.WriteBytes((ulong)remoteBuffer.ToInt64(), pathBytes, out string? writeError))
            {
                return Fail(method, "Could not write the DLL path into the target: " + writeError,
                    stopwatch, notes, win32: (uint)Marshal.GetLastWin32Error());
            }

            ulong remoteBufferAddress = (ulong)remoteBuffer.ToInt64();
            if (target.Architecture == ProcessArchitecture.X86 && remoteBufferAddress > uint.MaxValue)
            {
                return Fail(method,
                    $"VirtualAllocEx returned 0x{remoteBufferAddress:X} in a 32-bit target; the path buffer must be below 4 GB.",
                    stopwatch, notes);
            }

            if (method == InjectionMethod.QueueUserApc)
                return InjectWithApc(target, loadLibrary, remoteBuffer, fullPath, method, stopwatch, notes, cancellationToken, out bufferSafeToFree);
            if (method == InjectionMethod.NtCreateThreadEx)
                return InjectWithNtCreateThread(target, loadLibrary, remoteBuffer, fullPath, method, stopwatch, notes, cancellationToken, out bufferSafeToFree);
            return InjectWithRemoteThread(target, loadLibrary, remoteBuffer, fullPath, method, stopwatch, notes, cancellationToken, out bufferSafeToFree);
        }
        finally
        {
            if (bufferSafeToFree)
            {
                if (!NativeMethods.VirtualFreeEx(target.Handle, remoteBuffer, IntPtr.Zero, NativeMethods.MemRelease))
                {
                    _log.Write(LogLevel.Warning,
                        $"VirtualFreeEx failed for 0x{remoteBuffer.ToInt64():X} in pid {processId} (Win32 error {Marshal.GetLastWin32Error()}).",
                        "inject");
                }
            }
        }
    }

    private InjectionResult InjectWithRemoteThread(RemoteProcess target, ulong loadLibrary, IntPtr remoteBuffer,
        string fullPath, InjectionMethod method, Stopwatch stopwatch, List<string> notes,
        CancellationToken cancellationToken, out bool bufferSafeToFree)
    {
        bufferSafeToFree = true;

        IntPtr thread = NativeMethods.CreateRemoteThread(
            target.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            (IntPtr)(long)loadLibrary,
            remoteBuffer,
            0,
            out uint threadId);
        if (thread == IntPtr.Zero)
        {
            int code = Marshal.GetLastWin32Error();
            return Fail(method,
                $"CreateRemoteThread failed: {Win32ErrorText.DescribeWin32(code)} (Win32 error {code}). Anti-malware or a protected process can block this call.",
                stopwatch, notes, win32: (uint)code);
        }

        try
        {
            notes.Add($"Remote loader thread {threadId} runs kernel32!LoadLibraryW at 0x{loadLibrary:X}.");
            return AwaitLoaderThread(target, thread, fullPath, method, stopwatch, notes, cancellationToken, out bufferSafeToFree);
        }
        finally
        {
            NativeMethods.CloseHandle(thread);
        }
    }

    private InjectionResult InjectWithNtCreateThread(RemoteProcess target, ulong loadLibrary, IntPtr remoteBuffer,
        string fullPath, InjectionMethod method, Stopwatch stopwatch, List<string> notes,
        CancellationToken cancellationToken, out bool bufferSafeToFree)
    {
        bufferSafeToFree = true;

        if (target.Architecture != ProcessArchitecture.X64)
        {
            return Fail(method,
                $"NtCreateThreadEx injection requires an x64 target; this target is {ArchitectureLabel(target.Architecture)}. Use {InjectionMethod.CreateRemoteThread} for 32-bit targets.",
                stopwatch, notes);
        }

        int status = NativeMethods.NtCreateThreadEx(
            out IntPtr thread,
            NativeMethods.ThreadAllAccess,
            IntPtr.Zero,
            target.Handle,
            (IntPtr)(long)loadLibrary,
            remoteBuffer,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (status != NativeMethods.NtSuccess)
        {
            uint win32 = NativeMethods.RtlNtStatusToDosError(status);
            uint ntStatus = unchecked((uint)status);
            return Fail(method,
                $"NtCreateThreadEx failed: {Win32ErrorText.ForNtStatus(status)} (NTSTATUS 0x{ntStatus:X8}, Win32 error {win32}).",
                stopwatch, notes, win32: win32, ntStatus: ntStatus);
        }

        if (thread == IntPtr.Zero)
            return Fail(method, "NtCreateThreadEx reported success but returned no thread handle.", stopwatch, notes);

        try
        {
            notes.Add($"Remote loader thread runs kernel32!LoadLibraryW at 0x{loadLibrary:X} through NtCreateThreadEx.");
            return AwaitLoaderThread(target, thread, fullPath, method, stopwatch, notes, cancellationToken, out bufferSafeToFree);
        }
        finally
        {
            NativeMethods.CloseHandle(thread);
        }
    }

    private InjectionResult InjectWithApc(RemoteProcess target, ulong loadLibrary, IntPtr remoteBuffer,
        string fullPath, InjectionMethod method, Stopwatch stopwatch, List<string> notes,
        CancellationToken cancellationToken, out bool bufferSafeToFree)
    {
        // Pending APCs may reference the path buffer at any later point, so it is
        // only freed when no APC was queued at all.
        bufferSafeToFree = false;

        if (target.Architecture != ProcessArchitecture.X64)
        {
            bufferSafeToFree = true;
            return Fail(method,
                $"QueueUserAPC injection is only supported for x64 targets; this target is {ArchitectureLabel(target.Architecture)}. Use {InjectionMethod.CreateRemoteThread} instead.",
                stopwatch, notes);
        }

        List<uint> threadIds = ListTargetThreads(target.Id);
        if (threadIds.Count == 0)
        {
            bufferSafeToFree = true;
            return Fail(method, $"No threads were found in pid {target.Id}; QueueUserAPC cannot deliver LoadLibraryW.", stopwatch, notes);
        }

        int queued = 0;
        int inaccessible = 0;
        foreach (uint threadId in threadIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IntPtr thread = NativeMethods.OpenThread(NativeMethods.ThreadSetContext, false, threadId);
            if (thread == IntPtr.Zero)
            {
                inaccessible++;
                continue;
            }

            try
            {
                if (NativeMethods.QueueUserAPC((IntPtr)(long)loadLibrary, thread, remoteBuffer) != 0)
                    queued++;
                else
                    inaccessible++;
            }
            finally
            {
                NativeMethods.CloseHandle(thread);
            }
        }

        if (queued == 0)
        {
            bufferSafeToFree = true;
            return Fail(method,
                $"QueueUserAPC failed for every thread of pid {target.Id} ({inaccessible} thread(s) were inaccessible). Access denied usually means the target runs at a higher integrity level.",
                stopwatch, notes, win32: NativeMethods.ErrorAccessDenied);
        }

        notes.Add($"Queued LoadLibraryW on {queued} of {threadIds.Count} thread(s)"
                  + (inaccessible > 0 ? $"; {inaccessible} thread(s) could not be opened." : "."));
        notes.Add("The remote path buffer is left allocated in the target because pending APCs may still reference it; it is released when the target exits.");

        string fileName = Path.GetFileName(fullPath);
        bool loaded = false;
        ulong moduleBase = 0;
        var wait = Stopwatch.StartNew();
        while (wait.ElapsedMilliseconds < ApcWaitMs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                foreach (var module in ProcessDiscovery.QueryModuleList(target.Id))
                {
                    if (!module.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    moduleBase = module.Base;
                    loaded = true;
                    break;
                }
            }
            catch (Exception)
            {
                // Module enumeration is best effort here; poll again.
            }

            if (loaded || !target.IsAlive)
                break;
            Thread.Sleep(150);
        }

        notes.Add("User-mode APCs are only delivered while a thread enters an alertable wait; if no such thread exists the DLL loads only later (or never).");

        if (loaded)
        {
            string message = $"Queued LoadLibraryW on {queued} thread(s) of pid {target.Id}; '{fileName}' is now loaded at 0x{moduleBase:X}.";
            _log.Write(LogLevel.Success, message, "inject");
            return new InjectionResult
            {
                Success = true,
                Method = method,
                Message = message,
                RemoteModuleBase = moduleBase,
                DurationMs = (int)stopwatch.ElapsedMilliseconds,
                Notes = notes.ToArray(),
            };
        }

        if (!target.IsAlive)
            return Fail(method, $"The target process exited while the APCs were pending; '{fileName}' did not load.", stopwatch, notes);

        return Fail(method,
            $"Queued LoadLibraryW on {queued} thread(s) of pid {target.Id}, but '{fileName}' had not loaded after {ApcWaitMs} ms. The DLL may still load later when a thread enters an alertable wait; refresh the module list to check.",
            stopwatch, notes);
    }

    private InjectionResult AwaitLoaderThread(RemoteProcess target, IntPtr threadHandle, string fullPath,
        InjectionMethod method, Stopwatch stopwatch, List<string> notes,
        CancellationToken cancellationToken, out bool bufferSafeToFree)
    {
        bufferSafeToFree = true;

        uint wait = WaitForRemoteThread(threadHandle, RemoteThreadTimeoutMs, cancellationToken, out bool cancelled);
        if (cancelled)
        {
            bufferSafeToFree = false;
            return Fail(method,
                "The injection was cancelled while the remote loader thread was running; the remote path buffer was left allocated because the thread may still be using it.",
                stopwatch, notes);
        }

        if (wait == NativeMethods.WaitTimeout)
        {
            bufferSafeToFree = false;
            return Fail(method,
                $"LoadLibraryW did not finish in pid {target.Id} within {RemoteThreadTimeoutMs / 1000} s; the loader thread is still running and the remote path buffer was left allocated. The loader lock may be held or the target may be unresponsive.",
                stopwatch, notes);
        }

        if (wait == NativeMethods.WaitFailed)
        {
            int code = Marshal.GetLastWin32Error();
            bufferSafeToFree = false;
            return Fail(method,
                $"Waiting for the remote loader thread failed: {Win32ErrorText.DescribeWin32(code)} (Win32 error {code}).",
                stopwatch, notes, win32: (uint)code);
        }

        if (!target.IsAlive)
        {
            bufferSafeToFree = false;
            notes.Add("The remote allocation disappeared together with the target process.");
            return Fail(method, $"The target process exited while '{Path.GetFileName(fullPath)}' was loading.", stopwatch, notes);
        }

        if (!NativeMethods.GetExitCodeThread(threadHandle, out uint exitCode))
        {
            int code = Marshal.GetLastWin32Error();
            bufferSafeToFree = false;
            return Fail(method,
                $"The remote loader thread finished but its exit code could not be read: {Win32ErrorText.DescribeWin32(code)} (Win32 error {code}).",
                stopwatch, notes, win32: (uint)code);
        }

        if (exitCode == 0)
        {
            return Fail(method,
                $"LoadLibraryW returned NULL inside pid {target.Id}: the loader rejected '{Path.GetFileName(fullPath)}'. Common causes: the target's token cannot read the file, a dependency is missing, DllMain returned FALSE, or the image is not loadable.",
                stopwatch, notes, win32: NativeMethods.ErrorBadExeFormat);
        }

        string loadedName = Path.GetFileName(fullPath);
        ulong moduleBase = ResolveLoadedModuleBase(target, fullPath, exitCode, notes);
        string message = $"Loaded '{loadedName}' into pid {target.Id} via {method} (module base 0x{moduleBase:X}).";
        _log.Write(LogLevel.Success, message, "inject");
        return new InjectionResult
        {
            Success = true,
            Method = method,
            Message = message,
            RemoteModuleBase = moduleBase,
            DurationMs = (int)stopwatch.ElapsedMilliseconds,
            Notes = notes.ToArray(),
        };
    }

    /// <summary>
    /// The thread exit code is a DWORD, so an x64 HMODULE is truncated. The
    /// authoritative base therefore comes from the target module list; the exit
    /// code is only used to disambiguate same-named modules and as a fallback.
    /// </summary>
    private static ulong ResolveLoadedModuleBase(RemoteProcess target, string fullPath, uint exitCode,
        List<string> notes)
    {
        string fileName = Path.GetFileName(fullPath);
        try
        {
            var candidates = new List<(string Name, string Path, ulong Base, uint Size)>();
            foreach (var module in ProcessDiscovery.QueryModuleList(target.Id))
            {
                if (!IsModuleForTargetBitness(module.Path, module.Base, target.Architecture))
                    continue;
                if (module.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                    candidates.Add(module);
            }

            if (candidates.Count == 0)
            {
                notes.Add("The loader returned a non-zero HMODULE but the module is not in the target module list; it may have unloaded itself from DllMain. For x64 targets the reported base is the low 32 bits of the HMODULE.");
                return exitCode;
            }

            foreach (var candidate in candidates)
            {
                if ((uint)(candidate.Base & 0xFFFFFFFF) == exitCode)
                    return candidate.Base;
            }

            if (candidates.Count == 1)
            {
                notes.Add("The module base was taken from the target module list because a thread exit code only carries the low 32 bits of an x64 HMODULE.");
                return candidates[0].Base;
            }

            notes.Add("Several modules share the injected file name; the loader's return value (low 32 bits) is reported instead of a module base.");
            return exitCode;
        }
        catch (Exception)
        {
            return exitCode;
        }
    }

    private static uint WaitForRemoteThread(IntPtr threadHandle, int timeoutMs, CancellationToken cancellationToken,
        out bool cancelled)
    {
        cancelled = false;
        int elapsed = 0;
        while (true)
        {
            uint result = NativeMethods.WaitForSingleObject(threadHandle, 250);
            if (result != NativeMethods.WaitTimeout)
                return result;

            elapsed += 250;
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                return NativeMethods.WaitTimeout;
            }

            if (elapsed >= timeoutMs)
                return NativeMethods.WaitTimeout;
        }
    }

    private static List<uint> ListTargetThreads(int processId)
    {
        var threadIds = new List<uint>();
        IntPtr snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32csSnapThread, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            return threadIds;

        try
        {
            var entry = new NativeMethods.THREADENTRY32
            {
                dwSize = (uint)Marshal.SizeOf<NativeMethods.THREADENTRY32>(),
            };

            if (!NativeMethods.Thread32First(snapshot, ref entry))
                return threadIds;

            do
            {
                if (entry.th32OwnerProcessID == (uint)processId)
                    threadIds.Add(entry.th32ThreadID);
            }
            while (NativeMethods.Thread32Next(snapshot, ref entry));
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }

        return threadIds;
    }

    /// <summary>
    /// Resolves an export of the *target's* kernel32: the module base comes from
    /// the target module list and the export RVA from the matching local image
    /// (the native x64 kernel32, or SysWOW64\kernel32.dll for a 32-bit target).
    /// </summary>
    private static bool TryResolveKernel32Export(RemoteProcess target, string exportName, out ulong address,
        out string? error)
    {
        address = 0;
        error = null;

        bool target32 = target.Architecture == ProcessArchitecture.X86
                        || target.Architecture == ProcessArchitecture.Arm;
        if (target.Architecture is ProcessArchitecture.Arm64)
        {
            error = "ARM64 targets are not supported by this x64 build.";
            return false;
        }

        IReadOnlyList<(string Name, string Path, ulong Base, uint Size)> modules =
            ProcessDiscovery.QueryModuleList(target.Id);

        ulong kernelBase = 0;
        foreach (var module in modules)
        {
            if (!module.Name.Equals("kernel32.dll", StringComparison.OrdinalIgnoreCase))
                continue;

            bool isSysWow64 = module.Path.Contains("SysWOW64", StringComparison.OrdinalIgnoreCase);
            if (target32)
            {
                if (isSysWow64 || (string.IsNullOrEmpty(module.Path) && module.Base <= uint.MaxValue))
                {
                    kernelBase = module.Base;
                    break;
                }
            }
            else if (!isSysWow64 && (module.Base > uint.MaxValue || module.Path.Contains("System32", StringComparison.OrdinalIgnoreCase)))
            {
                kernelBase = module.Base;
                break;
            }
        }

        if (kernelBase == 0)
        {
            error = $"kernel32.dll was not found in the module list of pid {target.Id}.";
            return false;
        }

        uint rva;
        if (target32 && Environment.Is64BitOperatingSystem)
        {
            string systemX86 = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
            string kernel32Path = Path.Combine(systemX86, "kernel32.dll");
            if (!File.Exists(kernel32Path))
            {
                error = $"The 32-bit kernel32.dll for export lookup was not found at '{kernel32Path}'.";
                return false;
            }

            if (!TryReadExportRvaFromFile(kernel32Path, exportName, out rva, out error))
                return false;
        }
        else
        {
            IntPtr localKernel32 = NativeMethods.GetModuleHandleW("kernel32.dll");
            if (localKernel32 == IntPtr.Zero)
            {
                error = "The local kernel32.dll module handle is unavailable.";
                return false;
            }

            IntPtr localExport = NativeMethods.GetProcAddress(localKernel32, exportName);
            if (localExport == IntPtr.Zero)
            {
                error = $"The local kernel32.dll does not export {exportName}.";
                return false;
            }

            long delta = localExport.ToInt64() - localKernel32.ToInt64();
            if (delta <= 0 || delta > uint.MaxValue)
            {
                error = $"Unexpected local address for kernel32!{exportName}.";
                return false;
            }

            rva = (uint)delta;
        }

        address = kernelBase + rva;
        if (target32 && address > uint.MaxValue)
        {
            error = $"The resolved address 0x{address:X} of kernel32!{exportName} does not fit the 32-bit address space.";
            address = 0;
            return false;
        }

        return true;
    }

    /// <summary>Reads the RVA of an export from an on-disk PE image (x86 and x64).</summary>
    private static bool TryReadExportRvaFromFile(string path, string exportName, out uint rva, out string? error)
    {
        rva = 0;
        error = null;

        string cacheKey = path + "|" + exportName;
        lock (ExportCacheLock)
        {
            if (ExportRvaCache.TryGetValue(cacheKey, out uint cached))
            {
                rva = cached;
                return true;
            }
        }

        try
        {
            byte[] file = File.ReadAllBytes(path);
            if (!PeImageLayout.TryReadHeaders(file, out PeHeaders headers))
            {
                error = $"'{path}' does not contain readable PE headers.";
                return false;
            }

            int exportDirectoryOffset = headers.OptionalHeaderOffset + (headers.Is64Bit ? 112 : 96);
            if (exportDirectoryOffset + 8 > file.Length)
            {
                error = $"'{path}' has a truncated optional header.";
                return false;
            }

            uint exportRva = PeImageLayout.ReadU32(file, exportDirectoryOffset);
            if (exportRva == 0)
            {
                error = $"'{path}' does not export any functions.";
                return false;
            }

            int directory = PeImageLayout.RvaToFileOffset(headers, exportRva);
            if (directory < 0 || directory + 40 > file.Length)
            {
                error = $"The export directory of '{path}' is outside the file.";
                return false;
            }

            uint nameCount = PeImageLayout.ReadU32(file, directory + 24);
            uint functionsRva = PeImageLayout.ReadU32(file, directory + 28);
            uint namesRva = PeImageLayout.ReadU32(file, directory + 32);
            uint ordinalsRva = PeImageLayout.ReadU32(file, directory + 36);

            int names = PeImageLayout.RvaToFileOffset(headers, namesRva);
            int ordinals = PeImageLayout.RvaToFileOffset(headers, ordinalsRva);
            int functions = PeImageLayout.RvaToFileOffset(headers, functionsRva);
            if (names < 0 || ordinals < 0 || functions < 0)
            {
                error = $"The export tables of '{path}' are outside the file.";
                return false;
            }

            int count = (int)Math.Min(nameCount, 65536u);
            for (int i = 0; i < count; i++)
            {
                int nameEntry = names + i * 4;
                if (nameEntry + 4 > file.Length)
                    break;

                int nameOffset = PeImageLayout.RvaToFileOffset(headers, PeImageLayout.ReadU32(file, nameEntry));
                if (nameOffset < 0 || nameOffset >= file.Length)
                    continue;

                int end = nameOffset;
                while (end < file.Length && end - nameOffset < 256 && file[end] != 0)
                    end++;
                if (end >= file.Length || file[end] != 0)
                    continue;

                if (!Encoding.ASCII.GetString(file, nameOffset, end - nameOffset).Equals(exportName, StringComparison.Ordinal))
                    continue;

                int ordinalEntry = ordinals + i * 2;
                if (ordinalEntry + 2 > file.Length)
                    break;

                uint ordinal = PeImageLayout.ReadU16(file, ordinalEntry);
                int functionEntry = functions + (int)ordinal * 4;
                if (functionEntry + 4 > file.Length)
                    break;

                rva = PeImageLayout.ReadU32(file, functionEntry);
                if (rva == 0)
                {
                    error = $"The export '{exportName}' of '{path}' has no address.";
                    return false;
                }

                lock (ExportCacheLock)
                {
                    ExportRvaCache[cacheKey] = rva;
                }

                return true;
            }

            error = $"The export '{exportName}' was not found in '{path}'.";
            return false;
        }
        catch (Exception ex)
        {
            error = $"Could not read the export table of '{path}': {ex.Message}";
            return false;
        }
    }

    private static bool TryReadDllImage(string path, out DllImageInfo image, out string? error)
    {
        image = new DllImageInfo();
        error = null;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            if (stream.Length < 0x40)
            {
                error = "the file is too small to be a PE image";
                return false;
            }

            int headerLength = (int)Math.Min(stream.Length, 0x10000);
            var header = new byte[headerLength];
            int headerRead = 0;
            while (headerRead < headerLength)
            {
                int got = stream.Read(header, headerRead, headerLength - headerRead);
                if (got <= 0)
                    break;
                headerRead += got;
            }

            if (!PeImageLayout.TryReadHeaders(header, out PeHeaders headers))
            {
                error = "the PE headers are invalid";
                return false;
            }

            uint clrFlags = 0;
            bool hasCli = headers.ClrDirectoryRva != 0;
            if (hasCli)
            {
                int cliOffset = PeImageLayout.RvaToFileOffset(headers, headers.ClrDirectoryRva);
                if (cliOffset >= 0 && cliOffset + 72 <= stream.Length)
                {
                    var cli = new byte[72];
                    stream.Position = cliOffset;
                    int cliRead = 0;
                    while (cliRead < cli.Length)
                    {
                        int got = stream.Read(cli, cliRead, cli.Length - cliRead);
                        if (got <= 0)
                            break;
                        cliRead += got;
                    }

                    if (cliRead == cli.Length && PeImageLayout.ReadU32(cli, 0) >= 72)
                        clrFlags = PeImageLayout.ReadU32(cli, 16);
                    else
                        hasCli = false;
                }
                else
                {
                    hasCli = false;
                }
            }

            image = new DllImageInfo
            {
                Machine = headers.Machine,
                IsDll = (headers.Characteristics & ImageFileDll) != 0,
                HasCli = hasCli,
                ClrFlags = clrFlags,
            };
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool CheckArchitecture(ProcessArchitecture targetArchitecture, DllImageInfo image,
        out string? reason, List<string> notes)
    {
        reason = null;
        string dllArchitecture = PeImageLayout.DescribeMachine(image.Machine);

        bool anyCpu = image.HasCli && image.IsIlOnly && !image.Requires32Bit;
        if (anyCpu)
        {
            if (targetArchitecture is ProcessArchitecture.Arm or ProcessArchitecture.Arm64)
            {
                reason = $"ARM targets are not supported by this x64 build (target is {ArchitectureLabel(targetArchitecture)}).";
                return false;
            }

            notes.Add("The DLL is a managed IL-only assembly (AnyCPU). LoadLibraryW maps it as data and the CLR will not run its code; inject a native or mixed-mode DLL to verify loader access.");
            return true;
        }

        switch (targetArchitecture)
        {
            case ProcessArchitecture.X64:
                if (image.Machine == ImageFileMachineAmd64)
                    return true;
                reason = $"{dllArchitecture} DLL cannot be loaded into an x64 process.";
                return false;

            case ProcessArchitecture.X86:
                if (image.Machine == ImageFileMachineI386)
                    return true;
                reason = $"{dllArchitecture} DLL cannot be loaded into an x86 (WOW64) process.";
                return false;

            case ProcessArchitecture.Arm64:
                reason = image.Machine == ImageFileMachineArm64
                    ? "ARM64 targets require an ARM64 native loader and are not supported by this x64 build."
                    : $"ARM64 targets are not supported by this x64 build (DLL is {dllArchitecture}).";
                return false;

            case ProcessArchitecture.Arm:
                reason = "ARM targets are not supported by this x64 build.";
                return false;

            default:
                notes.Add($"The target architecture could not be determined; attempting to load a {dllArchitecture} DLL anyway.");
                return true;
        }
    }

    private static string ArchitectureLabel(ProcessArchitecture architecture) => architecture switch
    {
        ProcessArchitecture.X86 => "x86",
        ProcessArchitecture.X64 => "x64",
        ProcessArchitecture.Arm64 => "ARM64",
        ProcessArchitecture.Arm => "ARM",
        _ => "unknown-architecture",
    };

    private static bool IsModuleForTargetBitness(string path, ulong moduleBase, ProcessArchitecture architecture)
    {
        bool isSysWow64 = path.Contains("SysWOW64", StringComparison.OrdinalIgnoreCase);
        return architecture switch
        {
            ProcessArchitecture.X86 => isSysWow64 || (string.IsNullOrEmpty(path) && moduleBase <= uint.MaxValue),
            ProcessArchitecture.X64 => !isSysWow64,
            _ => true,
        };
    }

    private static bool ModuleMatches((string Name, string Path, ulong Base, uint Size) module, string requested)
    {
        if (module.Name.Equals(requested, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrEmpty(module.Path))
        {
            if (module.Path.Equals(requested, StringComparison.OrdinalIgnoreCase))
                return true;
            if (Path.GetFileName(module.Path).Equals(requested, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        string requestedFile = Path.GetFileName(requested);
        if (requestedFile.Length > 0 && !requestedFile.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            requestedFile += ".dll";
        return module.Name.Equals(requestedFile, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindSimilarModules(IReadOnlyList<(string Name, string Path, ulong Base, uint Size)> modules,
        string requested)
    {
        string stem = Path.GetFileNameWithoutExtension(requested);
        if (stem.Length < 2)
            return null;

        var matches = new List<string>();
        foreach (var module in modules)
        {
            if (module.Name.Contains(stem, StringComparison.OrdinalIgnoreCase) && !matches.Contains(module.Name))
                matches.Add(module.Name);
            if (matches.Count >= 5)
                break;
        }

        return matches.Count == 0 ? null : string.Join(", ", matches);
    }

    private InjectionResult Fail(InjectionMethod method, string message, Stopwatch stopwatch, List<string> notes,
        uint win32 = 0, uint ntStatus = 0)
    {
        _log.Write(LogLevel.Error, message, "inject");
        return new InjectionResult
        {
            Success = false,
            Method = method,
            Message = message,
            Win32Error = win32,
            NtStatus = ntStatus,
            DurationMs = (int)stopwatch.ElapsedMilliseconds,
            Notes = notes.ToArray(),
        };
    }

    private sealed class DllImageInfo
    {
        public ushort Machine { get; init; }
        public bool IsDll { get; init; }
        public bool HasCli { get; init; }
        public uint ClrFlags { get; init; }

        public bool IsIlOnly => (ClrFlags & 0x00000001) != 0;
        public bool Requires32Bit => (ClrFlags & 0x00000002) != 0;
    }
}
