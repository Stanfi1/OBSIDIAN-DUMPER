using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Obsidian.Dumper.Core.PE;

public sealed class PeField
{
    public string Group { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
}

public sealed class PeSectionInfo
{
    public string Name { get; init; } = string.Empty;
    public uint VirtualAddress { get; init; }
    public uint VirtualSize { get; init; }
    public uint RawPointer { get; init; }
    public uint RawSize { get; init; }
    public uint Characteristics { get; init; }
    public double Entropy { get; init; }

    public string RvaLabel => "0x" + VirtualAddress.ToString("X8", CultureInfo.InvariantCulture);
    public string VirtualSizeLabel => FormatBytes(VirtualSize);
    public string RawSizeLabel => FormatBytes(RawSize);
    public string EntropyLabel => Entropy.ToString("0.00", CultureInfo.InvariantCulture);

    public string FlagsLabel
    {
        get
        {
            var flags = new List<string>();
            if ((Characteristics & 0x00000020) != 0) flags.Add("code");
            if ((Characteristics & 0x00000040) != 0) flags.Add("init");
            if ((Characteristics & 0x00000080) != 0) flags.Add("uninit");
            if ((Characteristics & 0x02000000) != 0) flags.Add("discard");
            if ((Characteristics & 0x20000000) != 0) flags.Add("exec");
            if ((Characteristics & 0x40000000) != 0) flags.Add("read");
            if ((Characteristics & 0x80000000) != 0) flags.Add("write");
            return flags.Count == 0 ? "—" : string.Join(" ", flags);
        }
    }

    internal static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
            return "0 B";
        if (bytes < 1024)
            return bytes + " B";
        double value = bytes;
        string[] units = ["KB", "MB", "GB"];
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

public sealed class PeDirectoryInfo
{
    public string Name { get; init; } = string.Empty;
    public uint VirtualAddress { get; init; }
    public uint Size { get; init; }

    public string RvaLabel => VirtualAddress == 0 ? "—" : "0x" + VirtualAddress.ToString("X8", CultureInfo.InvariantCulture);
    public string SizeLabel => Size == 0 ? "—" : PeSectionInfo.FormatBytes(Size);
}

public sealed class PeHeaderInfo
{
    public bool IsValid { get; init; }
    public string? Error { get; init; }

    public string MachineLabel { get; init; } = "—";
    public string KindLabel { get; init; } = "—";
    public string SubsystemLabel { get; init; } = "—";
    public string CharacteristicsLabel { get; init; } = "—";
    public string DllCharacteristicsLabel { get; init; } = "—";
    public string TimestampLabel { get; init; } = "—";
    public string ImageBaseLabel { get; init; } = "—";
    public string EntryPointLabel { get; init; } = "—";
    public string SizeOfImageLabel { get; init; } = "—";
    public string SizeOfHeadersLabel { get; init; } = "—";
    public string ChecksumLabel { get; init; } = "—";
    public string? ClrVersion { get; init; }
    public bool IsManaged => ClrVersion != null;

    public IReadOnlyList<PeField> Fields { get; init; } = Array.Empty<PeField>();
    public IReadOnlyList<PeSectionInfo> Sections { get; init; } = Array.Empty<PeSectionInfo>();
    public IReadOnlyList<PeDirectoryInfo> Directories { get; init; } = Array.Empty<PeDirectoryInfo>();

    public string SummaryLabel => IsValid
        ? $"{KindLabel} · {MachineLabel} · {SubsystemLabel} · {Sections.Count} sections"
        : Error ?? "Not a PE image";
}

/// <summary>
/// Bounds-checked PE header reader used by the Inspector's PE tab. It never
/// throws: malformed input returns <see cref="PeHeaderInfo.IsValid"/> = false
/// with a readable error.
/// </summary>
public static class PeHeaderReader
{
    private static readonly string[] DirectoryNames =
    [
        "Export", "Import", "Resource", "Exception", "Certificate", "Base reloc",
        "Debug", "Architecture", "Global ptr", "TLS", "Load config", "Bound import",
        "IAT", "Delay import", "CLR header", "Reserved",
    ];

    public static PeHeaderInfo Read(byte[] data)
    {
        try
        {
            return Parse(data);
        }
        catch (Exception ex)
        {
            return new PeHeaderInfo { IsValid = false, Error = "PE parse failed: " + ex.Message };
        }
    }

