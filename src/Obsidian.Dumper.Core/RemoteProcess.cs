using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Obsidian.Dumper.Core.Model;
using Obsidian.Dumper.Core.Native;
using Obsidian.Dumper.Shared;

namespace Obsidian.Dumper.Core;

/// <summary>
/// Read-only view of a live process. All access goes through a single
/// process handle opened with the minimum rights required, and every read is
/// best effort: unreadable pages are zero-filled rather than aborting a dump.
/// </summary>
public sealed class RemoteProcess : IDisposable
{
    private readonly SafeProcessHandle _handle;
    private readonly bool _isWow64Target;
    private bool _disposed;

    private RemoteProcess(int id, string name, SafeProcessHandle handle, ProcessArchitecture architecture, bool isWow64Target)
    {
        Id = id;
        Name = name;
        _handle = handle;
        Architecture = architecture;
        _isWow64Target = isWow64Target;
    }

    public int Id { get; }
    public string Name { get; }
    public ProcessArchitecture Architecture { get; }
    public bool Is64Bit => Architecture is ProcessArchitecture.X64 or ProcessArchitecture.Arm64;
    public SafeProcessHandle Handle => _handle;

    public static RemoteProcess? TryOpen(int processId, out string? error, bool forInjection = false)
    {
        error = null;
        uint baseAccess = NativeMethods.ProcessQueryInformation | NativeMethods.ProcessVmRead;
        if (forInjection)
            baseAccess |= NativeMethods.ProcessVmWrite | NativeMethods.ProcessVmOperation | NativeMethods.ProcessCreateThread;

        SafeProcessHandle handle = NativeMethods.OpenProcess(baseAccess, false, processId);
        if (handle.IsInvalid)
        {
            // PROCESS_QUERY_INFORMATION is denied by some protected processes but
            // the limited variant still allows reading. Retry before giving up.
            handle.Dispose();
            uint limited = NativeMethods.ProcessQueryLimitedInformation | NativeMethods.ProcessVmRead;
            if (forInjection)
                limited |= NativeMethods.ProcessVmWrite | NativeMethods.ProcessVmOperation | NativeMethods.ProcessCreateThread;
            handle = NativeMethods.OpenProcess(limited, false, processId);

            if (handle.IsInvalid)
            {
                int code = Marshal.GetLastWin32Error();
                error = code switch
                {
                    5 => "Access denied — the process runs at a higher integrity level or is protected.",
                    87 => "The process does not exist.",
                    _ => $"OpenProcess failed (Win32 error {code}).",
                };
                handle.Dispose();
                return null;
            }
        }

        string name = string.Empty;
        try
        {
            using var process = Process.GetProcessById(processId);
            name = process.ProcessName + ".exe";
        }
        catch
        {
            name = $"pid-{processId}";
        }

        bool isWow64 = false;
        ProcessArchitecture architecture = ProcessArchitecture.X64;
        if (NativeMethods.IsWow64Process2(handle.DangerousGetHandle(), out ushort processMachine, out ushort nativeMachine))
        {
            if (processMachine != 0)
            {
                isWow64 = true;
                architecture = processMachine switch
                {
                    0x014C => ProcessArchitecture.X86,
                    0x01C4 => ProcessArchitecture.Arm,
                    0xAA64 => ProcessArchitecture.Arm64,
                    _ => ProcessArchitecture.Unknown,
                };
            }
            else
            {
                architecture = nativeMachine switch
                {
                    0x8664 => ProcessArchitecture.X64,
                    0xAA64 => ProcessArchitecture.Arm64,
                    0x014C => ProcessArchitecture.X86,
                    _ => ProcessArchitecture.Unknown,
                };
                isWow64 = nativeMachine == 0x014C;
            }
        }
        else if (NativeMethods.IsWow64Process(handle.DangerousGetHandle(), out bool wow64) && wow64)
        {
            isWow64 = true;
            architecture = ProcessArchitecture.X86;
        }

        return new RemoteProcess(processId, name, handle, architecture, isWow64);
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes, zero-filling unreadable pages.</summary>
    public byte[] ReadBytes(ulong address, int count, out int unreadablePages)
    {
        var buffer = new byte[Math.Max(0, count)];
        unreadablePages = ReadInto(address, buffer, 0, buffer.Length);
        return buffer;
    }

    /// <summary>
    /// Reads into a buffer and returns the number of 4 KB pages that could not be
    /// read (those pages stay zero-filled). The caller must pass a valid
    /// <paramref name="offset"/> / <paramref name="count"/> window; invalid ranges
    /// throw <see cref="ArgumentOutOfRangeException"/> instead of corrupting memory.
    /// </summary>
    public unsafe int ReadInto(ulong address, byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset < 0 || count < 0 || offset > buffer.Length - count)
        {
            throw new ArgumentOutOfRangeException(nameof(count),
                $"The range offset={offset}, count={count} does not fit a buffer of {buffer.Length} bytes.");
        }

        if (count == 0)
            return 0;

        int unreadable = 0;
        int position = 0;

        fixed (byte* pointer = &buffer[offset])
        {
            while (position < count)
            {
                int chunk = Math.Min(count - position, 1024 * 1024);
                if (TryReadChunk(address + (ulong)position, pointer + position, chunk, out int got))
                {
                    position += chunk;
                    continue;
                }

                if (got > 0)
                {
                    // A partial copy is still useful; continue after the bytes the
                    // kernel managed to copy rather than discarding them.
                    position += got;
                    continue;
                }

                // The image contains a gap that is only reserved (this is normal
                // for .NET modules with 0x2000 section alignment). Read the
                // window page by page so committed pages are preserved and the
                // reserved holes simply stay zero filled.
                int window = Math.Min(chunk, 256 * 1024);
                for (int inner = 0; inner < window; inner += 0x1000)
                {
                    int slice = Math.Min(0x1000, window - inner);
                    if (!TryReadChunk(address + (ulong)(position + inner), pointer + position + inner, slice, out int pageRead) ||
                        pageRead < slice)
                    {
                        unreadable++;
                    }
                }

                position += window;
            }
        }

        return unreadable;
    }

