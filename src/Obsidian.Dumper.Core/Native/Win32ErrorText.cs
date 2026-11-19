namespace Obsidian.Dumper.Core.Native;

/// <summary>
/// Human readable descriptions for the Win32 / NTSTATUS error codes the engine
/// surfaces. Keeping the mapping in one place makes the UI, the CLI and the
/// audit log agree on the wording of common failures (access denied in
/// particular is the single most frequent injection problem).
/// </summary>
internal static class Win32ErrorText
{
    internal static string DescribeWin32(int code) => code switch
    {
        0 => "the operation completed successfully",
        2 => "the file was not found",
        3 => "the path was not found",
        5 => "access denied",
        6 => "invalid handle",
        8 => "not enough memory",
        12 => "invalid access code",
        14 => "not enough storage is available",
        18 => "there are no more files",
        24 => "too many open files",
        31 => "a device attached to the system is not functioning",
        50 => "the request is not supported",
        67 => "the network name cannot be found",
        87 => "invalid parameter",
        122 => "the buffer is too small",
        126 => "the module was not found",
        127 => "the procedure was not found",
        193 => "not a valid executable image (bad EXE format)",
        233 => "no process is on the other end of the pipe",
        258 => "the wait operation timed out",
        259 => "the process is still active",
        299 => "only part of the request was completed",
        997 => "overlapped I/O is in progress",
        _ => $"Win32 error {code}",
    };

    /// <summary>
    /// Message for a failed <c>OpenProcess</c>. Access denied is expanded with
    /// the concrete reason and the remedy, because a bare "error 5" hides the
    /// most common operational mistake (running unelevated).
    /// </summary>
    internal static string ForOpenProcess(int processId, int code) => code switch
    {
        2 => $"The process with id {processId} does not exist.",
        5 => $"Access denied opening process {processId}: it runs at a higher integrity level, is protected, or belongs to another user. Run OBSIDIAN elevated or use a process you own.",
        6 => $"Process id {processId} is not a valid process id.",
        87 => $"The process with id {processId} does not exist.",
        _ => $"OpenProcess({processId}) failed: {DescribeWin32(code)} (Win32 error {code}).",
    };

    /// <summary>Symbolic names for the NTSTATUS values the engine can receive.</summary>
    internal static string ForNtStatus(int status) => unchecked((uint)status) switch
    {
        0x00000000 => "STATUS_SUCCESS",
        0x80000002 => "STATUS_WAIT_0",
        0xC0000002 => "STATUS_NOT_IMPLEMENTED",
        0xC0000005 => "STATUS_ACCESS_VIOLATION",
        0xC0000008 => "STATUS_INVALID_HANDLE",
        0xC000000D => "STATUS_INVALID_PARAMETER",
        0xC0000010 => "STATUS_INVALID_DEVICE_REQUEST",
        0xC0000022 => "STATUS_ACCESS_DENIED",
        0xC000010A => "STATUS_PROCESS_IS_TERMINATING",
        0xC0000121 => "STATUS_CANNOT_DELETE",
        var other => $"NTSTATUS 0x{other:X8}",
    };
}