    private static PeHeaderInfo Parse(byte[] data)
    {
        if (data.Length < 0x40 || data[0] != 'M' || data[1] != 'Z')
            return new PeHeaderInfo { IsValid = false, Error = "Missing the DOS MZ signature." };

        int peOffset = ReadInt32(data, 0x3C);
        if (peOffset < 0x40 || peOffset + 24 > data.Length)
            return new PeHeaderInfo { IsValid = false, Error = "e_lfanew points outside the file." };

        if (data[peOffset] != 'P' || data[peOffset + 1] != 'E' || data[peOffset + 2] != 0 || data[peOffset + 3] != 0)
            return new PeHeaderInfo { IsValid = false, Error = "Missing the PE signature." };

        int coff = peOffset + 4;
        ushort machine = ReadUInt16(data, coff);
        ushort sectionCount = ReadUInt16(data, coff + 2);
        uint timestamp = ReadUInt32(data, coff + 4);
        ushort optionalSize = ReadUInt16(data, coff + 16);
        ushort characteristics = ReadUInt16(data, coff + 18);
        int optional = coff + 20;

        if (optional + optionalSize > data.Length || optionalSize < 96)
            return new PeHeaderInfo { IsValid = false, Error = "The optional header is truncated." };

        ushort magic = ReadUInt16(data, optional);
        bool pe32Plus = magic == 0x20B;
        if (magic is not (0x10B or 0x20B))
            return new PeHeaderInfo { IsValid = false, Error = $"Unknown optional-header magic 0x{magic:X}." };

        uint entryPoint = ReadUInt32(data, optional + 16);
        ulong imageBase = pe32Plus ? ReadUInt64(data, optional + 24) : ReadUInt32(data, optional + 28);
        uint sectionAlignment = ReadUInt32(data, optional + 32);
        uint fileAlignment = ReadUInt32(data, optional + 36);
        uint sizeOfImage = ReadUInt32(data, optional + 56);
        uint sizeOfHeaders = ReadUInt32(data, optional + 60);
        uint checksum = ReadUInt32(data, optional + 64);
        ushort subsystem = ReadUInt16(data, optional + 68);
        ushort dllCharacteristics = ReadUInt16(data, optional + 70);
        uint directoryCount = ReadUInt32(data, optional + (pe32Plus ? 108 : 92));
        int directoriesOffset = optional + (pe32Plus ? 112 : 96);

        var directories = new List<PeDirectoryInfo>();
        int maxDirectories = Math.Min((int)directoryCount, DirectoryNames.Length);
        for (int i = 0; i < maxDirectories; i++)
        {
            int offset = directoriesOffset + i * 8;
            if (offset + 8 > optional + optionalSize || offset + 8 > data.Length)
                break;
            directories.Add(new PeDirectoryInfo
            {
                Name = DirectoryNames[i],
                VirtualAddress = ReadUInt32(data, offset),
                Size = ReadUInt32(data, offset + 4),
            });
        }

        uint clrRva = directories.Count > 14 ? directories[14].VirtualAddress : 0;
        string? clrVersion = clrRva != 0 ? ReadClrVersion(data, sectionCount, coff, optionalSize, clrRva) : null;

        var sections = new List<PeSectionInfo>(sectionCount);
        int sectionTable = optional + optionalSize;
        for (int i = 0; i < sectionCount; i++)
        {
            int offset = sectionTable + i * 40;
            if (offset + 40 > data.Length)
                break;

            string name = ReadName(data, offset);
            uint virtualSize = ReadUInt32(data, offset + 8);
            uint virtualAddress = ReadUInt32(data, offset + 12);
            uint rawSize = ReadUInt32(data, offset + 16);
            uint rawPointer = ReadUInt32(data, offset + 20);
            uint sectionFlags = ReadUInt32(data, offset + 36);

            double entropy = 0;
            if (rawSize > 0 && rawPointer + (long)rawSize <= data.Length)
                entropy = ShanonEntropy(data, (int)rawPointer, (int)Math.Min(rawSize, 2 * 1024 * 1024));

            sections.Add(new PeSectionInfo
            {
                Name = name,
                VirtualAddress = virtualAddress,
                VirtualSize = virtualSize,
                RawPointer = rawPointer,
                RawSize = rawSize,
                Characteristics = sectionFlags,
                Entropy = entropy,
            });
        }

        var fields = new List<PeField>
        {
            new() { Group = "COFF", Name = "Machine", Value = $"{MachineLabel(machine)} (0x{machine:X4})" },
            new() { Group = "COFF", Name = "Sections", Value = sectionCount.ToString(CultureInfo.InvariantCulture) },
            new() { Group = "COFF", Name = "Characteristics", Value = $"{CharacteristicsLabel(characteristics)} (0x{characteristics:X4})" },
            new() { Group = "COFF", Name = "Timestamp", Value = TimestampLabel(timestamp) },
            new() { Group = "OPTIONAL", Name = "Magic", Value = pe32Plus ? "PE32+ (0x20B)" : "PE32 (0x10B)" },
            new() { Group = "OPTIONAL", Name = "Image base", Value = "0x" + imageBase.ToString("X", CultureInfo.InvariantCulture) },
            new() { Group = "OPTIONAL", Name = "Entry point RVA", Value = "0x" + entryPoint.ToString("X8", CultureInfo.InvariantCulture) },
            new() { Group = "OPTIONAL", Name = "Section alignment", Value = "0x" + sectionAlignment.ToString("X", CultureInfo.InvariantCulture) },
            new() { Group = "OPTIONAL", Name = "File alignment", Value = "0x" + fileAlignment.ToString("X", CultureInfo.InvariantCulture) },
            new() { Group = "OPTIONAL", Name = "Size of image", Value = PeSectionInfo.FormatBytes(sizeOfImage) + $" (0x{sizeOfImage:X})" },
            new() { Group = "OPTIONAL", Name = "Size of headers", Value = PeSectionInfo.FormatBytes(sizeOfHeaders) + $" (0x{sizeOfHeaders:X})" },
            new() { Group = "OPTIONAL", Name = "Checksum", Value = "0x" + checksum.ToString("X8", CultureInfo.InvariantCulture) },
            new() { Group = "OPTIONAL", Name = "Subsystem", Value = SubsystemLabel(subsystem) },
            new() { Group = "OPTIONAL", Name = "DLL characteristics", Value = DllCharacteristicsLabel(dllCharacteristics) },
            new() { Group = "OPTIONAL", Name = "Data directories", Value = directoryCount.ToString(CultureInfo.InvariantCulture) },
            new() { Group = "HEADERS", Name = "DOS header", Value = "MZ at 0x0" },
            new() { Group = "HEADERS", Name = "PE header offset", Value = "0x" + peOffset.ToString("X", CultureInfo.InvariantCulture) },
            new() { Group = "HEADERS", Name = "Section table", Value = "0x" + sectionTable.ToString("X", CultureInfo.InvariantCulture) },
            new() { Group = "HEADERS", Name = "CLR runtime", Value = clrVersion ?? "native" },
        };

        return new PeHeaderInfo
        {
            IsValid = true,
            MachineLabel = MachineLabel(machine),
            KindLabel = pe32Plus ? "PE32+" : "PE32",
            SubsystemLabel = SubsystemLabel(subsystem),
            CharacteristicsLabel = CharacteristicsLabel(characteristics),
            DllCharacteristicsLabel = DllCharacteristicsLabel(dllCharacteristics),
            TimestampLabel = TimestampLabel(timestamp),
            ImageBaseLabel = "0x" + imageBase.ToString("X", CultureInfo.InvariantCulture),
            EntryPointLabel = "0x" + entryPoint.ToString("X8", CultureInfo.InvariantCulture),
            SizeOfImageLabel = PeSectionInfo.FormatBytes(sizeOfImage),
            SizeOfHeadersLabel = PeSectionInfo.FormatBytes(sizeOfHeaders),
            ChecksumLabel = "0x" + checksum.ToString("X8", CultureInfo.InvariantCulture),
            ClrVersion = clrVersion,
            Fields = fields,
            Sections = sections,
            Directories = directories,
        };
    }

