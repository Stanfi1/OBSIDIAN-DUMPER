#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace Obsidian.Dumper.Shared;

/// <summary>
/// PE structures shared by the dumper engine and the injected payload.
/// The payload runs inside foreign CLRs (.NET Framework 2.0-4.8 and
/// .NET Core / .NET 5+), therefore this file is limited to C# language
/// features and BCL APIs that exist on net48 and netstandard2.0:
/// no Span, no System.Buffers, no records, no init-only setters.
/// </summary>
public static class PeImageLayout
{
    public const ushort DosSignature = 0x5A4D;      // "MZ"
    public const uint NtSignature = 0x00004550;     // "PE\0\0"
    public const ushort OptionalMagic32 = 0x010B;   // PE32
    public const ushort OptionalMagic64 = 0x020B;   // PE32+

    public const int SectionHeaderSize = 40;
    public const uint MetadataSignature = 0x424A5342; // "BSJB"

    /// <summary>Read the essential header facts of a mapped image.</summary>
    public static bool TryReadHeaders(byte[] image, out PeHeaders headers)
    {
        headers = default(PeHeaders);
        if (image == null || image.Length < 0x40)
            return false;

        if (ReadU16(image, 0) != DosSignature)
            return false;

        int peOffset = ReadI32(image, 0x3C);
        // Long arithmetic: a corrupt e_lfanew near int.MaxValue would otherwise
        // wrap peOffset + 24 and pass a naive int bounds check.
        if (peOffset < 0x40 || (long)peOffset + 24 > image.Length)
            return false;

        if (ReadU32(image, peOffset) != NtSignature)
            return false;

        ushort machine = ReadU16(image, peOffset + 4);
        int sectionCount = ReadU16(image, peOffset + 6);
        int sizeOfOptionalHeader = ReadU16(image, peOffset + 20);
        int optionalOffset = peOffset + 24;

        if (optionalOffset + 2 > image.Length)
            return false;

        ushort magic = ReadU16(image, optionalOffset);
        bool is64 = magic == OptionalMagic64;
        if (!is64 && magic != OptionalMagic32)
            return false;

        // The optional header has to reach the data directories (NumberOfRvaAndSizes
        // at 92/108, first directory at 96/112); anything shorter is truncated.
        int minimumOptionalHeader = is64 ? 112 : 96;
        if (sizeOfOptionalHeader < minimumOptionalHeader || optionalOffset + sizeOfOptionalHeader > image.Length)
            return false;
        if (sectionCount <= 0 || sectionCount > 96)
            return false;

        long sectionTableOffset64 = (long)optionalOffset + sizeOfOptionalHeader;
        if (sectionTableOffset64 + (long)sectionCount * SectionHeaderSize > image.Length)
            return false;
        int sectionTableOffset = (int)sectionTableOffset64;

        var result = new PeHeaders();
        result.PeOffset = peOffset;
        result.Machine = machine;
        result.Is64Bit = is64;
        result.SectionCount = sectionCount;
        result.SizeOfOptionalHeader = sizeOfOptionalHeader;
        result.OptionalHeaderOffset = optionalOffset;
        result.SectionTableOffset = sectionTableOffset;
        result.EntryPointRva = ReadU32(image, optionalOffset + 16);
        ulong imageBase = is64
            ? ReadU64(image, optionalOffset + 24)
            : ReadU32(image, optionalOffset + 28);
        result.ImageBase64 = imageBase;
        result.ImageBase = (uint)imageBase; // legacy 32-bit view; use ImageBase64 for x64 images
        result.SectionAlignment = ReadU32(image, optionalOffset + 32);
        result.FileAlignment = ReadU32(image, optionalOffset + 36);
        result.SizeOfImage = ReadU32(image, optionalOffset + 56);
        result.SizeOfHeaders = ReadU32(image, optionalOffset + 60);
        result.Subsystem = ReadU16(image, optionalOffset + 68);
        // IMAGE_FILE_HEADER.Characteristics is the last field of the COFF header.
        // The COFF header starts after the 4-byte "PE\0\0" signature, so the
        // field lives at peOffset + 4 + 18 (NOT in the optional header, where
        // optionalOffset + 18 is MajorSubsystemVersion).
        result.Characteristics = ReadU16(image, peOffset + 22);
        result.DllCharacteristics = ReadU16(image, optionalOffset + 70);

        int directoriesOffset = optionalOffset + (is64 ? 112 : 96);
        uint declaredDirectories = ReadU32(image, optionalOffset + (is64 ? 108 : 92));
        int maxDirectories = (sizeOfOptionalHeader - (is64 ? 112 : 96)) / 8;
        int directoryCount = (int)Math.Min(Math.Min(declaredDirectories, 16u), (uint)Math.Max(0, maxDirectories));
        if (directoryCount > 14 && directoriesOffset + 15 * 8 <= image.Length)
        {
            result.ClrDirectoryRva = ReadU32(image, directoriesOffset + 14 * 8);
            result.ClrDirectorySize = ReadU32(image, directoriesOffset + 14 * 8 + 4);
        }

        var sections = new PeSection[sectionCount];
        for (int i = 0; i < sectionCount; i++)
        {
            int off = sectionTableOffset + i * SectionHeaderSize;
            var section = new PeSection();
            section.Name = ReadSectionName(image, off);
            section.VirtualSize = ReadU32(image, off + 8);
            section.VirtualAddress = ReadU32(image, off + 12);
            section.RawSize = ReadU32(image, off + 16);
            section.RawPointer = ReadU32(image, off + 20);
            section.Characteristics = ReadU32(image, off + 36);
            sections[i] = section;
        }

        result.Sections = sections;
        headers = result;
        return true;
    }

