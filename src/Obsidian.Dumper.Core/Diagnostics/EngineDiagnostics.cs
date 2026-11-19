using System.Runtime.InteropServices;
using Obsidian.Dumper.Core.Native;

namespace Obsidian.Dumper.Core.Diagnostics;

/// <summary>Small self-checks used by the CLI and the UI diagnostics card.</summary>
public static class EngineDiagnostics
{
    /// <summary>Raw integrity token dump for one process (support / QA tool).</summary>
    public static string ProbeToken(int processId)
    {
        using RemoteProcess? process = RemoteProcess.TryOpen(processId, out string? error);
        if (process == null)
            return "open failed: " + error;

        return ProcessSecurity.Describe(process.Handle);
    }

    public static string ProbeProcessSnapshot()
    {
        IntPtr snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return $"snapshot failed: errno={Marshal.GetLastWin32Error()}";
        }

        var entry = new NativeMethods.PROCESSENTRY32W
        {
            dwSize = (uint)Marshal.SizeOf<NativeMethods.PROCESSENTRY32W>(),
        };

        int count = 0;
        int firstPid = -1;
        string first = string.Empty;
        try
        {
            if (NativeMethods.Process32FirstW(snapshot, ref entry))
            {
                do
                {
                    count++;
                    if (firstPid < 0)
                    {
                        firstPid = (int)entry.th32ProcessID;
                        first = entry.szExeFile;
                    }
                }
                while (NativeMethods.Process32NextW(snapshot, ref entry));
            }
            else
            {
                return $"first failed: errno={Marshal.GetLastWin32Error()}";
            }
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }

        return $"struct={Marshal.SizeOf<NativeMethods.PROCESSENTRY32W>()} count={count} first={firstPid}:{first} native={Environment.Is64BitProcess}";
    }
}