    private unsafe bool TryReadChunk(ulong address, byte* destination, int size, out int bytesRead)
    {
        bool ok = NativeMethods.ReadProcessMemory(
            _handle,
            (IntPtr)(long)address,
            destination,
            (IntPtr)size,
            out IntPtr read);

        bytesRead = (int)read;
        return ok && bytesRead == size;
    }

    public unsafe bool WriteBytes(ulong address, byte[] data, out string? error)
    {
        error = null;
        if (_disposed)
        {
            error = "the process handle is closed";
            return false;
        }

        fixed (byte* pointer = data)
        {
            bool ok = NativeMethods.WriteProcessMemory(_handle, (IntPtr)(long)address, pointer,
                (IntPtr)data.Length, out IntPtr written);
            if (!ok || (int)written != data.Length)
            {
                error = $"WriteProcessMemory failed (Win32 error {Marshal.GetLastWin32Error()})";
                return false;
            }
        }

        return true;
    }

    public bool IsAlive
    {
        get
        {
            if (_disposed)
                return false;

            // WaitForSingleObject needs SYNCHRONIZE, which an injection handle
            // does not necessarily carry; GetExitCodeProcess only needs query
            // rights and reports STILL_ACTIVE for a running process.
            if (!NativeMethods.GetExitCodeProcess(_handle.DangerousGetHandle(), out uint exitCode))
                return true;
            return exitCode == 259; // STILL_ACTIVE
        }
    }