    private static string? ReadClrVersion(byte[] data, int sectionCount, int coff, ushort optionalSize, uint clrRva)
    {
        try
        {
            int sectionTable = coff + 20 + optionalSize;
            for (int i = 0; i < sectionCount; i++)
            {
                int offset = sectionTable + i * 40;
                if (offset + 40 > data.Length)
                    return null;

                uint virtualSize = ReadUInt32(data, offset + 8);
                uint virtualAddress = ReadUInt32(data, offset + 12);
                uint rawSize = ReadUInt32(data, offset + 16);
                uint rawPointer = ReadUInt32(data, offset + 20);
                uint span = Math.Max(virtualSize, rawSize);
                if (clrRva < virtualAddress || clrRva >= virtualAddress + span)
                    continue;

                long fileOffset = rawPointer + (clrRva - virtualAddress);
                if (fileOffset + 72 > data.Length)
                    return null;

                int metaOffset = (int)fileOffset + 8;
                uint metadataRva = ReadUInt32(data, metaOffset);
                uint metadataSize = ReadUInt32(data, metaOffset + 4);
                return metadataRva == 0 ? "CLR header (no metadata)" : "metadata " + PeSectionInfo.FormatBytes(metadataSize);
            }
        }
        catch
        {
            // A malformed CLR directory is not fatal for the header view.
        }

        return "CLR directory present";
    }