    private static string ReadSectionName(byte[] image, int offset)
    {
        int length = 0;
        while (length < 8 && image[offset + length] != 0)
            length++;
        var buffer = new char[length];
        for (int i = 0; i < length; i++)
            buffer[i] = (char)image[offset + i];
        return new string(buffer);
    }

    /// <summary>
    /// Map an RVA of a *mapped* image to a buffer offset. The loader places each
    /// section at its RVA, so every mapped RVA is also its buffer offset; this
    /// method only validates that the RVA belongs to the image.
    /// </summary>
    public static int MapRva(PeHeaders headers, uint rva)
    {
        if (headers.Sections == null)
            return rva < headers.SizeOfHeaders ? (int)rva : -1;

        if (rva < 0x1000 || rva < headers.SizeOfHeaders)
            return rva <= MaxHeaderRva(headers) ? (int)rva : -1;

        for (int i = 0; i < headers.Sections.Length; i++)
        {
            var section = headers.Sections[i];
            uint extent = Math.Max(section.VirtualSize, section.RawSize);
            if (extent == 0)
                continue;
            // Subtraction form avoids uint overflow when VirtualAddress + extent wraps.
            if (rva >= section.VirtualAddress && rva - section.VirtualAddress < extent)
                return (int)rva;
        }

        return -1;
    }

    private static uint MaxHeaderRva(PeHeaders headers)
    {
        uint limit = headers.SizeOfHeaders;
        if (limit == 0)
            limit = (uint)(headers.SectionTableOffset + headers.SectionCount * SectionHeaderSize);
        return limit;
    }

