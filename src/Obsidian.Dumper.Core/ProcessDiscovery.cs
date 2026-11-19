using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Obsidian.Dumper.Core.Native;
using Obsidian.Dumper.Core.Model;

namespace Obsidian.Dumper.Core;

/// <summary>Token / integrity helpers for a target process.</summary>
internal static class ProcessSecurity
{
    public static (IntegrityLevel Level, bool Elevated) Read(SafeProcessHandle process)
    {
        if (process.IsInvalid)
            return (IntegrityLevel.Unknown, false);

        if (!NativeMethods.OpenProcessToken(process, NativeMethods.TokenQuery, out SafeAccessTokenHandle token))
            return (IntegrityLevel.Unknown, false);

        using (token)
        {
            IntegrityLevel level = ReadIntegrity(token);
            bool elevated = ReadElevation(token);
            return (level, elevated);
        }
    }

    private static IntegrityLevel ReadIntegrity(SafeAccessTokenHandle token)
    {
        NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel, null, 0, out int size);
        int minimum = IntPtr.Size + 8;
        if (size < minimum || size > 4096)
            return IntegrityLevel.Unknown;

        var buffer = new byte[size];
        if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel, buffer, size, out _))
            return IntegrityLevel.Unknown;

        // TOKEN_MANDATORY_LABEL = { SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes; } }.
        // The SID is copied into the same buffer; read the PSID at offset 0 and
        // validate that it points inside the buffer, then use the SID helpers
        // instead of scanning raw bytes for a SID-shaped pattern.
        GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            IntPtr bufferAddress = handle.AddrOfPinnedObject();
            IntPtr sid = Marshal.ReadIntPtr(bufferAddress);
            long sidOffset = sid.ToInt64() - bufferAddress.ToInt64();
            if (sid == IntPtr.Zero || sidOffset < 0 || sidOffset + 8 > buffer.Length)
            {
                // Some layouts return a rebased/zero pointer; SID_AND_ATTRIBUTES is
                // pointer-sized, so the SID follows it in the returned blob.
                sidOffset = IntPtr.Size == 8 ? 16 : 8;
                if (sidOffset + 8 > buffer.Length)
                    return IntegrityLevel.Unknown;
                sid = bufferAddress + (int)sidOffset;
            }

            // Validate the SID shape: revision 1, at least one sub-authority and
            // the SECURITY_MANDATORY_LABEL_AUTHORITY (16) identifier authority.
            if (Marshal.ReadByte(sid) != 1)
                return IntegrityLevel.Unknown;
            byte subAuthorities = Marshal.ReadByte(sid, 1);
            if (subAuthorities == 0)
                return IntegrityLevel.Unknown;
            for (int i = 0; i < 5; i++)
            {
                if (Marshal.ReadByte(sid, 2 + i) != 0)
                    return IntegrityLevel.Unknown;
            }
            if (Marshal.ReadByte(sid, 7) != 16)
                return IntegrityLevel.Unknown;

            IntPtr ridAddress = NativeMethods.GetSidSubAuthority(sid, (uint)(subAuthorities - 1));
            if (ridAddress == IntPtr.Zero)
                return IntegrityLevel.Unknown;

            uint rid = unchecked((uint)Marshal.ReadInt32(ridAddress));
            return rid switch
            {
                0x0000 => IntegrityLevel.Untrusted,
                0x1000 => IntegrityLevel.Low,
                0x2000 => IntegrityLevel.Medium,
                0x2100 => IntegrityLevel.Protected,
                0x3000 => IntegrityLevel.High,
                0x4000 => IntegrityLevel.System,
                _ => rid > 0x4000 ? IntegrityLevel.Protected : IntegrityLevel.Unknown,
            };
        }
        finally
        {
            handle.Free();
        }
    }

    private static bool ReadElevation(SafeAccessTokenHandle token)
    {
        var buffer = new byte[4];
        if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenElevation, buffer, 4, out _))
            return false;
        return BitConverter.ToUInt32(buffer, 0) != 0;
    }

    internal static string Describe(SafeProcessHandle process)
    {
        if (!NativeMethods.OpenProcessToken(process, NativeMethods.TokenQuery, out SafeAccessTokenHandle token))
            return "OpenProcessToken failed (" + Marshal.GetLastWin32Error() + ")";

        using (token)
        {
            NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel, null, 0, out int size);
            var buffer = new byte[Math.Max(size, 64)];
            bool ok = NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel, buffer, buffer.Length,
                out int written);
            var hex = new System.Text.StringBuilder();
            for (int i = 0; i < Math.Min(size, 40); i++)
                hex.Append(buffer[i].ToString("X2")).Append(' ');

            return $"size={size} ok={ok} written={written} level={ReadIntegrity(token)} bytes={hex}";
        }
    }
}

