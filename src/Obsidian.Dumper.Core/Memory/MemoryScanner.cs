using System.Globalization;
using System.Text;
using Obsidian.Dumper.Core.Model;

namespace Obsidian.Dumper.Core.Memory;

public enum MemoryScanKind
{
    Utf8 = 0,
    Utf16 = 1,
    Hex = 2,
}

public sealed class MemoryScanOptions
{
    public MemoryScanKind Kind { get; init; } = MemoryScanKind.Utf8;
    public string Pattern { get; init; } = string.Empty;
    public bool CaseInsensitive { get; init; } = true;
    public bool ExecutableOnly { get; init; }
    public int MaxResults { get; init; } = 2000;
    public long MaxBytes { get; init; } = 512L * 1024 * 1024;
    public int ContextBytes { get; init; } = 24;
}

public sealed class MemoryScanProgress
{
    public int RegionsScanned { get; init; }
    public int RegionsTotal { get; init; }
    public long BytesScanned { get; init; }
    public int Hits { get; init; }
    public string? CurrentModule { get; init; }
}

public sealed class MemoryScanHit
{
    public ulong Address { get; init; }
    public ulong RegionBase { get; init; }
    public ulong RegionSize { get; init; }
    public string? ModuleName { get; init; }
    public uint Protect { get; init; }
    public string Preview { get; init; } = string.Empty;
    public byte[] Context { get; init; } = Array.Empty<byte>();

    public string ProtectLabel
    {
        get
        {
            uint protect = Protect & 0xFF;
            string access = protect switch
            {
                0x02 => "R--",
                0x04 => "RW-",
                0x08 => "RW-",
                0x10 => "--X",
                0x20 => "R-X",
                0x40 => "RWX",
                0x80 => "RWX",
                _ => "---",
            };
            return (Protect & 0x100) != 0 ? access + "g" : access;
        }
    }

    public string AddressLabel => "0x" + Address.ToString("X", CultureInfo.InvariantCulture);
    public string RegionLabel => "0x" + RegionBase.ToString("X", CultureInfo.InvariantCulture);
    public string ModuleLabel => string.IsNullOrEmpty(ModuleName) ? "—" : ModuleName!;
}

public sealed class MemoryScanResult
{
    public IReadOnlyList<MemoryScanHit> Hits { get; init; } = Array.Empty<MemoryScanHit>();
    public long BytesScanned { get; init; }
    public int RegionsScanned { get; init; }
    public bool Truncated { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Read-only search over committed readable regions of a live process. Patterns
/// are UTF-8/ASCII text, UTF-16LE text or a hex byte pattern with <c>??</c>
/// wildcards. Scanning is chunked, cancellable and bounded by <see cref="MemoryScanOptions.MaxBytes"/>.
/// </summary>
public static class MemoryScanner
{
    private const int ChunkSize = 1024 * 1024;

    public static MemoryScanResult Scan(
        RemoteProcess process,
        IReadOnlyList<ModuleRecord> modules,
        MemoryScanOptions options,
        IProgress<MemoryScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(options);

        (byte[]? pattern, bool[]? mask, string? error) = Compile(options);
        if (error != null || pattern == null)
            return new MemoryScanResult { Error = error ?? "Empty search pattern." };

        IReadOnlyList<MemoryRegion> regions = MemoryMapReader.Read(process, modules, cancellationToken);
        var hits = new List<MemoryScanHit>();
        long bytesScanned = 0;
        int regionsScanned = 0;
        bool truncated = false;
        bool fold = options.CaseInsensitive && options.Kind == MemoryScanKind.Utf8;

        foreach (MemoryRegion region in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!region.IsReadable || region.IsGuarded || region.Size == 0)
                continue;
            if (options.ExecutableOnly && !region.IsExecutable)
                continue;

            regionsScanned++;
            ulong position = region.BaseAddress;
            ulong end = region.BaseAddress + region.Size;
            int overlap = pattern.Length - 1;

            while (position < end)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int chunk = (int)Math.Min(ChunkSize, end - position);
                ulong remaining = end - position;
                int readLength = (int)Math.Min((ulong)(chunk + overlap), Math.Min(remaining, int.MaxValue - 16));
                byte[] buffer = new byte[readLength];
                // ReadInto returns the number of unreadable pages (not bytes);
                // the buffer is fully populated with readable data plus zeros.
                int unreadablePages = process.ReadInto(position, buffer, 0, readLength);
                int unreadableBytes = Math.Min(readLength, unreadablePages * 0x1000);
                int read = readLength - unreadableBytes;
                if (read <= 0)
                {
                    position += (ulong)chunk;
                    continue;
                }

                bytesScanned += read;
                int searchEnd = read;
                int matchLimit = chunk; // matches starting in the overlap belong to the next chunk
                int index = 0;
                while (index <= searchEnd - pattern.Length)
                {
                    int found = Find(buffer, index, searchEnd, pattern, mask, fold);
                    if (found < 0)
                        break;

                    if (found < matchLimit)
                    {
                        hits.Add(CreateHit(process, region, position, found, buffer, read, pattern.Length, options));
                        if (hits.Count >= options.MaxResults)
                        {
                            truncated = true;
                            break;
                        }
                    }

                    index = found + Math.Max(1, pattern.Length);
                }

                if (truncated)
                    break;

                position += (ulong)chunk;

                if (bytesScanned >= options.MaxBytes)
                {
                    truncated = true;
                    break;
                }
            }

            progress?.Report(new MemoryScanProgress
            {
                RegionsScanned = regionsScanned,
                RegionsTotal = regions.Count,
                BytesScanned = bytesScanned,
                Hits = hits.Count,
                CurrentModule = region.ModuleName,
            });

            if (truncated)
                break;
        }

        return new MemoryScanResult
        {
            Hits = hits,
            BytesScanned = bytesScanned,
            RegionsScanned = regionsScanned,
            Truncated = truncated,
        };
    }