    /// <summary>
    /// Convert an RVA to an offset in the *file* representation, where section
    /// content lives at PointerToRawData instead of its virtual address.
    /// </summary>
    public static int RvaToFileOffset(PeHeaders headers, uint rva)
    {
        if (headers.Sections == null)
            return rva < headers.SizeOfHeaders ? (int)rva : -1;

        if (rva < headers.SizeOfHeaders)
            return (int)rva;

        for (int i = 0; i < headers.Sections.Length; i++)
        {
            var section = headers.Sections[i];
            uint extent = Math.Max(section.VirtualSize, section.RawSize);
            if (extent == 0 || section.RawSize == 0)
                continue;
            if (rva < section.VirtualAddress || rva - section.VirtualAddress >= extent)
                continue;

            uint delta = rva - section.VirtualAddress;
            if (delta >= section.RawSize)
                return -1;

            ulong offset = (ulong)section.RawPointer + delta;
            return offset > int.MaxValue ? -1 : (int)offset;
        }

        return -1;
    }

    public static int MapRva(byte[] image, uint rva)
    {
        PeHeaders headers;
        if (!TryReadHeaders(image, out headers))
            return -1;
        return MapRva(headers, rva);
    }

    /// <summary>Locate the CLI (COR20) header inside a mapped image.</summary>
    public static bool TryGetCliHeader(byte[] image, out CliHeader cli)
        => TryGetCliHeader(image, mapped: true, out cli);

    /// <summary>Locate the CLI (COR20) header in either a mapped or a file image.</summary>
    public static bool TryGetCliHeader(byte[] image, bool mapped, out CliHeader cli)
    {
        cli = default(CliHeader);
        PeHeaders headers;
        if (!TryReadHeaders(image, out headers) || headers.ClrDirectoryRva == 0)
            return false;

        int offset = mapped
            ? MapRva(headers, headers.ClrDirectoryRva)
            : RvaToFileOffset(headers, headers.ClrDirectoryRva);

        if (offset < 0 || (long)offset + 72 > image.Length)
            return false;

        uint cb = ReadU32(image, offset);
        if (cb < 72)
            return false;

        cli = new CliHeader();
        cli.Offset = offset;
        cli.MajorRuntimeVersion = ReadU16(image, offset + 4);
        cli.MinorRuntimeVersion = ReadU16(image, offset + 6);
        cli.MetadataRva = ReadU32(image, offset + 8);
        cli.MetadataSize = ReadU32(image, offset + 12);
        cli.Flags = ReadU32(image, offset + 16);
        cli.EntryPointToken = ReadU32(image, offset + 20);
        cli.ResourcesRva = ReadU32(image, offset + 24);
        cli.ResourcesSize = ReadU32(image, offset + 28);
        cli.StrongNameSignatureRva = ReadU32(image, offset + 32);
        cli.StrongNameSignatureSize = ReadU32(image, offset + 36);

        return cli.MetadataRva != 0 && cli.MetadataSize >= 16;
    }

    /// <summary>Read the metadata root (BSJB) of a mapped image.</summary>
    public static bool TryGetMetadataRoot(byte[] image, out MetadataRoot root, out int rootOffset)
        => TryGetMetadataRoot(image, mapped: true, out root, out rootOffset);

    /// <summary>Read the metadata root (BSJB) of an on-disk file image.</summary>
    public static bool TryGetMetadataRootFromFile(byte[] image, out MetadataRoot root, out int rootOffset)
        => TryGetMetadataRoot(image, mapped: false, out root, out rootOffset);

    public static bool TryGetMetadataRoot(byte[] image, bool mapped, out MetadataRoot root, out int rootOffset)
    {
        root = default(MetadataRoot);
        rootOffset = -1;

        CliHeader cli;
        if (!TryGetCliHeader(image, mapped, out cli))
            return false;

        PeHeaders headers;
        if (!TryReadHeaders(image, out headers))
            return false;

        int offset = mapped ? MapRva(headers, cli.MetadataRva) : RvaToFileOffset(headers, cli.MetadataRva);
        if (offset < 0 || (long)offset + 20 > image.Length)
            return false;

        if (!TryReadMetadataRoot(image, offset, out root))
            return false;

        rootOffset = offset;
        return true;
    }

