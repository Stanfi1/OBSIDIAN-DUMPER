using System.Diagnostics;
using Microsoft.Diagnostics.Runtime;

namespace Obsidian.Dumper.Core.Runtime;

public sealed class RuntimeTypeStat
{
    public string Name { get; init; } = string.Empty;
    public long Count { get; init; }
    public long Size { get; init; }
    public double SharePercent { get; init; }

    public string CountLabel => Count.ToString("N0");
    public string SizeLabel => Format(Size);
    public string ShareLabel => SharePercent <= 0 ? "—" : SharePercent.ToString("0.0") + "%";

    internal static string Format(long bytes)
    {
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
        return value.ToString("0.##") + " " + units[unit];
    }
}

/// <summary>Read-only snapshot of a managed runtime via ClrMD.</summary>
public sealed class RuntimeSnapshot
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string RuntimeLabel { get; init; } = "—";
    public string FlavorLabel { get; init; } = "—";
    public string VersionLabel { get; init; } = "—";
    public int ThreadCount { get; init; }
    public int ModuleCount { get; init; }
    public long ObjectCount { get; init; }
    public long HeapSize { get; init; }
    public bool CanWalkHeap { get; init; }
    public bool Truncated { get; init; }
    public TimeSpan Duration { get; init; }
    public IReadOnlyList<RuntimeTypeStat> Types { get; init; } = Array.Empty<RuntimeTypeStat>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public string HeapLabel => RuntimeTypeStat.Format(HeapSize);
    public string ObjectCountLabel => ObjectCount.ToString("N0");
    public string DurationLabel => Duration.TotalSeconds.ToString("0.0") + " s";
    public string SummaryLabel => Success
        ? $"{FlavorLabel} {VersionLabel} · {ThreadCount} threads · {ModuleCount} modules · {ObjectCountLabel} objects · {HeapLabel}"
        : Error ?? "Runtime snapshot unavailable";
}

/// <summary>
/// Captures a heap/type snapshot with ClrMD. The attach is read-only and
/// non-invasive; every failure is returned as <see cref="RuntimeSnapshot.Error"/>
/// so the UI can explain why a target cannot be inspected.
/// </summary>
public static class RuntimeSnapshotService
{
    public static RuntimeSnapshot Capture(
        int processId,
        int topTypes = 80,
        TimeSpan? budget = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        TimeSpan timeBudget = budget ?? TimeSpan.FromSeconds(10);
        var warnings = new List<string>();

        try
        {
            progress?.Report("Attaching to the CLR…");
            using DataTarget target = DataTarget.AttachToProcess(processId, false);
            cancellationToken.ThrowIfCancellationRequested();

            ClrInfo? clr = target.ClrVersions.FirstOrDefault();
            if (clr == null)
            {
                return new RuntimeSnapshot
                {
                    Error = "No CLR runtime was found in this process (native or unsupported target).",
                    Duration = stopwatch.Elapsed,
                };
            }

            string flavor = clr.Flavor.ToString();
            string version = clr.Version?.ToString() ?? "unknown";

            progress?.Report($"Creating the {flavor} runtime…");
            using ClrRuntime runtime = clr.CreateRuntime();
            cancellationToken.ThrowIfCancellationRequested();

            int threadCount = runtime.Threads.Count();
            int moduleCount = runtime.EnumerateModules().Count();
            ClrHeap heap = runtime.Heap;
            bool canWalk = heap.CanWalkHeap;
            if (!canWalk)
                warnings.Add("The heap is not walkable on this runtime; object counts are unavailable.");

            var stats = new Dictionary<string, (long Count, long Size)>(StringComparer.Ordinal);
            long objectCount = 0;
            long heapSize = 0;
            bool truncated = false;

            if (canWalk)
            {
                progress?.Report("Walking the managed heap…");
                var deadline = stopwatch.Elapsed + timeBudget;
                foreach (ClrObject item in heap.EnumerateObjects())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if ((objectCount & 0x1FFF) == 0 && stopwatch.Elapsed > deadline)
                    {
                        truncated = true;
                        warnings.Add($"Heap walk stopped after {timeBudget.TotalSeconds:0} s; counts are partial.");
                        break;
                    }

                    ClrType? type = item.Type;
                    string name = type?.Name ?? "<unknown>";
                    long size = (long)item.Size;
                    objectCount++;
                    heapSize += size;

                    if (stats.TryGetValue(name, out (long Count, long Size) current))
                        stats[name] = (current.Count + 1, current.Size + size);
                    else
                        stats[name] = (1, size);
                }
            }

            List<RuntimeTypeStat> top = stats
                .OrderByDescending(pair => pair.Value.Size)
                .Take(Math.Clamp(topTypes, 10, 500))
                .Select(pair => new RuntimeTypeStat
                {
                    Name = pair.Key,
                    Count = pair.Value.Count,
                    Size = pair.Value.Size,
                    SharePercent = heapSize > 0 ? pair.Value.Size * 100.0 / heapSize : 0,
                })
                .ToList();

            progress?.Report("Snapshot complete.");
            return new RuntimeSnapshot
            {
                Success = true,
                FlavorLabel = flavor,
                VersionLabel = version,
                RuntimeLabel = flavor + " " + version,
                ThreadCount = threadCount,
                ModuleCount = moduleCount,
                ObjectCount = objectCount,
                HeapSize = heapSize,
                CanWalkHeap = canWalk,
                Truncated = truncated,
                Duration = stopwatch.Elapsed,
                Types = top,
                Warnings = warnings,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new RuntimeSnapshot
            {
                Error = Explain(ex),
                Duration = stopwatch.Elapsed,
            };
        }
    }

    private static string Explain(Exception exception)
    {
        string message = exception.Message;
        if (exception is DllNotFoundException or FileNotFoundException)
            return "ClrMD could not load its diagnostic components for this target: " + message;
        if (exception is UnauthorizedAccessException)
            return "Access denied. Run OBSIDIAN elevated to inspect this process.";
        if (message.Contains("DAC", StringComparison.OrdinalIgnoreCase))
            return "The runtime's data-access component (DAC) could not be loaded: " + message;
        return "Runtime snapshot failed: " + message;
    }
}
