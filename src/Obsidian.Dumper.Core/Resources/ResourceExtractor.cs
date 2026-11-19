using System.Text;
using dnlib.DotNet;

namespace Obsidian.Dumper.Core.Resources;

/// <summary>One extracted managed resource, ready for preview or saving.</summary>
public sealed class ResourceContent
{
    public string Name { get; init; } = string.Empty;
    public string Kind { get; init; } = "embedded";
    public long Size { get; init; }
    public byte[] Data { get; init; } = Array.Empty<byte>();
    public bool IsText { get; init; }
    public bool IsImage { get; init; }
    public string? ImageFormat { get; init; }
    public string? TextPreview { get; init; }
    public string? HexPreview { get; init; }
    public string? Note { get; init; }

    public string SizeLabel => Size switch
    {
        < 0 => "—",
        < 1024 => Size + " B",
        < 1024 * 1024 => (Size / 1024.0).ToString("0.##") + " KB",
        _ => (Size / (1024.0 * 1024)).ToString("0.##") + " MB",
    };
}

/// <summary>
/// Extracts embedded managed resources from an assembly. Reading is defensive:
/// linked resources have no payload and malformed resources return a note instead
/// of throwing.
/// </summary>
public static class ResourceExtractor
{
    private const int MaxPayloadBytes = 64 * 1024 * 1024;
    private const int MaxTextPreview = 128 * 1024;

    /// <summary>Upper bound for an image preview payload; larger resources are shown as hex.</summary>
    private const int MaxPreviewImageBytes = 32 * 1024 * 1024;

    /// <summary>
    /// Upper bound for decoded image dimensions. The byte payload limit alone is
    /// not enough: a small crafted PNG/BMP can declare gigapixel dimensions and
    /// force a multi-gigabyte bitmap allocation in the Inspector.
    /// </summary>
    private const int MaxPreviewImageDimension = 8192;

    private const long MaxPreviewImagePixels = 8192L * 8192L;

    public static ResourceContent? ReadOne(string assemblyPath, string resourceName)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            return null;