    private static MemoryScanHit CreateHit(
        RemoteProcess process,
        MemoryRegion region,
        ulong chunkAddress,
        int offset,
        byte[] buffer,
        int read,
        int patternLength,
        MemoryScanOptions options)
    {
        int context = Math.Max(4, Math.Min(options.ContextBytes, 128));
        int start = Math.Max(0, offset - context);
        int end = Math.Min(read, offset + patternLength + context);
        int length = Math.Max(0, end - start);
        byte[] contextBytes = new byte[length];
        if (length > 0)
            Buffer.BlockCopy(buffer, start, contextBytes, 0, length);

        string preview = options.Kind == MemoryScanKind.Utf16
            ? PreviewUtf16(contextBytes)
            : PreviewAscii(contextBytes);

        return new MemoryScanHit
        {
            Address = chunkAddress + (ulong)offset,
            RegionBase = region.BaseAddress,
            RegionSize = region.Size,
            ModuleName = region.ModuleName,
            Protect = region.Protect,
            Context = contextBytes,
            Preview = preview,
        };
    }

    private static string PreviewAscii(byte[] data)
    {
        var builder = new StringBuilder(data.Length);
        foreach (byte value in data)
            builder.Append(value is >= 32 and < 127 ? (char)value : '·');
        return builder.ToString();
    }

    private static string PreviewUtf16(byte[] data)
    {
        var builder = new StringBuilder(data.Length / 2);
        for (int i = 0; i + 1 < data.Length; i += 2)
        {
            char value = (char)(data[i] | (data[i + 1] << 8));
            builder.Append(char.IsControl(value) ? '·' : value);
        }
        return builder.ToString();
    }

    private static int Find(byte[] haystack, int start, int end, byte[] needle, bool[]? mask, bool folded)
    {
        int last = end - needle.Length;
        for (int i = start; i <= last; i++)
        {
            bool firstMatches = mask is { Length: > 0 } && mask[0]
                ? true
                : Fold(haystack[i], folded) == needle[0];
            if (!firstMatches)
                continue;

            int j = 1;
            for (; j < needle.Length; j++)
            {
                if (mask != null && mask[j])
                    continue;
                if (Fold(haystack[i + j], folded) != needle[j])
                    break;
            }

            if (j == needle.Length)
                return i;
        }

        return -1;
    }

    private static byte Fold(byte value, bool folded)
        => folded && value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 32) : value;

    private static (byte[]? Pattern, bool[]? Mask, string? Error) Compile(MemoryScanOptions options)
    {
        string text = options.Pattern ?? string.Empty;
        if (text.Length == 0)
            return (null, null, "Enter a search pattern.");

        if (options.Kind == MemoryScanKind.Hex)
        {
            string[] tokens = text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                return (null, null, "Enter a hex pattern such as 48 8B ?? 05.");

            var bytes = new byte[tokens.Length];
            var mask = new bool[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i];
                if (token is "??" or "?")
                {
                    mask[i] = true;
                    continue;
                }

                // Exactly one byte per token: "4D5A" used to slip through as the
                // single byte 0x4D (byte.TryParse ignores the leading zeros), which
                // silently searched for the wrong pattern.
                if (token.Length != 2)
                    return (null, null,
                        $"'{token}' is not a hex byte or wildcard (use two hex digits like 4D, or ??).");
                if (!byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value))
                    return (null, null, $"'{token}' is not valid hex.");
                bytes[i] = value;
            }

            return (bytes, mask, null);
        }

        if (options.Kind == MemoryScanKind.Utf16)
        {
            byte[] utf16 = Encoding.Unicode.GetBytes(text);
            return (utf16, null, null);
        }

        string prepared = options.CaseInsensitive ? FoldAscii(text) : text;
        return (Encoding.UTF8.GetBytes(prepared), null, null);
    }

    private static string FoldAscii(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (char value in text)
            builder.Append(value is >= 'A' and <= 'Z' ? (char)(value + 32) : value);
        return builder.ToString();
    }
}