    public static bool TryReadMetadataRoot(byte[] image, int offset, out MetadataRoot root)
    {
        root = default(MetadataRoot);
        if (image == null || offset < 0 || (long)offset + 20 > image.Length)
            return false;
        if (ReadU32(image, offset) != MetadataSignature)
            return false;

        ushort major = ReadU16(image, offset + 4);
        ushort minor = ReadU16(image, offset + 6);
        int versionLength = ReadI32(image, offset + 12);
        if (versionLength < 0 || versionLength > 512 || (long)offset + 16 + versionLength + 4 > image.Length)
            return false;

        string version = Encoding.UTF8.GetString(image, offset + 16, versionLength).TrimEnd('\0');

        // VersionString is written by the compiler already padded to 4 bytes, but
        // some generators are sloppy; accept both layouts by scanning for a
        // plausible stream table.
        int cursor = offset + 16 + versionLength;
        int tableStart = AlignUp4(cursor);
        if (!LooksLikeStreamTable(image, tableStart, out int streamCount) &&
            !LooksLikeStreamTable(image, cursor, out streamCount))
            return false;
        if (!LooksLikeStreamTable(image, tableStart, out _))
            tableStart = cursor;
        if (!LooksLikeStreamTable(image, tableStart, out streamCount))
            return false;

        int flags = ReadU16(image, tableStart);
        int position = tableStart + 4;
        var streams = new List<MetadataStream>(streamCount);

        for (int i = 0; i < streamCount; i++)
        {
            if (position + 8 > image.Length)
                return false;

            var stream = new MetadataStream();
            stream.Offset = ReadI32(image, position);
            stream.Size = ReadI32(image, position + 4);
            position += 8;

            int nameStart = position;
            int nameEnd = nameStart;
            while (nameEnd < image.Length && nameEnd - nameStart <= 32 && image[nameEnd] != 0)
                nameEnd++;
            if (nameEnd >= image.Length || nameEnd - nameStart > 32)
                return false;

            stream.Name = Encoding.ASCII.GetString(image, nameStart, nameEnd - nameStart);
            position = AlignUp4(nameEnd + 1);

            if (stream.Offset < 0 || stream.Size < 0)
                return false;

            streams.Add(stream);
        }

        root = new MetadataRoot();
        root.Offset = offset;
        root.MajorVersion = major;
        root.MinorVersion = minor;
        root.RuntimeVersion = version;
        root.Flags = flags;
        root.Streams = streams.ToArray();
        return true;
    }

    private static bool LooksLikeStreamTable(byte[] image, int offset, out int streamCount)
    {
        streamCount = 0;
        if (offset < 0 || offset + 4 > image.Length)
            return false;
        int count = ReadU16(image, offset + 2);
        if (count <= 0 || count > 8)
            return false;
        // First stream entry must be a sane offset/size pair followed by a name.
        int position = offset + 4 + 8;
        if (position >= image.Length)
            return false;
        int firstOffset = ReadI32(image, offset + 4);
        int firstSize = ReadI32(image, offset + 8);
        if (firstOffset < 16 || firstSize < 0)
            return false;
        streamCount = count;
        return true;
    }

    private static int AlignUp4(int value) => (value + 3) & ~3;

    /// <summary>True when the mapped image carries CLI metadata (any .NET module).</summary>
    public static bool IsManaged(byte[] image)
    {
        MetadataRoot root;
        int offset;
        return TryGetMetadataRoot(image, out root, out offset);
    }

    /// <summary>Cheap probe: does this buffer start with a plausible PE image?</summary>
    public static bool LooksLikeImage(byte[] buffer, int offset)
    {
        if (buffer == null || offset < 0 || offset + 0x40 > buffer.Length)
            return false;
        if (ReadU16(buffer, offset) != DosSignature)
            return false;
        int peOffset = ReadI32(buffer, offset + 0x3C);
        if (peOffset < 0x40 || peOffset > 0x1000)
            return false;
        if (offset + peOffset + 24 > buffer.Length)
            return false;
        return ReadU32(buffer, offset + peOffset) == NtSignature;
    }