        try
        {
            using ModuleDefMD module = ModuleDefMD.Load(assemblyPath);
            Resource? resource = module.Resources.FirstOrDefault(r =>
                string.Equals(r.Name?.String, resourceName, StringComparison.Ordinal));
            if (resource == null)
                return null;

            if (resource is EmbeddedResource embedded)
            {
                byte[] data = embedded.CreateReader().ToArray();
                if (data.Length > MaxPayloadBytes)
                {
                    return new ResourceContent
                    {
                        Name = resource.Name?.String ?? resourceName,
                        Kind = "embedded",
                        Size = data.LongLength,
                        Note = "Resource is larger than the 64 MB preview limit.",
                    };
                }

                return Describe(resource, data);
            }

            string kind = resource switch
            {
                AssemblyLinkedResource => "assembly-linked",
                LinkedResource linked => "linked: " + (linked.File?.Name.ToString() ?? "?"),
                _ => "linked",
            };

            return new ResourceContent
            {
                Name = resource.Name?.String ?? resourceName,
                Kind = kind,
                Size = 0,
                Note = "This resource is a link, not embedded data. OBSIDIAN only extracts embedded payloads.",
            };
        }
        catch (Exception ex)
        {
            return new ResourceContent
            {
                Name = resourceName,
                Kind = "error",
                Note = "Resource could not be read: " + ex.Message,
            };
        }
    }

    private static ResourceContent Describe(Resource resource, byte[] data)
    {
        string name = resource.Name?.String ?? string.Empty;
        (bool isImage, string? format) = DetectImage(data);
        string? note = null;
        // Refuse unsafe image previews before WPF gets to decode them.
        if (isImage && !IsPreviewableImage(data, out note))
            isImage = false;

        string? text = null;
        bool isText = !isImage && TryDecodeText(data, out text);
        string? hex = isText || isImage ? null : HexPreview(data, 2048);
        if (isText && data.LongLength > MaxTextPreview)
            note = $"Preview truncated to {MaxTextPreview / 1024} KB.";

        return new ResourceContent
        {
            Name = name,
            Kind = "embedded",
            Size = data.LongLength,
            Data = data,
            IsImage = isImage,
            ImageFormat = format,
            IsText = isText,
            TextPreview = isText ? Truncate(text ?? string.Empty, MaxTextPreview) : null,
            HexPreview = hex,
            Note = note,
        };
    }

    /// <summary>
    /// Guards the Inspector image preview against "decompression bombs" embedded
    /// in untrusted assemblies. Returns false and a human readable reason when the
    /// payload or its declared pixel dimensions exceed the preview limits.
    /// </summary>
    private static bool IsPreviewableImage(byte[] data, out string? note)
    {
        note = null;
        if (data.LongLength > MaxPreviewImageBytes)
        {
            note = $"Image preview skipped: the payload is larger than {MaxPreviewImageBytes / (1024 * 1024)} MB.";
            return false;
        }

        if (!TryGetImageDimensions(data, out int width, out int height) || width <= 0 || height <= 0)
            return true; // Unknown header: keep the historical decoder behaviour.

        if (width > MaxPreviewImageDimension || height > MaxPreviewImageDimension ||
            (long)width * height > MaxPreviewImagePixels)
        {
            note = $"Image preview skipped: {width}×{height} px exceeds the " +
                   $"{MaxPreviewImageDimension}×{MaxPreviewImageDimension} px preview limit.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads the pixel dimensions from the header of the image formats the
    /// Inspector previews (PNG, JPEG, GIF, BMP, ICO). Returns false for unknown or
    /// malformed headers; callers then fall back to the byte-length limit only.
    /// </summary>
    public static bool TryGetImageDimensions(byte[] data, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (data.Length < 8)
            return false;

        try
        {
            if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
                return TryGetPngDimensions(data, out width, out height);
            if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
                return TryGetJpegDimensions(data, out width, out height);
            if (data[0] == 'G' && data[1] == 'I' && data[2] == 'F')
                return TryGetGifDimensions(data, out width, out height);
            if (data[0] == 'B' && data[1] == 'M')
                return TryGetBmpDimensions(data, out width, out height);
            if (data[0] == 0 && data[1] == 0 && data[2] == 1 && data[3] == 0)
                return TryGetIcoDimensions(data, out width, out height);
        }
        catch (Exception)
        {
            // Truncated/oversized headers must never break resource inspection.
        }

        return false;
    }

    private static bool TryGetPngDimensions(byte[] data, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (data.Length < 24)
            return false;
        // The IHDR chunk must be the first chunk of a PNG stream.
        if (data[12] != (byte)'I' || data[13] != (byte)'H' || data[14] != (byte)'D' || data[15] != (byte)'R')
            return false;

        uint w = ReadU32BigEndian(data, 16);
        uint h = ReadU32BigEndian(data, 20);
        if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue)
            return false;

        width = (int)w;
        height = (int)h;
        return true;
    }

    private static bool TryGetGifDimensions(byte[] data, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (data.Length < 10)
            return false;

        width = data[6] | (data[7] << 8);
        height = data[8] | (data[9] << 8);
        return width > 0 && height > 0;
    }

    private static bool TryGetBmpDimensions(byte[] data, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (data.Length < 26)
            return false;

        int headerSize = BitConverter.ToInt32(data, 14);
        if (headerSize == 12)
        {
            // BITMAPCOREHEADER: 16-bit fields.
            width = BitConverter.ToUInt16(data, 18);
            height = BitConverter.ToUInt16(data, 20);
        }
        else if (headerSize >= 40)
        {
            width = BitConverter.ToInt32(data, 18);
            height = BitConverter.ToInt32(data, 22); // Negative for top-down DIBs.
        }
        else
        {
            return false;
        }

        if (width < 0)
            width = -width;
        if (height < 0)
            height = -height;
        return width > 0 && height > 0;
    }

    private static bool TryGetIcoDimensions(byte[] data, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (data.Length < 22)
            return false;
        if (BitConverter.ToUInt16(data, 4) == 0)
            return false;

        // The directory entry stores 0 for 256 px.
        width = data[6] == 0 ? 256 : data[6];
        height = data[7] == 0 ? 256 : data[7];
        return true;
    }

    private static bool TryGetJpegDimensions(byte[] data, out int width, out int height)
    {
        width = 0;
        height = 0;
        int index = 2;
        while (index + 9 < data.Length)
        {
            if (data[index] != 0xFF)
            {
                index++;
                continue;
            }

            byte marker = data[index + 1];
            index += 2;
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                continue; // Standalone markers.
            if (marker is 0xD9 or 0xDA)
                return false; // EOI / start of scan before any frame header.

            int length = (data[index] << 8) | data[index + 1];
            if (length < 2)
                return false;

            bool isStartOfFrame =
                (marker >= 0xC0 && marker <= 0xC3) ||
                (marker >= 0xC5 && marker <= 0xC7) ||
                (marker >= 0xC9 && marker <= 0xCB) ||
                (marker >= 0xCD && marker <= 0xCF);
            if (isStartOfFrame)
            {
                height = (data[index + 3] << 8) | data[index + 4];
                width = (data[index + 5] << 8) | data[index + 6];
                return width > 0 && height > 0;
            }

            index += length;
        }

        return false;
    }

    private static uint ReadU32BigEndian(byte[] data, int offset)
        => ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
           ((uint)data[offset + 2] << 8) | data[offset + 3];

    private static bool TryDecodeText(byte[] data, out string? text)
    {
        text = null;
        if (data.Length == 0)
        {
            text = string.Empty;
            return true;
        }

        try
        {
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            string candidate = utf8.GetString(data);
            if (IsMostlyPrintable(candidate))
            {
                text = candidate;
                return true;
            }
        }
        catch (DecoderFallbackException)
        {
            // Not UTF-8; try Latin-1 below only if it still looks like text.
        }

        try
        {
            string latin = Encoding.Latin1.GetString(data);
            if (IsMostlyPrintable(latin))
            {
                text = latin;
                return true;
            }
        }
        catch
        {
            // Ignore.
        }

        return false;
    }

    private static bool IsMostlyPrintable(string text)
    {
        if (text.Length == 0)
            return true;

        int printable = 0;
        int sample = Math.Min(text.Length, 4000);
        for (int i = 0; i < sample; i++)
        {
            char value = text[i];
            if (!char.IsControl(value) || value is '\r' or '\n' or '\t')
                printable++;
        }

        return printable >= sample * 0.92;
    }

    private static (bool IsImage, string? Format) DetectImage(byte[] data)
    {
        if (data.Length >= 8 &&
            data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            return (true, "PNG");
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return (true, "JPEG");
        if (data.Length >= 6 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F')
            return (true, "GIF");
        if (data.Length >= 2 && data[0] == 'B' && data[1] == 'M')
            return (true, "BMP");
        if (data.Length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 1 && data[3] == 0)
            return (true, "ICO");
        return (false, null);
    }

    private static string? HexPreview(byte[] data, int limit)
    {
        if (data.Length == 0)
            return null;

        int length = Math.Min(data.Length, limit);
        var builder = new StringBuilder(length * 4);
        for (int i = 0; i < length; i += 16)
        {
            int rowLength = Math.Min(16, length - i);
            builder.Append(i.ToString("X8")).Append("  ");
            for (int j = 0; j < rowLength; j++)
            {
                builder.Append(data[i + j].ToString("X2")).Append(' ');
            }

            builder.Append(' ', (16 - rowLength) * 3);
            builder.Append("  ");
            for (int j = 0; j < rowLength; j++)
            {
                byte value = data[i + j];
                builder.Append(value is >= 32 and < 127 ? (char)value : '.');
            }

            builder.AppendLine();
        }

        if (data.Length > length)
            builder.AppendLine("… preview truncated");
        return builder.ToString();
    }

    private static string Truncate(string text, int limit)
        => text.Length <= limit ? text : text[..limit] + "\n… preview truncated";

    public static bool TrySave(ResourceContent content, string path, out string? error)
    {
        error = null;
        if (content.Data.Length == 0)
        {
            error = "This resource has no embedded payload to save.";
            return false;
        }

        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllBytes(path, content.Data);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
