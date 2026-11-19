using System.ComponentModel;
using System.Runtime.CompilerServices;
using Obsidian.Dumper.Shared;

namespace Obsidian.Dumper.Core.Model;

public enum ProcessArchitecture
{
    Unknown = 0,
    X86 = 1,
    X64 = 2,
    Arm64 = 3,
    Arm = 4,
}

public enum ClrFlavor
{
    None = 0,
    Framework,
    Core,
}

public enum IntegrityLevel
{
    Unknown = 0,
    Untrusted,
    Low,
    Medium,
    High,
    System,
    Protected,
}

public enum DumpStatus
{
    Idle = 0,
    Dumping,
    Dumped,
    Skipped,
    Failed,
}

public enum DumpMode
{
    /// <summary>Read the loader-mapped image and rebuild a valid PE file.</summary>
    Memory = 0,

    /// <summary>Write the mapped image untouched (forensics / broken headers).</summary>
    Raw,

    /// <summary>Managed agent executed inside the target (dynamic and hidden modules).</summary>
    Deep,
}

public sealed class ClrInfo
{
    public ClrFlavor Flavor { get; init; }
    public string RuntimeVersion { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string ClrModulePath { get; init; } = string.Empty;
    public string RuntimeDirectory { get; init; } = string.Empty;
    public bool IsServerGc { get; init; }
    public bool IsPreview { get; init; }

    public bool IsManaged => Flavor != ClrFlavor.None;

    public override string ToString() => DisplayName;
}

public sealed record ProcessRecord
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? ImagePath { get; init; }
    public ProcessArchitecture Architecture { get; init; }
    public int SessionId { get; init; }
    public IntegrityLevel Integrity { get; init; }
    public bool IsElevated { get; init; }
    public ClrInfo? Clr { get; init; }
    public string? Company { get; init; }
    public string? FileVersion { get; init; }
    public bool CanRead { get; init; }
    public string? AccessProblem { get; init; }
    public int ModuleCount { get; init; }
    public string? CommandLine { get; init; }
    public DateTime? StartTime { get; init; }
    public long? WorkingSet { get; init; }

    public bool IsManaged => Clr != null && Clr.Flavor != ClrFlavor.None;
    public bool IsWow64 => Architecture == ProcessArchitecture.X86 && Environment.Is64BitOperatingSystem;
    public string DisplayName => Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Name[..^4] : Name;
    public string PidLabel => $"pid {Id}";

    public string ArchitectureLabel => Architecture switch
    {
        ProcessArchitecture.X86 => "x86",
        ProcessArchitecture.X64 => "x64",
        ProcessArchitecture.Arm64 => "arm64",
        ProcessArchitecture.Arm => "arm",
        _ => "?",
    };

    public string RuntimeLabel => Clr == null || Clr.Flavor == ClrFlavor.None ? "native" : Clr.DisplayName;

    public string IntegrityLabel => Integrity switch
    {
        IntegrityLevel.Untrusted => "untrusted",
        IntegrityLevel.Low => "low",
        IntegrityLevel.Medium => "medium",
        IntegrityLevel.High => "high",
        IntegrityLevel.System => "system",
        IntegrityLevel.Protected => "protected",
        _ => "unknown",
    };

    public string SizeLabel => WorkingSet is > 0
        ? FormatBytes(WorkingSet.Value)
        : "—";

    public override string ToString() => $"{DisplayName} ({Id})";

    internal static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}

public sealed class ModuleRecord : INotifyPropertyChanged
{
    private bool _isSelected;
    private DumpStatus _status = DumpStatus.Idle;
    private string? _dumpedPath;
    private string? _note;

    public string Name { get; init; } = string.Empty;
    public string? Path { get; init; }
    public ulong BaseAddress { get; init; }
    public uint Size { get; init; }
    public bool IsManaged { get; init; }
    public string? MetadataVersion { get; init; }
    public bool IsFramework { get; init; }
    public bool IsInMemory { get; init; }
    public bool IsNative => !IsManaged;

    public DumpStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            Raise();
            Raise(nameof(StatusLabel));
            Raise(nameof(HasOutput));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            Raise();
        }
    }

    public string? DumpedPath
    {
        get => _dumpedPath;
        set
        {
            if (_dumpedPath == value) return;
            _dumpedPath = value;
            Raise();
            Raise(nameof(HasOutput));
        }
    }

    public string? Note
    {
        get => _note;
        set
        {
            if (_note == value) return;
            _note = value;
            Raise();
        }
    }

    public bool HasOutput => _dumpedPath != null;

    public string StatusLabel => Status switch
    {
        DumpStatus.Dumping => "dumping",
        DumpStatus.Dumped => "dumped",
        DumpStatus.Skipped => "skipped",
        DumpStatus.Failed => "failed",
        _ => "idle",
    };

    public string AddressLabel => $"0x{BaseAddress:X}";
    public string SizeLabel => ProcessRecord.FormatBytes(Size);
    public string KindLabel => IsManaged ? "managed" : "native";
    public string MetadataLabel => !IsManaged
        ? "—"
        : MetadataVersion switch
        {
            "v2.0.50727" => "v2",
            "v4.0.30319" => "v4",
            null or "" => "—",
            _ => MetadataVersion,
        };
    public string DisplayPath => Path ?? (IsInMemory ? "in-memory module" : "—");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>What the out-of-process probe learned from a CLI header in memory.</summary>
