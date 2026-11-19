using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Obsidian.Dumper.Core.Native;

namespace Obsidian.Dumper.Core;

/// <summary>
/// Best-effort suspend / resume / terminate control for a live process.
///
/// Suspend and resume use the NT primitives (<c>NtSuspendProcess</c> /
/// <c>NtResumeProcess</c>) because they operate on the whole process instead of
/// racing a thread snapshot. Every method reports failure through
/// <c>error</c> — nothing throws — and access-denied situations are described
/// honestly so the UI can tell the operator to elevate.
/// </summary>
public static class ProcessControl
{
    /// <summary>
    /// Suspends every thread of the process. Fails with an explanatory message
    /// when the process cannot be opened (typically access denied) or when the
    /// NT call reports an error.
    /// </summary>
    public static bool TrySuspend(int processId, out string? error)
        => SetSuspension(processId, suspend: true, out error);

    /// <summary>
    /// Resumes a process previously suspended with <see cref="TrySuspend"/>.
    /// Fails with an explanatory message when the process cannot be opened or
    /// the NT call reports an error.
    /// </summary>
    public static bool TryResume(int processId, out string? error)
        => SetSuspension(processId, suspend: false, out error);

    /// <summary>
    /// Terminates the process with exit code 1. Requires PROCESS_TERMINATE;
    /// access denied is reported honestly instead of being retried.
    /// </summary>
    public static bool TryTerminate(int processId, out string? error)
    {
        error = null;

        if (processId <= 0)
        {
            error = $"Invalid process id {processId}.";
            return false;
        }

        using SafeProcessHandle process = NativeMethods.OpenProcess(NativeMethods.ProcessTerminate, false, processId);
        if (process.IsInvalid)
        {
            error = Win32ErrorText.ForOpenProcess(processId, Marshal.GetLastWin32Error());
            return false;
        }

        if (!NativeMethods.TerminateProcess(process, 1))
        {
            int code = Marshal.GetLastWin32Error();
            error = $"TerminateProcess failed for pid {processId}: {Win32ErrorText.DescribeWin32(code)} (Win32 error {code}).";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Best-effort check that every thread of the process is suspended.
    ///
    /// The state is derived from the per-thread wait reason exposed by
    /// <see cref="ProcessThread.WaitReason"/>: after NtSuspendProcess the kernel
    /// reports every thread as waiting with the Suspended reason. Returns false
    /// when the process cannot be inspected, has no threads, or any thread is
    /// not suspended — a conservative answer for a destructive UI toggle.
    /// </summary>
    public static bool IsSuspended(int processId)
    {
        if (processId <= 0)
            return false;

        try
        {
            using Process process = Process.GetProcessById(processId);
            ProcessThreadCollection threads = process.Threads;
            if (threads.Count == 0)
                return false;

            foreach (ProcessThread thread in threads)
            {
                try
                {
                    if (thread.WaitReason != ThreadWaitReason.Suspended)
                        return false;
                }
                catch (Exception)
                {
                    // The thread exited between enumeration and inspection.
                    return false;
                }
            }

            return true;
        }
        catch (Exception)
        {
            // Protected process, access denied, or process gone.
            return false;
        }
    }

    private static bool SetSuspension(int processId, bool suspend, out string? error)
    {
        error = null;
        string verb = suspend ? "suspend" : "resume";

        if (processId <= 0)
        {
            error = $"Invalid process id {processId}.";
            return false;
        }

        using SafeProcessHandle process = NativeMethods.OpenProcess(NativeMethods.ProcessSuspendResume, false, processId);
        if (process.IsInvalid)
        {
            error = Win32ErrorText.ForOpenProcess(processId, Marshal.GetLastWin32Error())
                    + $" The process was not {(suspend ? "suspended" : "resumed")}.";
            return false;
        }

        int status = suspend
            ? NativeMethods.NtSuspendProcess(process.DangerousGetHandle())
            : NativeMethods.NtResumeProcess(process.DangerousGetHandle());

        if (status != NativeMethods.NtSuccess)
        {
            uint win32 = NativeMethods.RtlNtStatusToDosError(status);
            error = $"Nt{(suspend ? "Suspend" : "Resume")}Process failed for pid {processId}: "
                    + $"{Win32ErrorText.ForNtStatus(status)} (NTSTATUS 0x{unchecked((uint)status):X8}, Win32 error {win32}).";
            return false;
        }

        return true;
    }
}