    /// <summary>
    /// Rebuild a valid on-disk PE file from a mapped image. Section content is
    /// re-serialised on FileAlignment; virtual addresses are preserved, so every
    /// RVA (metadata, imports, entry point) remains valid.
    /// </summary>
    public static byte[]? Rebuild(byte[] mapped, PeRebuildOptions options) => Rebuild(mapped, options, out _);

    /// <summary>Rebuild with a human readable failure reason for diagnostics.</summary>
    public static byte[]? Rebuild(byte[] mapped, PeRebuildOptions options, out string? error)
    {
        error = null;
        if (options == null)
            options = new PeRebuildOptions();

        PeHeaders headers;
        if (!TryReadHeaders(mapped, out headers))
        {
            error = "invalid PE headers";
            return null;
        }

        if (headers.SizeOfImage == 0 || headers.SizeOfImage > 512u * 1024 * 1024)
        {
            error = "implausible SizeOfImage";
            return null;
        }

        if ((long)mapped.Length < headers.SizeOfImage)
        {
            error = "mapped image is truncated";
            return null;
        }

        uint fileAlignment = headers.FileAlignment;
        if (fileAlignment < 0x200 || fileAlignment > 0x10000 || (fileAlignment & (fileAlignment - 1)) != 0)
            fileAlignment = 0x200;

        uint minimumHeaders = (uint)(headers.SectionTableOffset + headers.SectionCount * SectionHeaderSize);
        uint rawHeadersSize = Math.Max(headers.SizeOfHeaders, minimumHeaders);
        if (rawHeadersSize > 0x100000 || rawHeadersSize > headers.SizeOfImage)
        {
            error = "header size out of range";
            return null;
        }

        // Saturating Align: a corrupt SizeOfHeaders near uint.MaxValue would wrap
        // to a tiny value and place section data on top of the headers.
        uint headersSize = Align(rawHeadersSize, fileAlignment);
        if (headersSize > 0x100000 || headersSize > headers.SizeOfImage)
        {
            error = "aligned header size out of range";
            return null;
        }

        if ((long)headersSize > mapped.Length)
        {
            error = "the mapped image is smaller than its header region";
            return null;
        }

        int rawCursor = (int)headersSize;
        var rawPointers = new uint[headers.SectionCount];
        var rawSizes = new uint[headers.SectionCount];
        long totalSize = headersSize;

        for (int i = 0; i < headers.SectionCount; i++)
        {
            var section = headers.Sections[i];
            uint virtualSize = section.VirtualSize == 0 ? section.RawSize : section.VirtualSize;
            if (section.Characteristics == 0 || virtualSize == 0)
                continue;

            uint rawSize = options.TrimPadding
                ? Align(virtualSize, fileAlignment)
                : Align(Math.Max(virtualSize, section.RawSize), fileAlignment);

            // Saturating Align returns uint.MaxValue for absurd inputs; reject
            // instead of letting the raw cursor / file size wrap.
            if (rawSize == 0 || rawSize == uint.MaxValue || rawSize > 512u * 1024 * 1024)
            {
                error = "section '" + section.Name + "' size out of range";
                return null;
            }

            if ((long)rawCursor + rawSize > int.MaxValue - 0x1000)
            {
                error = "rebuilt image is too large";
                return null;
            }

            rawPointers[i] = (uint)rawCursor;
            rawSizes[i] = rawSize;
            rawCursor += (int)rawSize;
            totalSize = rawCursor;
        }

        if (totalSize <= 0 || totalSize > (long)int.MaxValue - 0x1000)
        {
            error = "rebuilt image is too large";
            return null;
        }

        var file = new byte[Align((uint)totalSize, 0x10)];
        Buffer.BlockCopy(mapped, 0, file, 0, (int)headersSize);

        for (int i = 0; i < headers.SectionCount; i++)
        {
            int headerOffset = headers.SectionTableOffset + i * SectionHeaderSize;
            if (rawSizes[i] == 0)
            {
                // The section is not materialised in the rebuilt file: zero its raw
                // fields instead of leaving the mapped-image values in the header.
                if (headerOffset + SectionHeaderSize <= file.Length)
                {
                    WriteU32(file, headerOffset + 16, 0);
                    WriteU32(file, headerOffset + 20, 0);
                }
                continue;
            }

            var section = headers.Sections[i];
            long sourceOffset = section.VirtualAddress;
            if (sourceOffset < mapped.Length)
            {
                long available = Math.Min((long)rawSizes[i], mapped.Length - sourceOffset);
                if (available > 0)
                    Buffer.BlockCopy(mapped, (int)sourceOffset, file, (int)rawPointers[i], (int)available);
            }

            WriteU32(file, headerOffset + 16, rawSizes[i]);
            WriteU32(file, headerOffset + 20, rawPointers[i]);
        }

        int optional = headers.OptionalHeaderOffset;
        WriteU32(file, optional + 64, 0);              // CheckSum: stale after the rewrite
        WriteU32(file, optional + 36, fileAlignment);  // FileAlignment
        WriteU32(file, optional + 60, headersSize);    // SizeOfHeaders

        if (options.StripDebugDirectory)
            ClearDataDirectory(file, headers, 6);      // IMAGE_DIRECTORY_ENTRY_DEBUG
        if (options.StripBoundImports)
            ClearDataDirectory(file, headers, 11);     // IMAGE_DIRECTORY_ENTRY_BOUND_IMPORT

        return file;
    }