public sealed class ManagedImage
{
    public bool IsManaged { get; init; }
    public string? RuntimeVersion { get; init; }
    public ushort MajorRuntimeVersion { get; init; }
    public uint MetadataRva { get; init; }
    public uint MetadataSize { get; init; }
    public uint ImageSize { get; init; }
    public uint ClrFlags { get; init; }
    public bool IsIlOnly => (ClrFlags & 0x1) != 0;
    public bool IsStrongNameSigned => (ClrFlags & 0x8) != 0;
    public ushort Machine { get; init; }
    public string? SectionName { get; init; }

    public static ManagedImage Native { get; } = new() { IsManaged = false };
}

public sealed class DumpResult
{
    public ModuleRecord Module { get; init; } = null!;
    public bool Success { get; init; }
    public DumpMode Mode { get; init; }
    public string? OutputPath { get; init; }
    public long Bytes { get; init; }
    public string? Sha256 { get; init; }
    public string? Error { get; init; }
    public bool Verified { get; init; }
    public string? Verification { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public int DurationMs { get; init; }
}

/// <summary>What the dumper does when the computed output file already exists.</summary>
public enum DumpOverwritePolicy
{
    /// <summary>Write to the next free name (<c>name_2.dll</c>, <c>name_3.dll</c>, ...). Default.</summary>
    Rename = 0,

    /// <summary>Replace the existing file.</summary>
    Overwrite = 1,

    /// <summary>Leave the existing file untouched and fail the dump for this module.</summary>
    Fail = 2,
}

public sealed class DumpOptions
{
    private DumpOverwritePolicy _overwritePolicy = DumpOverwritePolicy.Rename;

    public string OutputDirectory { get; set; } = DefaultOutputDirectory();

    /// <summary>
    /// Optional parent directory for the deep-dump work folder (the managed
    /// agent, job/result files and payload). When null or empty the dumper
    /// picks a default writable location. The per-run GUID subfolder is always
    /// created below this path.
    /// </summary>
    public string? WorkDirectory { get; set; }

    public string FileNameTemplate { get; set; } = "{name}";
    public bool RebuildPe { get; set; } = true;
    public bool StripDebugDirectory { get; set; } = true;
    public bool StripBoundImports { get; set; } = true;
    public bool InvalidateStrongName { get; set; } = true;
    public bool VerifyWithMetadata { get; set; } = true;

    /// <summary>
    /// When a managed dump has unreadable metadata, try to patch the COR20
    /// metadata directory from the live CLR (ClrMD) before giving up. This is
    /// the anti-dump repair path; it is read-only on the target.
    /// </summary>
    public bool RepairMetadata { get; set; } = true;
    public bool IncludeFrameworkModules { get; set; }
    public bool DumpNativeModules { get; set; }
    public bool RawImage { get; set; }

    /// <summary>Policy applied when the computed output file already exists.</summary>
    public DumpOverwritePolicy OverwritePolicy
    {
        get => _overwritePolicy;
        set => _overwritePolicy = value;
    }

    /// <summary>
    /// Legacy boolean view of <see cref="OverwritePolicy"/>:
    /// <c>true</c> maps to <see cref="DumpOverwritePolicy.Overwrite"/> and
    /// <c>false</c> to <see cref="DumpOverwritePolicy.Rename"/>. Setting it
    /// updates the policy, so old callers keep their historical behaviour.
    /// </summary>
    public bool Overwrite
    {
        get => _overwritePolicy == DumpOverwritePolicy.Overwrite;
        set => _overwritePolicy = value ? DumpOverwritePolicy.Overwrite : DumpOverwritePolicy.Rename;
    }

    public int MaxDegreeOfParallelism { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    public PeRebuildOptions ToRebuildOptions() => new()
    {
        TrimPadding = true,
        StripDebugDirectory = StripDebugDirectory,
        StripBoundImports = StripBoundImports,
    };

    public static string DefaultOutputDirectory()
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
            documents = Environment.CurrentDirectory;
        return System.IO.Path.Combine(documents, "OBSIDIAN", "dumps");
    }
}