/// <summary>
/// Fast, read-only process enumeration. CLR detection uses only the module
/// list (no target memory reads) so a full refresh stays interactive.
/// </summary>
public static class ProcessDiscovery
{
    private static readonly Dictionary<string, (string? Company, string? Version)> VersionCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object VersionLock = new();

    public static IReadOnlyList<ProcessRecord> Enumerate(CancellationToken cancellationToken = default,
        IProgress<int>? progress = null)
    {
        int[] ids = EnumerateProcessIds(cancellationToken);
        var records = new ProcessRecord[ids.Length];
        int completed = 0;

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8),
            CancellationToken = cancellationToken,
        };

        Parallel.For(0, ids.Length, options, index =>
        {
            records[index] = Inspect(ids[index], cancellationToken);
            int done = Interlocked.Increment(ref completed);
            if (done % 16 == 0 || done == ids.Length)
                progress?.Report(done);
        });

        var list = new List<ProcessRecord>(records);
        list.Sort((a, b) =>
        {
            int managed = b.IsManaged.CompareTo(a.IsManaged);
            return managed != 0 ? managed : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        progress?.Report(list.Count);
        return list;
    }

    private static int[] EnumerateProcessIds(CancellationToken cancellationToken)
    {
        var ids = new List<int>(320);
        IntPtr snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            return ids.ToArray();

        try
        {
            var entry = new NativeMethods.PROCESSENTRY32W
            {
                dwSize = (uint)Marshal.SizeOf<NativeMethods.PROCESSENTRY32W>(),
            };

            if (!NativeMethods.Process32FirstW(snapshot, ref entry))
                return ids.ToArray();

            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.th32ProcessID > 4)
                    ids.Add((int)entry.th32ProcessID);
            }
            while (NativeMethods.Process32NextW(snapshot, ref entry));
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }

        return ids.ToArray();
    }

    /// <summary>
    /// Builds a full process record. Never returns null: an inaccessible process
    /// is represented by a record with <see cref="ProcessRecord.CanRead"/> false
    /// and <see cref="ProcessRecord.AccessProblem"/> set, so callers do not need a
    /// placeholder path.
    /// </summary>
    public static ProcessRecord Inspect(int processId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using RemoteProcess? process = RemoteProcess.TryOpen(processId, out string? error);
        if (process == null)
        {
            return new ProcessRecord
            {
                Id = processId,
                Name = $"pid-{processId}",
                Architecture = ProcessArchitecture.Unknown,
                CanRead = false,
                AccessProblem = error,
                Integrity = IntegrityLevel.Unknown,
            };
        }

        NativeMethods.ProcessIdToSessionId((uint)processId, out uint sessionId);
        (IntegrityLevel integrity, bool elevated) = ProcessSecurity.Read(process.Handle);

        string? imagePath = QueryImagePath(process);
        IReadOnlyList<(string Name, string Path, ulong Base, uint Size)> modules = QueryModuleList(processId);

        ClrInfo? clr = DetectClr(modules, process.Architecture);
        string? company = null;
        string? version = null;
        if (imagePath != null)
        {
            (company, version) = ReadVersionInfo(imagePath);
        }

        DateTime? startTime = null;
        long? workingSet = null;
        if (NativeMethods.GetProcessTimes(process.Handle, out NativeMethods.FILETIME creation, out _, out _, out _))
        {
            try
            {
                startTime = DateTime.FromFileTimeUtc(creation.ToInt64()).ToLocalTime();
            }
            catch
            {
                startTime = null;
            }
        }

        var counters = new NativeMethods.PROCESS_MEMORY_COUNTERS
        {
            cb = (uint)Marshal.SizeOf<NativeMethods.PROCESS_MEMORY_COUNTERS>(),
        };
        if (NativeMethods.K32GetProcessMemoryInfo(process.Handle, ref counters, counters.cb))
            workingSet = (long)counters.WorkingSetSize.ToUInt64();

        return new ProcessRecord
        {
            Id = processId,
            Name = process.Name,
            ImagePath = imagePath,
            Architecture = process.Architecture,
            SessionId = (int)sessionId,
            Integrity = integrity,
            IsElevated = elevated,
            Clr = clr,
            Company = company,
            FileVersion = version,
            CanRead = true,
            ModuleCount = modules.Count,
            StartTime = startTime,
            WorkingSet = workingSet,
        };
    }

    public static string? QueryImagePath(RemoteProcess process)
    {
        // QueryFullProcessImageNameW reports the required size when the buffer is
        // too small; retry with the reported (or a grown) capacity instead of
        // failing for paths longer than the initial 1024 characters.
        int capacity = 1024;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var builder = new System.Text.StringBuilder(capacity);
            uint size = (uint)builder.Capacity;
            if (NativeMethods.QueryFullProcessImageNameW(process.Handle, 0, builder, ref size))
                return builder.ToString(0, (int)size);

            int code = Marshal.GetLastWin32Error();
            if (code != NativeMethods.ErrorInsufficientBuffer)
                return null;

            capacity = size > (uint)capacity
                ? (int)Math.Min((long)size + 1, 1_048_576)
                : capacity * 4;
        }

        return null;
    }

    internal static IReadOnlyList<(string Name, string Path, ulong Base, uint Size)> QueryModuleList(int processId)
    {
        var modules = new List<(string, string, ulong, uint)>(256);
        IntPtr snapshot = NativeMethods.CreateToolhelp32Snapshot(
            NativeMethods.Th32csSnapModule | NativeMethods.Th32csSnapModule32, processId);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            return modules;

        try
        {
            var entry = new NativeMethods.MODULEENTRY32W
            {
                dwSize = (uint)Marshal.SizeOf<NativeMethods.MODULEENTRY32W>(),
            };

            if (!NativeMethods.Module32FirstW(snapshot, ref entry))
                return modules;

            do
            {
                modules.Add((entry.szModule ?? string.Empty, entry.szExePath ?? string.Empty,
                    (ulong)entry.modBaseAddr.ToInt64(), entry.modBaseSize));
            }
            while (NativeMethods.Module32NextW(snapshot, ref entry));
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }

        return modules;
    }

    internal static ClrInfo? DetectClr(IReadOnlyList<(string Name, string Path, ulong Base, uint Size)> modules,
        ProcessArchitecture architecture)
    {
        foreach (var module in modules)
        {
            if (!module.Name.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase))
                continue;

            string directory = string.IsNullOrEmpty(module.Path)
                ? string.Empty
                : System.IO.Path.GetDirectoryName(module.Path) ?? string.Empty;
            string runtimeVersion = directory.Length > 0 ? System.IO.Path.GetFileName(directory) : string.Empty;

            // A normal shared-runtime deployment puts coreclr.dll in
            // ...\Microsoft.NETCore.App\<version>. Self-contained apps keep it next
            // to the executable, so the folder name is meaningless there.
            if (string.IsNullOrWhiteSpace(runtimeVersion) || !char.IsDigit(runtimeVersion[0]))
            {
                runtimeVersion = TryReadVersionInfo(module.Path, out string? productVersion, out string? fileVersion)
                    ? NormalizeFallbackRuntimeVersion(productVersion ?? fileVersion ?? "unknown")
                    : "unknown";
            }

            return new ClrInfo
            {
                Flavor = ClrFlavor.Core,
                RuntimeVersion = runtimeVersion,
                DisplayName = runtimeVersion.Contains("unknown", StringComparison.OrdinalIgnoreCase)
                    ? ".NET"
                    : $".NET {TrimRuntime(runtimeVersion)}",
                ClrModulePath = module.Path,
                RuntimeDirectory = directory,
            };
        }

        foreach (var module in modules)
        {
            if (!module.Name.Equals("clr.dll", StringComparison.OrdinalIgnoreCase) &&
                !module.Name.Equals("mscorwks.dll", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string version = TryReadVersionInfo(module.Path, out string? productVersion, out string? fileVersion)
                ? productVersion ?? fileVersion ?? "4.0"
                : "4.0";
            return new ClrInfo
            {
                Flavor = ClrFlavor.Framework,
                RuntimeVersion = version,
                DisplayName = $".NET Framework {version}",
                ClrModulePath = module.Path,
                RuntimeDirectory = string.IsNullOrEmpty(module.Path)
                    ? string.Empty
                    : System.IO.Path.GetDirectoryName(module.Path) ?? string.Empty,
            };
        }

        return null;
    }

    /// <summary>Reads a module's version resource; missing files and bad resources are not fatal.</summary>
    private static bool TryReadVersionInfo(string path, out string? productVersion, out string? fileVersion)
    {
        productVersion = null;
        fileVersion = null;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            productVersion = string.IsNullOrWhiteSpace(info.ProductVersion) ? null : info.ProductVersion.Trim();
            fileVersion = string.IsNullOrWhiteSpace(info.FileVersion) ? null : info.FileVersion.Trim();
            return true;
        }
        catch (Exception)
        {
            // Version resources are optional and some protected modules cannot be
            // queried; never let a single module abort the whole enumeration.
            return false;
        }
    }

    private static string TrimRuntime(string version)
    {
        // "8.0.28" stays; "8.0.0-preview.7" becomes "8.0 preview 7".
        int dash = version.IndexOf('-');
        return dash > 0 ? version[..dash] + " " + version[(dash + 1)..] : version;
    }

    /// <summary>
    /// Normalises a coreclr.dll ProductVersion used as the fallback runtime label
    /// for self-contained apps. That resource is raw build metadata such as
    /// "10,0,1426,52326 @Commit: 0427b9abbead…" (commas instead of dots, build
    /// commit and other fields appended); the target list needs a short version.
    /// </summary>
    private static string NormalizeFallbackRuntimeVersion(string version)
    {
        string text = version.Trim();

        // Cut the version from the first space or "@" (commit/build metadata).
        int cut = text.IndexOfAny([' ', '\t']);
        int at = text.IndexOf('@');
        if (at >= 0 && (cut < 0 || at < cut))
            cut = at;
        if (cut > 0)
            text = text[..cut];

        // Some self-contained builds report "10,0,1426,52326".
        text = text.Replace(',', '.').Trim().TrimEnd('.');

        const int MaxLength = 20;
        if (text.Length > MaxLength)
            text = text[..MaxLength].TrimEnd('.');

        return text.Length == 0 ? "unknown" : text;
    }

    private static (string? Company, string? Version) ReadVersionInfo(string path)
    {
        lock (VersionLock)
        {
            if (VersionCache.TryGetValue(path, out var cached))
                return cached;
        }

        (string?, string?) result = (null, null);
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            string? company = string.IsNullOrWhiteSpace(info.CompanyName) ? null : info.CompanyName;
            string? version = info.FileVersion ?? info.ProductVersion;
            result = (company, string.IsNullOrWhiteSpace(version) ? null : version.Trim());
        }
        catch
        {
            // Version resources are optional.
        }

        lock (VersionLock)
        {
            // A refresh loop can walk dozens of image paths; keep the cache bounded.
            if (VersionCache.Count > 4096)
                VersionCache.Clear();
            VersionCache[path] = result;
        }
        return result;
    }
}
