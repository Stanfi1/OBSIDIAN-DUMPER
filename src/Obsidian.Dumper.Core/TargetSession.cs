using Obsidian.Dumper.Core.Diagnostics;
using Obsidian.Dumper.Core.Model;
using Obsidian.Dumper.Shared;

namespace Obsidian.Dumper.Core;

/// <summary>
/// One attached target: a single process handle, the module snapshot and the
/// metadata discovered about the process. Disposing the session releases the
/// handle, which is why the UI keeps exactly one alive at a time.
/// </summary>
public sealed class TargetSession : IDisposable
{
    private readonly RemoteProcess _target;
    private IReadOnlyList<ModuleRecord> _modules;
    private IReadOnlyList<ModuleRecord>? _managedModules;
    private bool _disposed;

    private TargetSession(RemoteProcess target, ProcessRecord process, IReadOnlyList<ModuleRecord> modules, string? commandLine)
    {
        _target = target;
        Process = process;
        _modules = modules;
        CommandLine = commandLine;
    }

    public ProcessRecord Process { get; }
    public ClrInfo? Clr => Process.Clr;
    public string? CommandLine { get; }
    public RemoteProcess Target => _target;
    public int Id => Process.Id;

    public IReadOnlyList<ModuleRecord> Modules => _modules;

    /// <summary>Managed modules of the current snapshot (cached until the next <see cref="ReloadModules"/>).</summary>
    public IReadOnlyList<ModuleRecord> ManagedModules => _managedModules ??= _modules.Where(m => m.IsManaged).ToList();

    public static TargetSession? Attach(int processId, out string? error)
    {
        error = null;

        ProcessRecord record = ProcessDiscovery.Inspect(processId);
        if (!record.CanRead)
        {
            error = record.AccessProblem ?? "Access to the process was denied.";
            return null;
        }

        RemoteProcess? target = RemoteProcess.TryOpen(processId, out error);
        if (target == null)
            return null;

        try
        {
            IReadOnlyList<ModuleRecord> modules = target.GetModules();
            string? commandLine = target.TryReadCommandLine();

            var enriched = record with
            {
                ModuleCount = modules.Count,
                CommandLine = string.IsNullOrWhiteSpace(commandLine) ? null : commandLine,
            };

            return new TargetSession(target, enriched, modules, enriched.CommandLine);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            target.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Re-reads the module list; the dump state (status, output path, note) of
    /// every module is carried over by name so a "Rescan modules" action never
    /// loses the result of an earlier dump. Works for native and managed modules
    /// alike and tolerates duplicate module base names.
    /// </summary>
    public IReadOnlyList<ModuleRecord> ReloadModules()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Two loaded modules can share a base name (different directories), so a
        // plain ToDictionary(name) would throw for such targets.
        var previous = new Dictionary<string, ModuleRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (ModuleRecord module in _modules)
        {
            if (!previous.ContainsKey(module.Name))
                previous[module.Name] = module;
        }

        var modules = _target.GetModules();
        foreach (ModuleRecord module in modules)
        {
            if (!previous.TryGetValue(module.Name, out ModuleRecord? old))
                continue;
            module.Status = old.Status;
            module.DumpedPath = old.DumpedPath;
            module.Note = old.Note;
        }

        _modules = modules;
        _managedModules = null;
        return modules;
    }

    /// <summary>
    /// Reconstructs a mapped module for inspection without writing it to disk.
    /// Returns null when the image cannot be read or rebuilt.
    /// </summary>
    public byte[]? ReadModuleImage(ModuleRecord module, PeRebuildOptions options, out byte[]? mapped, out int unreadablePages)
    {
        mapped = null;
        unreadablePages = 0;

        byte[] header = _target.ReadBytes(module.BaseAddress, 0x1000, out _);
        if (!PeImageLayout.TryReadHeaders(header, out PeHeaders headers))
            return null;

        long size = headers.SizeOfImage;
        if (size <= 0 || size > 512L * 1024 * 1024)
            size = Math.Max(module.Size, 0x1000);
        if (size <= 0 || size > int.MaxValue)
            return null;

        mapped = _target.ReadBytes(module.BaseAddress, (int)size, out unreadablePages);
        return PeImageLayout.Rebuild(mapped, options);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _target.Dispose();
    }
}