    public IReadOnlyList<ModuleRecord> GetModules(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var modules = new List<ModuleRecord>(256);

        IntPtr snapshot = NativeMethods.CreateToolhelp32Snapshot(
            NativeMethods.Th32csSnapModule | NativeMethods.Th32csSnapModule32, Id);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            return modules;

        try
        {
            var entry = new NativeMethods.MODULEENTRY32W
            {
                dwSize = (uint)Marshal.SizeOf<NativeMethods.MODULEENTRY32W>(),
            };

            bool hasEntry = NativeMethods.Module32FirstW(snapshot, ref entry);
            while (hasEntry)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string name = entry.szModule ?? string.Empty;
                string path = entry.szExePath ?? string.Empty;
                ulong baseAddress = (ulong)entry.modBaseAddr.ToInt64();
                uint size = entry.modBaseSize;

                ManagedImage managed = ManagedImage.Native;
                if (baseAddress != 0 && size >= 0x1000 && LooksLikeMz(baseAddress))
                    managed = ProbeManagedImage(baseAddress, size);

                bool isFramework = IsFrameworkPath(path);
                bool exists = path.Length > 0 && File.Exists(path);

                modules.Add(new ModuleRecord
                {
                    Name = name,
                    Path = path.Length > 0 ? path : null,
                    BaseAddress = baseAddress,
                    Size = size,
                    IsManaged = managed.IsManaged,
                    MetadataVersion = managed.RuntimeVersion,
                    IsFramework = isFramework,
                    IsInMemory = path.Length == 0 || !exists,
                });

                hasEntry = NativeMethods.Module32NextW(snapshot, ref entry);
            }

            return modules;
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }
    }

    private bool LooksLikeMz(ulong address)
    {
        var probe = new byte[2];
        return ReadInto(address, probe, 0, 2) == 0 && probe[0] == (byte)'M' && probe[1] == (byte)'Z';
    }

    /// <summary>Diagnostic view of the memory protection map for one module.</summary>
    public IReadOnlyList<string> DescribeMemory(ulong address, int pages)
    {
        var lines = new List<string>(pages);
        int size = Marshal.SizeOf<NativeMethods.MEMORY_BASIC_INFORMATION64>();
        for (int i = 0; i < pages; i++)
        {
            ulong page = address + (ulong)i * 0x1000;
            if (!NativeMethods.VirtualQueryEx(_handle, (IntPtr)(long)page, out var info, (IntPtr)size))
            {
                lines.Add($"0x{page:X}: query failed ({Marshal.GetLastWin32Error()})");
                continue;
            }

            lines.Add($"0x{page:X}: state=0x{info.State:X4} type=0x{info.Type:X4} protect=0x{info.Protect:X2} region=0x{info.RegionSize:X} alloc=0x{info.AllocationBase:X}");
            if (info.RegionSize > 0x1000 && info.BaseAddress == page)
            {
                // RegionSize can exceed int.MaxValue; never let the skip wrap the loop counter.
                long pagesInRegion = (long)(info.RegionSize / 0x1000);
                int skip = pagesInRegion > int.MaxValue ? int.MaxValue : (int)pagesInRegion - 1;
                i += skip;
            }
        }
        return lines;
    }

    /// <summary>
    /// Parse the CLI header of a loader-mapped image. Works across bitness
    /// because PE RVAs are bitness independent.
    /// </summary>
    public ManagedImage ProbeManagedImage(ulong baseAddress, uint moduleSize)
    {
        var header = ReadBytes(baseAddress, 0x1000, out _);
        if (!PeImageLayout.TryReadHeaders(header, out PeHeaders headers))
            return ManagedImage.Native;
        if (headers.ClrDirectoryRva == 0 || headers.ClrDirectorySize < 72)
            return ManagedImage.Native;

        int cliOffsetInHeader = -1;
        if (headers.ClrDirectoryRva < 0x1000)
            cliOffsetInHeader = (int)headers.ClrDirectoryRva;

        byte[] cliBuffer;
        int cliOffset;
        if (cliOffsetInHeader >= 0 && cliOffsetInHeader + 72 <= header.Length)
        {
            cliBuffer = header;
            cliOffset = cliOffsetInHeader;
        }
        else
        {
            cliBuffer = ReadBytes(baseAddress + headers.ClrDirectoryRva, 72, out _);
            cliOffset = 0;
        }

        if (PeImageLayout.ReadU32(cliBuffer, cliOffset) < 72)
            return ManagedImage.Native;

        uint metadataRva = PeImageLayout.ReadU32(cliBuffer, cliOffset + 8);
        uint metadataSize = PeImageLayout.ReadU32(cliBuffer, cliOffset + 12);
        uint clrFlags = PeImageLayout.ReadU32(cliBuffer, cliOffset + 16);
        if (metadataRva == 0 || metadataSize < 16)
            return ManagedImage.Native;

        var metadataProbe = ReadBytes(baseAddress + metadataRva, (int)Math.Min(metadataSize, 0x800), out _);
        if (PeImageLayout.ReadU32(metadataProbe, 0) != PeImageLayout.MetadataSignature)
            return ManagedImage.Native;

        int versionLength = PeImageLayout.ReadI32(metadataProbe, 12);
        string version = string.Empty;
        if (versionLength > 0 && versionLength <= 256 && 16 + versionLength <= metadataProbe.Length)
        {
            version = System.Text.Encoding.UTF8.GetString(metadataProbe, 16, versionLength).TrimEnd('\0');
        }

        return new ManagedImage
        {
            IsManaged = true,
            RuntimeVersion = version,
            MajorRuntimeVersion = PeImageLayout.ReadU16(cliBuffer, cliOffset + 4),
            MetadataRva = metadataRva,
            MetadataSize = metadataSize,
            ImageSize = headers.SizeOfImage == 0 ? moduleSize : headers.SizeOfImage,
            ClrFlags = clrFlags,
            Machine = headers.Machine,
            SectionName = PeImageLayout.SectionOf(headers, headers.ClrDirectoryRva),
        };
    }

    /// <summary>Reads the target command line from its PEB (best effort).</summary>
    public string? TryReadCommandLine()
    {
        try
        {
            ulong peb = 0;
            var basic = new NativeMethods.PROCESS_BASIC_INFORMATION();
            int size = Marshal.SizeOf<NativeMethods.PROCESS_BASIC_INFORMATION>();
            int status = NativeMethods.NtQueryInformationProcess(_handle, 0, ref basic, size, out _);
            if (status == 0)
                peb = (ulong)basic.PebBaseAddress.ToInt64();

            if (_isWow64Target)
            {
                // ProcessWow64Information (26) returns a ULONG_PTR to the 32-bit
                // PEB. There is no separate "NtQueryInformationProcessWow64"
                // export in ntdll, so the generic entry point must be used.
                status = NativeMethods.NtQueryInformationProcessPointer(_handle,
                    NativeMethods.ProcessWow64Information, out IntPtr peb32, IntPtr.Size, out _);
                if (status != 0 || peb32 == IntPtr.Zero)
                    return null;
                peb = (ulong)peb32.ToInt64();
            }

            if (peb == 0)
                return null;

            ulong parameters;
            if (_isWow64Target)
            {
                if (!TryReadPointer32(peb + 0x10, out uint parameters32))
                    return null;
                parameters = parameters32;
                return ReadUnicodeString32(parameters + 0x40);
            }

            var pointerBuffer = new byte[8];
            if (ReadInto(peb + 0x20, pointerBuffer, 0, 8) > 0)
                return null;
            parameters = PeImageLayout.ReadU64(pointerBuffer, 0);
            return ReadUnicodeString64(parameters + 0x70);
        }
        catch
        {
            return null;
        }
    }

    private string? ReadUnicodeString64(ulong address)
    {
        var buffer = new byte[16];
        if (ReadInto(address, buffer, 0, 16) > 0)
            return null;
        int length = PeImageLayout.ReadU16(buffer, 0) & ~1; // UNICODE_STRING.Length is bytes; ignore a bogus odd tail
        ulong textAddress = PeImageLayout.ReadU64(buffer, 8);
        if (length <= 0 || length > 64 * 1024 || textAddress == 0)
            return null;
        var text = ReadBytes(textAddress, length, out _);
        return System.Text.Encoding.Unicode.GetString(text);
    }

    private string? ReadUnicodeString32(ulong address)
    {
        var buffer = new byte[8];
        if (ReadInto(address, buffer, 0, 8) > 0)
            return null;
        int length = PeImageLayout.ReadU16(buffer, 0) & ~1;
        uint textAddress = PeImageLayout.ReadU32(buffer, 4);
        if (length <= 0 || length > 64 * 1024 || textAddress == 0)
            return null;
        var text = ReadBytes(textAddress, length, out _);
        return System.Text.Encoding.Unicode.GetString(text);
    }

    private bool TryReadPointer32(ulong address, out uint value)
    {
        var buffer = new byte[4];
        if (ReadInto(address, buffer, 0, 4) > 0)
        {
            value = 0;
            return false;
        }
        value = PeImageLayout.ReadU32(buffer, 0);
        return true;
    }

    internal static bool IsFrameworkPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        return path.Contains(@"\Microsoft.NET\Framework", StringComparison.OrdinalIgnoreCase)
            || path.Contains(@"\Microsoft.NET\assembly\", StringComparison.OrdinalIgnoreCase)
            || path.Contains(@"\dotnet\shared\", StringComparison.OrdinalIgnoreCase)
            || path.Contains(@"\dotnet\host\fxr\", StringComparison.OrdinalIgnoreCase)
            || path.Contains(@"\Windows\Microsoft.NET\", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsFrameworkName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        string stem = name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        return stem.Equals("mscorlib", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("System", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("netstandard", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("WindowsBase", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _handle.Dispose();
    }
}