    private static void ClearDataDirectory(byte[] file, PeHeaders headers, int index)
    {
        int offset = headers.OptionalHeaderOffset + (headers.Is64Bit ? 112 : 96) + index * 8;
        if (offset + 8 > file.Length)
            return;
        WriteU32(file, offset, 0);
        WriteU32(file, offset + 4, 0);
    }

    public static uint Align(uint value, uint alignment)
    {
        if (alignment <= 1)
            return value;
        ulong aligned = ((ulong)value + alignment - 1) & ~((ulong)alignment - 1);
        return aligned > uint.MaxValue ? uint.MaxValue : (uint)aligned;
    }

    /// <summary>Best-effort name of the section that owns an RVA.</summary>
    public static string SectionOf(PeHeaders headers, uint rva)
    {
        if (headers.Sections == null)
            return "headers";
        for (int i = 0; i < headers.Sections.Length; i++)
        {
            var section = headers.Sections[i];
            uint extent = Math.Max(section.VirtualSize, section.RawSize);
            if (extent > 0 && rva >= section.VirtualAddress && rva - section.VirtualAddress < extent)
                return section.Name;
        }
        return "headers";
    }

    public static PeArchitecture MachineToArchitecture(ushort machine)
    {
        switch (machine)
        {
            case 0x014C: return PeArchitecture.X86;
            case 0x8664: return PeArchitecture.X64;
            case 0xAA64: return PeArchitecture.Arm64;
            case 0x01C4: return PeArchitecture.Arm;
            default: return PeArchitecture.Unknown;
        }
    }

    public static string DescribeMachine(ushort machine)
    {
        switch (machine)
        {
            case 0x014C: return "x86";
            case 0x8664: return "x64";
            case 0xAA64: return "ARM64";
            case 0x01C4: return "ARM";
            case 0x0200: return "IA64";
            default: return "0x" + machine.ToString("X4");
        }
    }

    // ---- little-endian primitives (the BCL ones are not available on net48) ----

    public static ushort ReadU16(byte[] buffer, int offset)
        => (ushort)(buffer[offset] | (buffer[offset + 1] << 8));

