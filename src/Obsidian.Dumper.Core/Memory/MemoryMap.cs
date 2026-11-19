using System.Globalization;
using Obsidian.Dumper.Core.Model;
using Obsidian.Dumper.Core.Native;

namespace Obsidian.Dumper.Core.Memory;

/// <summary>State of a virtual memory region (MEMORY_BASIC_INFORMATION.State).</summary>
public enum MemoryRegionState
{
    Free = 0,
    Reserve = 1,
    Commit = 2,
    Unknown = 3,
}

/// <summary>Type of a virtual memory region (MEMORY_BASIC_INFORMATION.Type).</summary>
public enum MemoryRegionType
{
    Unknown = 0,
    Private = 1,
    Mapped = 2,
    Image = 3,
}

/// <summary>
/// One <c>VirtualQueryEx</c> region of a target process, with the module that owns
/// it (if any) and human-readable access labels.
/// </summary>
public sealed class MemoryRegion
{
    public ulong BaseAddress { get; init; }
    public ulong AllocationBase { get; init; }
    public ulong Size { get; init; }
    public uint Protect { get; init; }
    public uint AllocationProtect { get; init; }
    public uint State { get; init; }
    public uint Type { get; init; }
    public string? ModuleName { get; init; }
    public ulong ModuleBase { get; init; }
    public string? ModulePath { get; init; }

    public MemoryRegionState StateKind => State switch
    {
        NativeMethods.MemCommit => MemoryRegionState.Commit,
        NativeMethods.MemReserve => MemoryRegionState.Reserve,
        0x10000 => MemoryRegionState.Free,
        _ => MemoryRegionState.Unknown,
    };

    public MemoryRegionType TypeKind => Type switch
    {
        NativeMethods.MemImage => MemoryRegionType.Image,
        NativeMethods.MemMapped => MemoryRegionType.Mapped,
        NativeMethods.MemPrivate => MemoryRegionType.Private,
        _ => MemoryRegionType.Unknown,
    };

    public bool IsCommitted => StateKind == MemoryRegionState.Commit;
    public bool IsReadable => IsCommitted && (Protect & 0x01) == 0 && (Protect & 0x100) == 0;
    public bool IsWritable => IsReadable && (Protect & (0x04 | 0x08 | 0x40 | 0x80)) != 0;
    public bool IsExecutable => IsCommitted && (Protect & (0x10 | 0x20 | 0x40 | 0x80)) != 0;
    public bool IsGuarded => (Protect & 0x100) != 0;

    /// <summary>RWX, or private executable memory — worth a second look.</summary>
    public bool IsSuspicious
        => IsCommitted && ((IsWritable && IsExecutable) || (IsExecutable && TypeKind == MemoryRegionType.Private));

    public bool HasModule => !string.IsNullOrEmpty(ModuleName);

    public string BaseLabel => "0x" + BaseAddress.ToString("X", CultureInfo.InvariantCulture);

    public string SizeLabel => FormatBytes((long)Size);

    public string ProtectLabel
    {
        get
        {
            uint protect = Protect & 0xFF;
            if ((Protect & 0x01) != 0)
                return "---";

            string access = protect switch
            {
                0x02 => "R--",
                0x04 => "RW-",
                0x08 => "RW-",
                0x10 => "--X",
                0x20 => "R-X",
                0x40 => "RWX",
                0x80 => "RWX",
                _ => "???",
            };

            if ((Protect & 0x100) != 0)
                access += "g";
            return access;
        }
    }

    public string StateLabel => StateKind switch
    {
        MemoryRegionState.Commit => "commit",
        MemoryRegionState.Reserve => "reserve",
        MemoryRegionState.Free => "free",
        _ => "?",
    };

    public string TypeLabel => TypeKind switch
    {
        MemoryRegionType.Image => "image",
        MemoryRegionType.Mapped => "mapped",
        MemoryRegionType.Private => "private",
        _ => "—",
    };

    public string ModuleLabel => HasModule ? ModuleName! : "—";

    public string FlagsLabel
    {
        get
        {
            if (IsSuspicious)
                return IsWritable && IsExecutable ? "RWX" : "private exec";
            if (IsGuarded)
                return "guard";
            if (IsExecutable)
                return "exec";
            return string.Empty;
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
            return "—";
        if (bytes < 1024)
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";

        double value = bytes;
        string[] units = ["KB", "MB", "GB", "TB"];
        int unit = -1;
        do
        {
            value /= 1024;
            unit++;
        }
        while (value >= 1024 && unit < units.Length - 1);

        return value.ToString("0.##", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}

/// <summary>Reads the virtual address space of a target process.</summary>
public static class MemoryMapReader
{
    private const int MaxRegions = 200_000;

    /// <summary>
    /// Enumerates committed and reserved regions. Free regions carry no useful
    /// information and would only add tens of thousands of empty rows.
    /// </summary>
    public static IReadOnlyList<MemoryRegion> Read(
        RemoteProcess process,
        IReadOnlyList<ModuleRecord>? modules = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);

        List<ModuleRecord> ordered = modules?
            .Where(m => m.BaseAddress != 0 && m.Size != 0)
            .OrderBy(m => m.BaseAddress)
            .ToList() ?? new List<ModuleRecord>();

        var regions = new List<MemoryRegion>(2048);
        ulong address = 0;
        ulong maxAddress = process.Is64Bit ? 0x7FFFFFFF_0000UL : 0xFFFF_FFFFUL;
        int infoSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORY_BASIC_INFORMATION64>();

        while (address < maxAddress && regions.Count < MaxRegions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!NativeMethods.VirtualQueryEx(
                    process.Handle,
                    (IntPtr)(long)address,
                    out NativeMethods.MEMORY_BASIC_INFORMATION64 info,
                    (IntPtr)infoSize))
            {
                break;
            }

            ulong baseAddress = info.BaseAddress;
            ulong size = info.RegionSize;
            if (size == 0)
                break;

            if (info.State != 0x10000) // MEM_FREE
            {
                (ModuleRecord? module, bool inside) = FindModule(ordered, baseAddress, size);
                regions.Add(new MemoryRegion
                {
                    BaseAddress = baseAddress,
                    AllocationBase = info.AllocationBase,
                    Size = size,
                    Protect = info.Protect,
                    AllocationProtect = info.AllocationProtect,
                    State = info.State,
                    Type = info.Type,
                    ModuleName = module?.Name,
                    ModuleBase = module?.BaseAddress ?? 0,
                    ModulePath = module?.Path,
                });
            }

            ulong next = baseAddress + size;
            if (next <= address)
                break; // overflow / malformed response
            address = next;
        }

        return regions;
    }

    private static (ModuleRecord? Module, bool Inside) FindModule(
        IReadOnlyList<ModuleRecord> ordered,
        ulong address,
        ulong size)
    {
        // Binary search for the last module whose base is <= address.
        int low = 0;
        int high = ordered.Count - 1;
        int found = -1;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (ordered[mid].BaseAddress <= address)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        if (found < 0)
            return (null, false);

        ModuleRecord candidate = ordered[found];
        ulong end = candidate.BaseAddress + candidate.Size;
        bool startsInside = address >= candidate.BaseAddress && address < end;
        if (!startsInside)
            return (null, false);

        // A region fully covered by the module is the normal case; a region that
        // starts inside a module but extends past it is still attributed to it.
        return (candidate, address + size <= end);
    }

}
