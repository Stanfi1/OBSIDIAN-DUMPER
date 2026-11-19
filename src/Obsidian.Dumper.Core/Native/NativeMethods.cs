using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Obsidian.Dumper.Core.Native;

/// <summary>
/// Win32 / NT surface used by OBSIDIAN. The inspection engine itself is
/// strictly read-only; write/thread primitives exist only for the optional
/// managed agent that the operator has to enable explicitly.
/// </summary>
internal static class NativeMethods
{
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint ProcessQueryInformation = 0x0400;
    internal const uint ProcessVmRead = 0x0010;
    internal const uint ProcessVmWrite = 0x0020;
    internal const uint ProcessVmOperation = 0x0008;
    internal const uint ProcessCreateThread = 0x0002;
    internal const uint ProcessTerminate = 0x0001;
    internal const uint ProcessSuspendResume = 0x0800;

    internal const uint ThreadSetContext = 0x0010;
    internal const uint ThreadQueryInformation = 0x0040;
    internal const uint ThreadAllAccess = 0x001FFFFF;

    internal const uint TokenQuery = 0x0008;
    internal const int TokenIntegrityLevel = 25;
    internal const int TokenElevation = 20;

    internal const uint Th32csSnapProcess = 0x00000002;
    internal const uint Th32csSnapThread = 0x00000004;
    internal const uint Th32csSnapModule = 0x00000008;
    internal const uint Th32csSnapModule32 = 0x00000010;

    internal const uint MemCommit = 0x1000;
    internal const uint MemReserve = 0x2000;
    internal const uint PageNoAccess = 0x01;
    internal const uint PageReadWrite = 0x04;
    internal const uint MemImage = 0x01000000;
    internal const uint MemMapped = 0x00400000;
    internal const uint MemPrivate = 0x00020000;

    internal const uint MemRelease = 0x8000;

    internal const uint WaitObject0 = 0x00000000;
    internal const uint WaitTimeout = 0x00000102;
    internal const uint WaitFailed = 0xFFFFFFFF;
    internal const uint StillActive = 259;

    internal const int NtSuccess = 0;
    internal const int ProcessWow64Information = 26;

    internal const int ErrorAccessDenied = 5;
    internal const int ErrorInvalidParameter = 87;
    internal const int ErrorInsufficientBuffer = 122;
    internal const int ErrorBadExeFormat = 193;
    internal const int ErrorFileNotFound = 2;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern unsafe bool ReadProcessMemory(SafeProcessHandle process, IntPtr baseAddress, byte* buffer, IntPtr size, out IntPtr bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern unsafe bool WriteProcessMemory(SafeProcessHandle process, IntPtr baseAddress, byte* buffer, IntPtr size, out IntPtr bytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr VirtualAllocEx(SafeProcessHandle process, IntPtr address, IntPtr size, uint allocationType, uint protection);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool VirtualFreeEx(SafeProcessHandle process, IntPtr address, IntPtr size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool VirtualQueryEx(SafeProcessHandle process, IntPtr address, out MEMORY_BASIC_INFORMATION64 information, IntPtr length);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr CreateToolhelp32Snapshot(uint flags, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool Module32FirstW(IntPtr snapshot, ref MODULEENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool Module32NextW(IntPtr snapshot, ref MODULEENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder builder, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool IsWow64Process(IntPtr process, out bool wow64Process);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetProcessTimes(SafeProcessHandle process, out FILETIME creation, out FILETIME exit, out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool K32GetProcessMemoryInfo(SafeProcessHandle process, ref PROCESS_MEMORY_COUNTERS counters, uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, byte[]? information, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GetModuleHandleW([MarshalAs(UnmanagedType.LPWStr)] string moduleName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    internal static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateRemoteThread(SafeProcessHandle process, IntPtr attributes, IntPtr stackSize, IntPtr startAddress, IntPtr parameter, uint creationFlags, out uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetExitCodeThread(IntPtr thread, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenThread(uint desiredAccess, bool inheritHandle, uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint QueueUserAPC(IntPtr apcRoutine, IntPtr threadHandle, IntPtr data);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool Thread32First(IntPtr snapshot, ref THREADENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool Thread32Next(IntPtr snapshot, ref THREADENTRY32 entry);

    [DllImport("ntdll.dll")]
    internal static extern int NtCreateThreadEx(
        out IntPtr threadHandle,
        uint desiredAccess,
        IntPtr objectAttributes,
        SafeProcessHandle processHandle,
        IntPtr startRoutine,
        IntPtr argument,
        uint createFlags,
        IntPtr zeroBits,
        IntPtr stackSize,
        IntPtr maximumStackSize,
        IntPtr attributeList);

    [DllImport("ntdll.dll")]
    internal static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    internal static extern int NtResumeProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    internal static extern uint RtlNtStatusToDosError(int status);

    [DllImport("ntdll.dll")]
    internal static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass, ref PROCESS_BASIC_INFORMATION information, int length, out int returnLength);

    [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
    internal static extern int NtQueryInformationProcessPointer(SafeProcessHandle process, int informationClass, out IntPtr information, int length, out int returnLength);

    [DllImport("advapi32.dll")]
    internal static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthorityIndex);

    [DllImport("advapi32.dll")]
    internal static extern byte GetSidSubAuthorityCount(IntPtr sid);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct THREADENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ThreadID;
        public uint th32OwnerProcessID;
        public int tpBasePri;
        public int tpDeltaPri;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MODULEENTRY32W
    {
        public uint dwSize;
        public uint th32ModuleID;
        public uint th32ProcessID;
        public uint GlblcntUsage;
        public uint ProccntUsage;
        public IntPtr modBaseAddr;
        public uint modBaseSize;
        public IntPtr hModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORY_BASIC_INFORMATION64
    {
        public ulong BaseAddress;
        public ulong AllocationBase;
        public uint AllocationProtect;
        public uint Alignment1;
        public ulong RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint Alignment2;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FILETIME
    {
        public uint Low;
        public uint High;
        public long ToInt64() => ((long)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_MEMORY_COUNTERS
    {
        public uint cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
    }
}
