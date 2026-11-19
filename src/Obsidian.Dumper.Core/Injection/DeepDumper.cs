using System.Diagnostics;
using System.Globalization;
using System.Text;
using Obsidian.Dumper.Core.Diagnostics;
using Obsidian.Dumper.Core.Model;

namespace Obsidian.Dumper.Core.Injection;

public enum DeepDumpStatus
{
    Success,
    Unsupported,
    Unavailable,
    Failed,
}

public sealed class DeepDumpResult
{
    public DeepDumpStatus Status { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<DeepDumpedFile> Files { get; init; } = Array.Empty<DeepDumpedFile>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    public int DurationMs { get; init; }
    public string? WorkDirectory { get; init; }

    public bool Success => Status == DeepDumpStatus.Success;
}

public sealed class DeepDumpedFile
{
    public string AssemblyName { get; init; } = string.Empty;
    public string ModuleName { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public long Bytes { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string? OriginalLocation { get; init; }
}

/// <summary>
/// Managed-agent dump ("deep dump"). A tiny native agent is loaded into the
/// target with CreateRemoteThread(LoadLibraryW); its DllMain hands control to
/// the CLR hosting API of the target, which executes
/// <c>PayloadEntry.DumpAll(outputDirectory)</c> inside the target's default
/// app domain. That enumerates every loaded assembly — including assemblies
/// loaded from byte arrays that never appear in the module list.
///
/// Scope: x64 .NET Framework 4.x targets. A live CoreCLR cannot host a second
/// runtime in process, so CoreCLR targets are reported as unsupported and are
/// handled by the out-of-process + scan engines instead.
/// </summary>
public sealed class DeepDumper
{
    private const string AgentTypeName = "Obsidian.Dumper.Payload.PayloadEntry";
    private const string AgentMethodName = "DumpAll";

    /// <summary>Work folders this old are leftovers from finished runs and get purged.</summary>
    private static readonly TimeSpan StaleWorkDirectoryAge = TimeSpan.FromHours(1);

    private readonly ILogSink _log;

    public DeepDumper(ILogSink? log = null)
    {
        _log = log ?? NullLogSink.Instance;
    }

    public static string? AgentPath { get; private set; }
    public static string? PayloadPath { get; private set; }

    public static bool IsAvailable => ResolveAssets(out _, out _);

    public static bool ResolveAssets(out string? agent, out string? payload)
    {
        agent = AgentPath;
        payload = PayloadPath;
        if (agent != null && payload != null && File.Exists(agent) && File.Exists(payload))
            return true;

        foreach (string root in CandidateRoots())
        {
            string native = Path.Combine(root, "native", "Obsidian.Injector.x64.dll");
            string managed = Path.Combine(root, "payload", "net48", "Obsidian.Dumper.Payload.dll");
            if (File.Exists(native) && File.Exists(managed))
            {
                agent = native;
                payload = managed;
                AgentPath = native;
                PayloadPath = managed;
                return true;
            }
        }

        // Development layout: src/<project>/bin/<configuration>/<tfm>/...
        foreach (string root in CandidateRoots())
        {
            string? repo = FindRepositoryRoot(root);
            if (repo == null)
                continue;

            string native = Path.Combine(repo, "src", "Obsidian.Dumper.Native", "bin", "Obsidian.Injector.x64.dll");
            string managed = Path.Combine(repo, "src", "Obsidian.Dumper.Payload", "bin",
                "Release", "net48", "Obsidian.Dumper.Payload.dll");
            if (!File.Exists(managed))
            {
                managed = Path.Combine(repo, "src", "Obsidian.Dumper.Payload", "bin",
                    "Debug", "net48", "Obsidian.Dumper.Payload.dll");
            }

            if (File.Exists(native) && File.Exists(managed))
            {
                agent = native;
                payload = managed;
                AgentPath = native;
                PayloadPath = managed;
                return true;
            }
        }

        return false;
    }

    private static List<string> CandidateRoots()
    {
        var roots = new List<string> { AppContext.BaseDirectory };
        string? current = AppContext.BaseDirectory;
        for (int i = 0; i < 5 && current != null; i++)
        {
            current = Path.GetDirectoryName(current);
            if (current != null)
                roots.Add(current);
        }
        return roots;
    }

    private static string? FindRepositoryRoot(string start)
    {
        string? current = start;
        for (int i = 0; i < 6 && current != null; i++)
        {
            if (File.Exists(Path.Combine(current, "Obsidian.Dumper.sln")))
                return current;
            current = Path.GetDirectoryName(current);
        }
        return null;
    }

    public async Task<DeepDumpResult> RunAsync(TargetSession session, DumpOptions options,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        if (!ResolveAssets(out string? agentSource, out string? payloadSource))
        {
            return new DeepDumpResult
            {
                Status = DeepDumpStatus.Unavailable,
                Message = "The managed agent is not present. Build it with build-native.ps1 and build the payload project.",
            };
        }

        ClrInfo? clr = session.Clr;
        if (clr == null || clr.Flavor == ClrFlavor.None)
        {
            return new DeepDumpResult
            {
                Status = DeepDumpStatus.Unsupported,
                Message = "The target does not host a CLR.",
            };
        }

        if (clr.Flavor == ClrFlavor.Core)
        {
            return new DeepDumpResult
            {
                Status = DeepDumpStatus.Unsupported,
                Message = "Deep dump requires .NET Framework 4.x. CoreCLR cannot host a second runtime in process — use the memory dump or the aggressive scan for this target.",
            };
        }

        if (session.Process.Architecture != ProcessArchitecture.X64)
        {
            return new DeepDumpResult
            {
                Status = DeepDumpStatus.Unsupported,
                Message = "The agent is x64 only; this target is " + session.Process.ArchitectureLabel + ".",
            };
        }

        string workDirectory = CreateWorkDirectory(options.WorkDirectory);
        string outputDirectory = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);

        string agentPath = Path.Combine(workDirectory, "Obsidian.Injector.x64.dll");
        string payloadPath = Path.Combine(workDirectory, "Obsidian.Dumper.Payload.dll");
        string jobPath = Path.Combine(workDirectory, "obsidian.job");
        string resultPath = Path.Combine(workDirectory, "obsidian.result");

        try
        {
            File.Copy(agentSource!, agentPath, true);
            File.Copy(payloadSource!, payloadPath, true);
            File.WriteAllText(jobPath,
                payloadPath + "\n" + AgentTypeName + "\n" + AgentMethodName + "\n" + outputDirectory + "\n",
                new UTF8Encoding(false));

            progress?.Report("Injecting agent into pid " + session.Id);

            using RemoteProcess? target = RemoteProcess.TryOpen(session.Id, out string? openError, forInjection: true);
            if (target == null)
            {
                return new DeepDumpResult
                {
                    Status = DeepDumpStatus.Failed,
                    Message = "Cannot open the target for injection: " + openError,
                    WorkDirectory = workDirectory,
                };
            }

            if (!Inject(target, agentPath, out string? injectError))
            {
                return new DeepDumpResult
                {
                    Status = DeepDumpStatus.Failed,
                    Message = "Injection failed: " + injectError,
                    WorkDirectory = workDirectory,
                };
            }

            _log.Write(LogLevel.Info, "Agent loaded into pid " + session.Id + ", waiting for the payload", "deep");

            string? result = await WaitForResultAsync(resultPath, target, TimeSpan.FromSeconds(90), cancellationToken)
                .ConfigureAwait(false);

            if (result == null)
            {
                string injectorLog = ReadIfExists(Path.Combine(workDirectory, "obsidian-injector.log"));
                return new DeepDumpResult
                {
                    Status = DeepDumpStatus.Failed,
                    Message = "The agent did not report back. " + injectorLog,
                    WorkDirectory = workDirectory,
                    DurationMs = (int)stopwatch.ElapsedMilliseconds,
                };
            }

            progress?.Report("Payload finished, reading manifest");

            var files = ReadManifest(outputDirectory);
            var notes = new List<string>();

            string payloadLog = ReadIfExists(Path.Combine(outputDirectory, "obsidian-payload.log"));
            if (payloadLog.Length > 0)
                _log.Write(LogLevel.Trace, payloadLog.Trim(), "payload");

            // The native agent writes "framework\t<CLR HRESULT>\t<managed return value>".
            // The managed return value used to be ignored, so a payload that failed
            // from inside the target (returned -1/-2, no manifest) was reported as a
            // successful dump of 0 modules. Only a zero HRESULT *and* a non-negative
            // managed return value with a manifest count as success.
            bool failed;
            bool payloadCompleted = false;
            string detail = result.Replace('\t', ' ');
            if (result.StartsWith("framework", StringComparison.OrdinalIgnoreCase))
            {
                string[] fields = result.Split('\t');
                bool clrOk = fields.Length >= 2 &&
                             fields[1].Equals("0x00000000", StringComparison.OrdinalIgnoreCase);
                // The row carries the managed int as an unsigned DWORD, so a
                // managed -1 arrives here as 4294967295.
                long rawReturn = 0;
                bool returnKnown = fields.Length >= 3 &&
                    long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out rawReturn);
                int managedReturn = returnKnown ? unchecked((int)rawReturn) : 0;
                payloadCompleted = clrOk && returnKnown && managedReturn >= 0;
                failed = !payloadCompleted;

                if (!clrOk)
                    detail = "The CLR hosting call failed: " + result.Replace('\t', ' ');
                else if (!returnKnown)
                    detail = "The agent result could not be parsed: " + result.Replace('\t', ' ');
                else if (managedReturn < 0)
                    detail = $"The managed agent returned {managedReturn} (payload failure); see obsidian-payload.log in {outputDirectory}.";
            }
            else
            {
                failed = true;
                detail = "The managed agent returned an unexpected result: " + result.Replace('\t', ' ');
            }

            if (!failed && !File.Exists(Path.Combine(outputDirectory, "obsidian-manifest.tsv")))
            {
                failed = true;
                detail = "The managed agent reported success but wrote no manifest; no files were captured.";
            }

            if (!failed && files.Count == 0)
                notes.Add("The manifest is empty: the target's default app domain has no dumpable assemblies.");

            if (failed && payloadLog.Length > 0)
            {
                string line = PayloadFailureLine(payloadLog);
                if (line.Length > 0)
                    detail += " Payload log: " + line;
            }

            return new DeepDumpResult
            {
                Status = failed ? DeepDumpStatus.Failed : DeepDumpStatus.Success,
                Message = failed ? detail : $"Agent dumped {files.Count} module(s) from inside the target.",
                Files = files,
                Notes = notes,
                DurationMs = (int)stopwatch.ElapsedMilliseconds,
                WorkDirectory = workDirectory,
            };
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a caller decision (CLI maps it to exit code 130);
            // never mask it as a dump failure.
            throw;
        }
        catch (Exception ex)
        {
            return new DeepDumpResult
            {
                Status = DeepDumpStatus.Failed,
                Message = ex.GetType().Name + ": " + ex.Message,
                WorkDirectory = workDirectory,
                DurationMs = (int)stopwatch.ElapsedMilliseconds,
            };
        }
        finally
        {
            CleanUpWorkDirectory(workDirectory);
        }
    }