    private static string ReadName(byte[] data, int offset)
    {
        Span<byte> name = stackalloc byte[8];
        for (int i = 0; i < 8; i++)
            name[i] = data[offset + i];
        int length = name.IndexOf((byte)0);
        if (length < 0)
            length = 8;
        return Encoding.ASCII.GetString(name[..length]);
    }

    private static double ShanonEntropy(byte[] data, int offset, int length)
    {
        if (length <= 0)
            return 0;

        Span<int> counts = stackalloc int[256];
        for (int i = 0; i < length; i++)
            counts[data[offset + i]]++;

        double entropy = 0;
        for (int i = 0; i < 256; i++)
        {
            if (counts[i] == 0)
                continue;
            double probability = counts[i] / (double)length;
            entropy -= probability * Math.Log2(probability);
        }

        return entropy;
    }

    private static string MachineLabel(ushort machine) => machine switch
    {
        0x014C => "x86",
        0x8664 => "x64",
        0xAA64 => "ARM64",
        0x01C0 => "ARM",
        0x01C4 => "ARMv7",
        0x0200 => "IA64",
        _ => $"unknown (0x{machine:X4})",
    };

    private static string SubsystemLabel(ushort subsystem) => subsystem switch
    {
        1 => "native",
        2 => "Windows GUI",
        3 => "Windows CUI",
        5 => "OS/2 CUI",
        7 => "POSIX CUI",
        9 => "Windows CE",
        10 => "EFI application",
        12 => "EFI runtime",
        _ => $"unknown ({subsystem})",
    };

    private static string TimestampLabel(uint seconds)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch
        {
            return "0x" + seconds.ToString("X8", CultureInfo.InvariantCulture);
        }
    }

    private static string CharacteristicsLabel(ushort characteristics)
    {
        var flags = new List<string>();
        if ((characteristics & 0x0002) != 0) flags.Add("executable");
        if ((characteristics & 0x2000) != 0) flags.Add("dll");
        if ((characteristics & 0x0020) != 0) flags.Add("large-address");
        if ((characteristics & 0x0100) != 0) flags.Add("32-bit");
        if ((characteristics & 0x0001) != 0) flags.Add("relocs-stripped");
        return flags.Count == 0 ? "—" : string.Join(" ", flags);
    }

    private static string DllCharacteristicsLabel(ushort characteristics)
    {
        var flags = new List<string>();
        if ((characteristics & 0x0020) != 0) flags.Add("high-entropy-va");
        if ((characteristics & 0x0040) != 0) flags.Add("dynamic-base");
        if ((characteristics & 0x0100) != 0) flags.Add("nx-compat");
        if ((characteristics & 0x0200) != 0) flags.Add("no-isolation");
        if ((characteristics & 0x0400) != 0) flags.Add("no-seh");
        if ((characteristics & 0x0800) != 0) flags.Add("no-bind");
        if ((characteristics & 0x1000) != 0) flags.Add("app-container");
        if ((characteristics & 0x2000) != 0) flags.Add("wdm-driver");
        if ((characteristics & 0x8000) != 0) flags.Add("terminal-server");
        return flags.Count == 0 ? "—" : string.Join(" ", flags);
    }

    private static ushort ReadUInt16(byte[] data, int offset)
        => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));

    private static uint ReadUInt32(byte[] data, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

    private static int ReadInt32(byte[] data, int offset)
        => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));

    private static ulong ReadUInt64(byte[] data, int offset)
        => BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8));
}
