using System.Diagnostics;
using System.Text;
using Obsidian.Dumper.Core;
using Obsidian.Dumper.Core.Diagnostics;
using Obsidian.Dumper.Core.Model;

namespace Obsidian.Dumper.Core.Injection;

public sealed class ManagedInjectionResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? ResultRaw { get; init; }
    public int ExitCode { get; init; }
    public int DurationMs { get; init; }
    public string? WorkDirectory { get; init; }
    public string? AssemblyPath { get; init; }
    public string? TypeName { get; init; }
    public string? MethodName { get; init; }

    public string DurationLabel => $"{DurationMs} ms";
}

/// <summary>
/// Managed assembly injection for .NET Framework 4.x x64 targets.
///
/// It reuses the dependency-free native agent that also powers the deep dump:
/// the agent hosts the CLR in the target and calls
/// <c>ICLRRuntimeHost::ExecuteInDefaultAppDomain</c> for the requested
/// assembly/type/method. The target method must have the managed hosting
/// signature <c>static int Method(string)</c>.
/// </summary>
public sealed class ManagedInjectionService
{
    private const string AgentTypeName = "Obsidian.Dumper.Payload.PayloadEntry";
    private const string AgentMethodName = "DumpAll";

    private readonly ILogSink _log;

    public ManagedInjectionService(ILogSink? log = null)
    {
        _log = log ?? NullLogSink.Instance;
    }

    public async Task<ManagedInjectionResult> InjectAsync(
        TargetSession session,
        string assemblyPath,
        string typeName,
        string methodName,
        string argument,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        if (session.Clr == null || session.Clr.Flavor != ClrFlavor.Framework)
        {
            return Fail("Managed injection requires a .NET Framework target; CoreCLR cannot host a second runtime in process.",
                stopwatch, assemblyPath, typeName, methodName);
        }

        if (session.Process.Architecture != ProcessArchitecture.X64)
        {
            return Fail("The managed agent is x64 only; this target is " + session.Process.ArchitectureLabel + ".",
                stopwatch, assemblyPath, typeName, methodName);
        }

        if (!File.Exists(assemblyPath))
        {
            return Fail("The assembly to inject does not exist: " + assemblyPath,
                stopwatch, assemblyPath, typeName, methodName);
        }

        if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(methodName))
        {
            return Fail("A managed injection needs both a type name and a method name.",
                stopwatch, assemblyPath, typeName, methodName);
        }

        if (!DeepDumper.ResolveAssets(out string? agentSource, out _) || agentSource == null)
        {
            return Fail("The native managed-injection agent is not staged. Build the native agent and the payload.",
                stopwatch, assemblyPath, typeName, methodName);
        }

        string workDirectory;
        try
        {
            workDirectory = CreateWorkDirectory();
        }
        catch (Exception ex)
        {
            return Fail("Could not create an agent work directory: " + ex.Message,
                stopwatch, assemblyPath, typeName, methodName);
        }

        string agentPath = Path.Combine(workDirectory, "Obsidian.Injector.x64.dll");
        string jobPath = Path.Combine(workDirectory, "obsidian.job");
        string resultPath = Path.Combine(workDirectory, "obsidian.result");

        try
        {
            File.Copy(agentSource, agentPath, true);
            File.WriteAllText(jobPath,
                Path.GetFullPath(assemblyPath) + "\n" + typeName + "\n" + methodName + "\n" + argument + "\n",
                new UTF8Encoding(false));

            progress?.Report("Injecting the managed host into pid " + session.Id);

            using RemoteProcess? target = RemoteProcess.TryOpen(session.Id, out string? openError, forInjection: true);
            if (target == null)
            {
                return Fail("Cannot open the target for injection: " + (openError ?? "unknown error"),
                    stopwatch, assemblyPath, typeName, methodName, workDirectory);
            }

            var injector = new DllInjector(_log);
            InjectionResult injected = injector.Inject(session.Id, agentPath, InjectionMethod.CreateRemoteThread);
            if (!injected.Success)
            {
                string detail = injected.Message;
                if (injected.Win32Error != 0)
                    detail += $" (Win32 error {injected.Win32Error})";
                return Fail("The managed host could not be loaded: " + detail,
                    stopwatch, assemblyPath, typeName, methodName, workDirectory);
            }

            progress?.Report("Waiting for the managed entry point…");
            string? raw = await WaitForResultAsync(resultPath, target, TimeSpan.FromSeconds(90), cancellationToken)
                .ConfigureAwait(false);

            if (raw == null)
            {
                string injectorLog = ReadIfExists(Path.Combine(workDirectory, "obsidian-injector.log"));
                return Fail("The managed host did not report back. " + injectorLog,
                    stopwatch, assemblyPath, typeName, methodName, workDirectory);
            }

            string[] fields = raw.Split('\t');
            if (fields.Length >= 2 && fields[0].Equals("framework", StringComparison.OrdinalIgnoreCase))
            {
                uint hr = ParseHex(fields[1]);
                int exitCode = fields.Length >= 3 && int.TryParse(fields[2], out int parsed) ? parsed : 0;
                bool success = hr == 0;
                string message = success
                    ? $"Managed entry point returned {exitCode}."
                    : $"The CLR reported 0x{hr:X8}.";

                _log.Write(success ? LogLevel.Success : LogLevel.Error,
                    $"Managed injection {typeName}.{methodName} → pid {session.Id}: {message}", "inject");

                return new ManagedInjectionResult
                {
                    Success = success,
                    Message = message,
                    ResultRaw = raw,
                    ExitCode = exitCode,
                    DurationMs = (int)stopwatch.ElapsedMilliseconds,
                    WorkDirectory = workDirectory,
                    AssemblyPath = Path.GetFullPath(assemblyPath),
                    TypeName = typeName,
                    MethodName = methodName,
                };
            }

            return Fail("The managed host returned an unexpected result: " + raw.Replace('\t', ' '),
                stopwatch, assemblyPath, typeName, methodName, workDirectory);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail(ex.GetType().Name + ": " + ex.Message,
                stopwatch, assemblyPath, typeName, methodName, workDirectory);
        }
    }

    /// <summary>The default entry point used to validate the managed host path.</summary>
    public static (string Type, string Method) DeepDumpEntry => (AgentTypeName, AgentMethodName);

    private static ManagedInjectionResult Fail(string message, Stopwatch stopwatch,
        string assemblyPath, string typeName, string methodName, string? workDirectory = null)
        => new()
        {
            Success = false,
            Message = message,
            DurationMs = (int)stopwatch.ElapsedMilliseconds,
            WorkDirectory = workDirectory,
            AssemblyPath = assemblyPath,
            TypeName = typeName,
            MethodName = methodName,
        };

    private static uint ParseHex(string value)
    {
        string text = value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        return uint.TryParse(text, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out uint parsed)
            ? parsed
            : 1;
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
                    // The agent may still be writing.
                }
            }

            if (!target.IsAlive)
                return null;

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static string CreateWorkDirectory()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OBSIDIAN", "agent"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OBSIDIAN", "agent"),
            Path.Combine(AppContext.BaseDirectory, "agent"),
            Path.Combine(Path.GetTempPath(), "OBSIDIAN"),
        ];

        Exception? last = null;
        foreach (string candidate in candidates)
        {
            try
            {
                string directory = Path.Combine(candidate, Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(directory);
                return directory;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw new IOException("No writable directory for the managed agent was found.", last);
    }

    private static string ReadIfExists(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