    public static int ReadI32(byte[] buffer, int offset)
        => buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24);

    public static uint ReadU32(byte[] buffer, int offset)
        => (uint)(buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));

    public static ulong ReadU64(byte[] buffer, int offset)
        => ReadU32(buffer, offset) | ((ulong)ReadU32(buffer, offset + 4) << 32);

    public static void WriteU16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }

    public static void WriteU32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    public static void WriteU64(byte[] buffer, int offset, ulong value)
    {
        WriteU32(buffer, offset, (uint)value);
        WriteU32(buffer, offset + 4, (uint)(value >> 32));
    }
}

public struct PeSection
{
    public string Name;
    public uint VirtualAddress;
    public uint VirtualSize;
    public uint RawPointer;
    public uint RawSize;
    public uint Characteristics;

    public bool IsExecutable => (Characteristics & 0x20000000) != 0;
    public bool IsInitializedData => (Characteristics & 0x00000040) != 0;
    public bool IsDiscardable => (Characteristics & 0x02000000) != 0;
    public bool IsReadable => (Characteristics & 0x40000000) != 0;
}

public struct PeHeaders
{
    public int PeOffset;
    public ushort Machine;
    public bool Is64Bit;
    public int SectionCount;
    public int SizeOfOptionalHeader;
    public int OptionalHeaderOffset;
    public int SectionTableOffset;
    /// <summary>Low 32 bits of the image base (kept for compatibility).</summary>
    public uint ImageBase;
    /// <summary>Full image base; correct for x64/ARM64 images (ImageBase truncates them).</summary>
    public ulong ImageBase64;
    public uint EntryPointRva;
    public uint SectionAlignment;
    public uint FileAlignment;
    public uint SizeOfImage;
    public uint SizeOfHeaders;
    public ushort Subsystem;
    public ushort Characteristics;
    public ushort DllCharacteristics;
    public uint ClrDirectoryRva;
    public uint ClrDirectorySize;
    public PeSection[] Sections;
}

/// <summary>Bitness classification of a PE machine type (independent of the Core process enums).</summary>
public enum PeArchitecture : ushort
{
    X86 = 0,
    X64 = 1,
    Arm64 = 2,
    Arm = 3,
    Unknown = 255,
}

public struct CliHeader
{
    public int Offset;
    public ushort MajorRuntimeVersion;
    public ushort MinorRuntimeVersion;
    public uint MetadataRva;
    public uint MetadataSize;
    public uint Flags;
    public uint EntryPointToken;
    public uint ResourcesRva;
    public uint ResourcesSize;
    public uint StrongNameSignatureRva;
    public uint StrongNameSignatureSize;

    public bool IsIlOnly => (Flags & 0x1) != 0;
    public bool Is32BitRequired => (Flags & 0x2) != 0;
    public bool IsStrongNameSigned => (Flags & 0x8) != 0 && StrongNameSignatureRva != 0;
}

public struct MetadataStream
{
    public string Name;
    public int Offset;
    public int Size;
}

public struct MetadataRoot
{
    public int Offset;
    public ushort MajorVersion;
    public ushort MinorVersion;
    public string RuntimeVersion;
    public int Flags;
    public MetadataStream[] Streams;

    public bool HasStream(string name)
    {
        for (int i = 0; i < Streams.Length; i++)
            if (string.Equals(Streams[i].Name, name, StringComparison.Ordinal))
                return true;
        return false;
    }
}

public sealed class PeRebuildOptions
{
    /// <summary>Shrink raw section data to VirtualSize instead of preserving file slack.</summary>
    public bool TrimPadding { get; set; } = true;

    /// <summary>Drop the debug directory so the rebuilt image does not carry stale RSDS paths.</summary>
    public bool StripDebugDirectory { get; set; } = true;

    /// <summary>Drop bound-import metadata that no longer matches the rebuilt file.</summary>
    public bool StripBoundImports { get; set; } = true;

    public static PeRebuildOptions Default
    {
        get { return new PeRebuildOptions(); }
    }
}