    /// <summary>
    /// Creates the per-run work directory. A configured <paramref name="preferredParent"/>
    /// is honoured first; when it is null/empty or unusable the historical list of
    /// default writable locations is used.
    /// </summary>
    private string CreateWorkDirectory(string? preferredParent)
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];

        if (!string.IsNullOrWhiteSpace(preferredParent))
        {
            try
            {
                string parent = Path.GetFullPath(preferredParent);
                string configured = Path.Combine(parent, suffix);
                Directory.CreateDirectory(configured);
                TryPurgeStaleWorkDirectories(parent);
                return configured;
            }
            catch (Exception ex)
            {
                _log.Write(LogLevel.Warning,
                    $"The configured deep-dump work directory '{preferredParent}' is not usable ({ex.Message}); falling back to a default location.",
                    "deep");
            }
        }

        string[] candidates =
        [
            // .NET Framework refuses to load managed assemblies from some
            // sandboxed/virtualised folders ("network location" heuristic).
            // Documents is user writable and local on every stock Windows.
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OBSIDIAN", "agent"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OBSIDIAN", "agent"),
            Path.Combine(AppContext.BaseDirectory, "agent"),
            Path.Combine(Path.GetTempPath(), "OBSIDIAN"),
            Path.Combine(Environment.CurrentDirectory, ".obsidian-agent"),
        ];

        foreach (string candidate in candidates)
        {
            try
            {
                string directory = Path.Combine(candidate, suffix);
                Directory.CreateDirectory(directory);
                TryPurgeStaleWorkDirectories(candidate);
                return directory;
            }
            catch (Exception)
            {
                // Try the next location; locked down machines deny several of them.
            }
        }

        throw new IOException("No writable directory for the managed agent was found.");
    }

    /// <summary>
    /// Deletes work folders left behind by earlier runs. Their agent/payload DLLs
    /// stay memory-mapped until the target exits, so the immediate cleanup can
    /// only remove the rest; once the target is gone a later run can finish the
    /// job. Folders younger than <see cref="StaleWorkDirectoryAge"/> are left alone
    /// so a concurrent deep dump is never disturbed.
    /// </summary>
    private void TryPurgeStaleWorkDirectories(string parent)
    {
        try
        {
            if (!Directory.Exists(parent))
                return;

            DateTime threshold = DateTime.UtcNow - StaleWorkDirectoryAge;
            foreach (string directory in Directory.EnumerateDirectories(parent))
            {
                string name = Path.GetFileName(directory);
                if (name.Length != 8)
                    continue;
                if (Directory.GetLastWriteTimeUtc(directory) > threshold)
                    continue;

                try
                {
                    Directory.Delete(directory, recursive: true);
                    _log.Write(LogLevel.Trace,
                        $"Removed stale deep-dump work directory '{directory}'.", "deep");
                }
                catch (Exception)
                {
                    // Still loaded / in use: keep it for a later attempt.
                }
            }
        }
        catch (Exception)
        {
            // Housekeeping must never break the dump itself.
        }
    }

    /// <summary>
    /// Loads the native agent into the target. Delegates to <see cref="DllInjector"/>
    /// so the injection stack has exactly one implementation; the observable
    /// behaviour (CreateRemoteThread + LoadLibraryW, 15 s timeout, freed path
    /// buffer) is unchanged.
    /// </summary>
    private bool Inject(RemoteProcess target, string agentPath, out string? error)
    {
        var injector = new DllInjector(_log);
        InjectionResult result = injector.Inject(target.Id, agentPath, InjectionMethod.CreateRemoteThread);
        if (result.Success)
        {
            error = null;
            return true;
        }

        error = result.Message;
        if (result.Win32Error != 0)
            error += $" (Win32 error {result.Win32Error})";
        return false;
    }

    private static async Task<string?> WaitForResultAsync(string resultPath, RemoteProcess target,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(resultPath))
            {
                try
                {
                    using var stream = new FileStream(resultPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    string content = (await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Trim();
                    if (content.Length > 0)
                        return content;
                }
                catch (IOException)
                {
                    // The agent is still writing; retry.
                }
            }

            if (!target.IsAlive)
                return null;

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>
    /// Reads the manifest written by the agent (untrusted data: it comes from
    /// inside the target). Paths are resolved and must stay inside
    /// <paramref name="outputDirectory"/>; entries that point elsewhere are
    /// skipped and logged instead of being surfaced as dumped files.
    /// </summary>
    private List<DeepDumpedFile> ReadManifest(string outputDirectory)
    {
        var files = new List<DeepDumpedFile>();
        string root = Path.GetFullPath(outputDirectory);
        string path = Path.Combine(root, "obsidian-manifest.tsv");
        if (!File.Exists(path))
            return files;

        foreach (string line in File.ReadAllLines(path))
        {
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            string[] parts = line.Split('\t');
            if (parts.Length < 7)
                continue;

            string file = Unescape(parts[2]);
            if (file.Length == 0)
                continue;

            if (!TryResolveContainedPath(root, file, out string resolvedPath))
            {
                _log.Write(LogLevel.Warning,
                    $"Deep-dump manifest entry '{file}' points outside the output folder; skipped.", "deep");
                continue;
            }

            long.TryParse(parts[3], out long bytes);
            files.Add(new DeepDumpedFile
            {
                AssemblyName = Unescape(parts[0]),
                ModuleName = Unescape(parts[1]),
                Path = resolvedPath,
                Bytes = bytes,
                Sha256 = parts[4],
                Kind = parts[5],
                OriginalLocation = Unescape(parts[6]),
            });
        }

        return files;
    }

    /// <summary>
    /// Resolves a target-supplied relative file name against the output folder
    /// and rejects anything that escapes it (rooted paths, <c>..</c> segments).
    /// </summary>
    private static bool TryResolveContainedPath(string root, string relative, out string fullPath)
    {
        fullPath = string.Empty;
        if (Path.IsPathRooted(relative))
            return false;

        try
        {
            fullPath = Path.GetFullPath(Path.Combine(root, relative));
        }
        catch (Exception)
        {
            // Invalid characters / too long: treat as an invalid entry.
            return false;
        }

        string prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Decodes one escaped manifest field. A single left-to-right pass is required:
    /// replacing short escapes before the "\\\\" escape (or vice versa) corrupts
    /// Windows paths such as <c>C:\temp\new</c>.
    /// </summary>
    private static string Unescape(string value)
    {
        if (value.IndexOf('\\') < 0)
            return value;

        var builder = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c != '\\' || i + 1 >= value.Length)
            {
                builder.Append(c);
                continue;
            }

            char next = value[++i];
            switch (next)
            {
                case 't': builder.Append('\t'); break;
                case 'r': builder.Append('\r'); break;
                case 'n': builder.Append('\n'); break;
                case '\\': builder.Append('\\'); break;
                default:
                    builder.Append('\\').Append(next);
                    break;
            }
        }

        return builder.ToString();
    }

    private static string ReadIfExists(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Picks the most useful line from the agent's payload log: the fatal line
    /// when the payload caught an exception, otherwise the last non-empty line.
    /// </summary>
    private static string PayloadFailureLine(string text)
    {
        string[] lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase))
                return trimmed;
        }

        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.Length > 0)
                return trimmed;
        }

        return string.Empty;
    }

    /// <summary>
    /// Removes the per-run agent folder once the result has been read. The agent
    /// and payload DLLs stay loaded in the target until it exits, so Windows can
    /// keep those files mapped; the remaining folder is then left for a later
    /// cleanup instead of failing the dump. Everything the operator needs (dumped
    /// files, manifest and payload log) lives in the output folder.
    /// </summary>
    private void CleanUpWorkDirectory(string workDirectory)
    {
        try
        {
            if (!Directory.Exists(workDirectory))
                return;

            try
            {
                Directory.Delete(workDirectory, recursive: true);
                return;
            }
            catch (Exception)
            {
                // Locked agent/payload DLLs: delete what is possible below.
            }

            foreach (string file in Directory.EnumerateFiles(workDirectory))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception)
                {
                    // Keep diagnosing in the trace log; never fail the dump.
                }
            }

            Directory.Delete(workDirectory);
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Trace,
                $"Could not remove the deep-dump work directory '{workDirectory}': {ex.Message}", "deep");
        }
    }
}
