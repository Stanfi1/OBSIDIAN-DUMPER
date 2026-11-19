using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Obsidian.Dumper.Core;
using Obsidian.Dumper.Core.Diagnostics;
using Obsidian.Dumper.Core.Dumping;
using Obsidian.Dumper.Core.Injection;
using Obsidian.Dumper.Core.Inspection;
using Obsidian.Dumper.Core.Model;

namespace Obsidian.Dumper.Cli;

/// <summary>
/// Headless companion to the GUI. Same engine, same output, scriptable.
/// Run <c>obsidian help</c> for the command list and exit codes.
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitUsage = 1;
    private const int ExitFailure = 2;
    private const int ExitCancelled = 130;

    private static readonly LogBus Log = new();

    private static readonly string Version =
        typeof(Program).Assembly.GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "1.1.0";

    private static bool _verbose;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static async Task<int> Main(string[] args)
    {
        TrySetUtf8Console();

        // Accept "--help"/"help" anywhere before the first command; keep plain
        // "obsidian" a usable quick reference but exit as a usage error.
        if (args.Length == 0)
        {
            PrintHelp();
            return ExitUsage;
        }

        string command = args[0].ToLowerInvariant();
        if (command is "-h" or "--help" or "help" or "/?")
        {
            if (args.Length > 1 && args[1].Length > 0 && !args[1].StartsWith('-'))
                return PrintCommandHelp(args[1].ToLowerInvariant());
            PrintHelp();
            return ExitOk;
        }

        // Per-command help: "obsidian dump --help".
        if (args.Skip(1).Any(a => a is "-h" or "--help"))
            return PrintCommandHelp(command);

        try
        {
            ArgReader arguments = ArgReader.Parse(args);
            _verbose = arguments.Has("--verbose") || Environment.GetEnvironmentVariable("OBSIDIAN_DEBUG") == "1";
            return command switch
            {
                "list" => RunList(arguments),
                "info" => RunInfo(arguments),
                "modules" => RunModules(arguments),
                "dump" => await RunDumpAsync(arguments).ConfigureAwait(false),
                "deep" => await RunDeepAsync(arguments).ConfigureAwait(false),
                "inspect" => RunInspect(arguments),
                "token" => RunToken(arguments),
                "diag" => RunDiag(arguments),
                "readtest" => RunReadTest(arguments),
                "inject" => await RunInjectAsync(arguments).ConfigureAwait(false),
                "unload" => await RunUnloadAsync(arguments).ConfigureAwait(false),
                _ => UnknownCommand(args[0]),
            };
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            Console.Error.WriteLine("Run 'obsidian help' for usage.");
            return ExitUsage;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("cancelled.");
            return ExitCancelled;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("fatal: " + Describe(ex));
            if (_verbose)
                Console.Error.WriteLine(ex);
            return ExitFailure;
        }
    }

    private static string Describe(Exception exception)
    {
        string? message = exception.Message;
        return string.IsNullOrWhiteSpace(message)
            ? exception.GetType().Name
            : $"{exception.GetType().Name}: {message}";
    }

    private static void TrySetUtf8Console()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // No attached console (redirected/service host): keep the default.
        }
    }

    // ---------------------------------------------------------------- commands

    private static int RunDiag(ArgReader args)
    {
        Console.WriteLine(EngineDiagnostics.ProbeProcessSnapshot());
        int count = ProcessDiscovery.Enumerate().Count;
        Console.WriteLine($"enumerated={count}");
        return ExitOk;
    }

    private static int RunReadTest(ArgReader args)
    {
        int pid = args.Int("--pid");
        string? name = args.Value("--name");
        if (pid <= 0)
            throw new UsageException("missing required option --pid <n> (or --pid self)");
        if (string.IsNullOrWhiteSpace(name))
            throw new UsageException("missing required option --name <module.dll>");

        using TargetSession? session = TargetSession.Attach(pid, out string? error);
        if (session == null)
        {
            Console.Error.WriteLine("attach failed: " + error);
            return ExitFailure;
        }

        ModuleRecord? module = session.Modules.FirstOrDefault(
            m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (module == null)
        {
            Console.Error.WriteLine($"module not found: {name}");
            return ExitFailure;
        }

        Console.WriteLine($"{module.Name} base=0x{module.BaseAddress:X} size=0x{module.Size:X} managed={module.IsManaged}");
        foreach (string line in session.Target.DescribeMemory(module.BaseAddress, 16))
            Console.WriteLine("  " + line);

        foreach (int size in new[] { 0x100, 0x1000, 0x2000, 0x4000, 0x8000, 0xA000, 0x10000 })
        {
            byte[] bytes = session.Target.ReadBytes(module.BaseAddress, size, out int pages);
            string head = bytes.Length >= 2 ? $"{bytes[0]:X2}{bytes[1]:X2}" : "--";
            Console.WriteLine($"  read 0x{size:X6}: pages-unreadable={pages} head={head} len={bytes.Length}");
        }

        return ExitOk;
    }

    private static int RunInspect(ArgReader args)
    {
        string? path = args.Positional(0) ?? args.Value("--file");
        if (string.IsNullOrWhiteSpace(path))
            throw new UsageException("missing assembly path: obsidian inspect <assembly> [--type <name>] [--il] [--json]");

        if (!File.Exists(path))
        {
            Console.Error.WriteLine("file not found: " + path);
            return ExitFailure;
        }

        if (!AssemblyInspector.TryOpen(path, out AssemblyInspector? inspector, out string? error) || inspector == null)
        {
            Console.Error.WriteLine("not a readable .NET assembly: " + error);
            return ExitFailure;
        }

        using (inspector)
        {
            AssemblySummary summary = inspector.Summary;
            if (args.Has("--json"))
            {
                var payload = new
                {
                    summary,
                    types = inspector.Types.Select(t => new
                    {
                        t.FullName,
                        kind = t.KindLabel,
                        t.Visibility,
                        t.MethodCount,
                        t.FieldCount,
                        t.Token,
                        t.IsObfuscated,
                    }),
                    strings = inspector.Strings.Count,
                    userStrings = inspector.Strings.Count(s => s.Kind == "user"),
                };
                WriteJson(payload);
                return ExitOk;
            }

            Console.WriteLine(summary.FullName);
            Console.WriteLine($"  kind={summary.Kind} runtime={summary.RuntimeVersion} machine={summary.Machine} " +
                              $"ilonly={summary.IsIlOnly} mixed={summary.IsMixedMode} r2r={summary.IsReadyToRun} " +
                              $"strongname={summary.IsStrongNamed}");
            Console.WriteLine($"  types={summary.TypeCount} methods={summary.MethodCount} fields={summary.FieldCount} " +
                              $"strings={inspector.Strings.Count} refs={summary.AssemblyRefCount} " +
                              $"resources={summary.ResourceCount} attrs={summary.CustomAttributeCount}");
            Console.WriteLine($"  sha256={summary.Sha256} size={summary.SizeLabel}");
            Console.WriteLine($"  mvid={summary.Mvid ?? "?"} target={summary.TargetFramework ?? "?"} " +
                              $"entry={summary.EntryPoint ?? "-"}");
            Console.WriteLine($"  version={summary.Version} culture={(summary.Culture.Length == 0 ? "neutral" : summary.Culture)}");
            if (summary.CompileTimestamp != null)
                Console.WriteLine($"  compiled={summary.CompileTimestamp:yyyy-MM-dd HH:mm:ss} (local)");
            if (summary.HasDiagnostics)
                Console.WriteLine("  ! diagnostics: " + summary.Diagnostics);

            foreach (ProtectionHint hint in summary.Protections.Take(12))
                Console.WriteLine($"  ! {hint.SeverityLabel,-10} {hint.Name}: {hint.Evidence}");

            if (args.Has("--strings"))
            {
                Console.WriteLine();
                Console.WriteLine("--- user strings ---");
                foreach (StringEntry text in inspector.Strings.Where(s => s.Kind == "user").Take(2000))
                    Console.WriteLine($"  [{text.Count,3}x] {text.Display}");
            }

            string? typeName = args.Value("--type");
            if (typeName == null)
            {
                Console.WriteLine();
                foreach (TypeEntry type in inspector.Types.Take(20))
                    Console.WriteLine($"    {type.FullName}  [{type.KindLabel}, {type.MethodCount} methods]");
                if (inspector.Types.Count > 20)
                    Console.WriteLine($"    ... {inspector.Types.Count - 20} more type(s) (use --type or --json)");
                return ExitOk;
            }

            TypeEntry? match = inspector.Types.FirstOrDefault(t =>
                t.FullName.Equals(typeName, StringComparison.Ordinal) ||
                t.FullName.EndsWith("." + typeName, StringComparison.Ordinal) ||
                t.Name.Equals(typeName, StringComparison.Ordinal));
            if (match == null)
            {
                Console.Error.WriteLine("type not found: " + typeName);
                return ExitFailure;
            }

            if (args.Has("--il"))
            {
                Console.WriteLine(inspector.DisassembleType(match));
                MethodEntry? first = match.Methods.FirstOrDefault(m => m.HasBody);
                if (first != null)
                {
                    Console.WriteLine("--- single method: " + first.Display + " ---");
                    Console.WriteLine(inspector.Disassemble(first));
                }
                return ExitOk;
            }

            Console.WriteLine("--- decompiled ---");
            Console.WriteLine(inspector.DecompileType(match));
            return ExitOk;
        }
    }

    private static async Task<int> RunDeepAsync(ArgReader args)
    {
        int pid = args.Int("--pid");
        if (pid <= 0)
            throw new UsageException("missing required option --pid <n> (or --pid self)");

        string output = args.Value("--out") ?? DumpOptions.DefaultOutputDirectory();
        bool json = args.Has("--json");

        using TargetSession? session = TargetSession.Attach(pid, out string? error);
        if (session == null)
        {
            Console.Error.WriteLine("attach failed: " + error);
            return ExitFailure;
        }

        (CancellationTokenSource cts, ConsoleCancelEventHandler handler) = BeginCancellable("deep dump");
        try
        {
            if (!json)
                Console.WriteLine($"# deep dump: {session.Process.Name} (pid {pid}) " +
                                  $"runtime={session.Process.RuntimeLabel} -> {output}");

            var dumper = new DeepDumper(Log);
            var progress = new Progress<string>(line =>
            {
                if (!json)
                    Console.WriteLine("  " + line);
            });
            DeepDumpResult result = await dumper.RunAsync(session,
                new DumpOptions { OutputDirectory = output }, progress, cts.Token).ConfigureAwait(false);

            if (json)
            {
                WriteJson(result);
            }
            else
            {
                Console.WriteLine($"# status={result.Status} ({result.DurationMs} ms)");
                Console.WriteLine("  " + result.Message);
                foreach (DeepDumpedFile file in result.Files)
                    Console.WriteLine($"  + {Path.GetFileName(file.Path),-40} {file.Bytes,10:N0} B  {file.Kind}");
                foreach (string note in result.Notes)
                    Console.WriteLine("  note: " + note);
            }

            return result.Success ? ExitOk : ExitFailure;
        }
        finally
        {
            EndCancellable(cts, handler);
        }
    }

    private static int RunToken(ArgReader args)
    {
        int pid = args.Int("--pid");
        if (pid <= 0)
            throw new UsageException("missing required option --pid <n> (or --pid self)");
        Console.WriteLine(EngineDiagnostics.ProbeToken(pid));
        return ExitOk;
    }

    private static int RunList(ArgReader args)
    {
        bool managedOnly = args.Has("--managed");
        bool json = args.Has("--json");

        var stopwatch = Stopwatch.StartNew();
        var processes = ProcessDiscovery.Enumerate();
        if (managedOnly)
            processes = processes.Where(p => p.IsManaged).ToList();

        if (json)
        {
            WriteJson(processes);
            return ExitOk;
        }

        Console.WriteLine($"# {processes.Count} process(es) in {stopwatch.ElapsedMilliseconds} ms");

        Console.WriteLine($"{"PID",-7}{"ARCH",-6}{"RUNTIME",-26}{"SESSION",-8}NAME");
        foreach (ProcessRecord process in processes
                     .OrderByDescending(p => p.IsManaged)
                     .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"{process.Id,-7}{process.ArchitectureLabel,-6}{process.RuntimeLabel,-26}" +
                              $"{process.SessionId,-8}{process.Name}");
        }

        return ExitOk;
    }

    private static int RunInfo(ArgReader args)
    {
        int pid = args.Int("--pid");
        if (pid <= 0)
            throw new UsageException("missing required option --pid <n> (or --pid self)");

        using TargetSession? session = TargetSession.Attach(pid, out string? error);
        if (session == null)
        {
            Console.Error.WriteLine("attach failed: " + error);
            return ExitFailure;
        }

        if (args.Has("--json"))
        {
            WriteJson(new { process = session.Process, modules = session.Modules });
            return ExitOk;
        }

        ProcessRecord process = session.Process;
        Console.WriteLine($"{process.Name}  (pid {process.Id})");
        Console.WriteLine($"  architecture : {process.ArchitectureLabel}{(process.IsWow64 ? " (WOW64)" : "")}");
        Console.WriteLine($"  runtime      : {process.RuntimeLabel}");
        Console.WriteLine($"  integrity    : {process.IntegrityLabel}{(process.IsElevated ? " (elevated)" : "")}");
        Console.WriteLine($"  session      : {process.SessionId}");
        Console.WriteLine($"  image        : {process.ImagePath ?? "?"}");
        if (process.CommandLine != null)
            Console.WriteLine($"  command line : {process.CommandLine}");
        Console.WriteLine($"  modules      : {session.Modules.Count} ({session.ManagedModules.Count} managed)");

        PrintModuleTable(session.Modules);
        return ExitOk;
    }

    private static int RunModules(ArgReader args)
    {
        int pid = args.Int("--pid");
        if (pid <= 0)
            throw new UsageException("missing required option --pid <n> (or --pid self)");

        using TargetSession? session = TargetSession.Attach(pid, out string? error);
        if (session == null)
        {
            Console.Error.WriteLine("attach failed: " + error);
            return ExitFailure;
        }

        var modules = session.Modules;
        if (args.Has("--managed"))
            modules = modules.Where(m => m.IsManaged).ToList();

        if (args.Has("--json"))
        {
            WriteJson(modules);
            return ExitOk;
        }

        Console.WriteLine($"# {session.Process.Name} (pid {pid}): {modules.Count} module(s)");
        PrintModuleTable(modules);
        return ExitOk;
    }

    private static void PrintModuleTable(IReadOnlyList<ModuleRecord> modules)
    {
        Console.WriteLine();
        Console.WriteLine($"{"MANAGED",-8}{"BASE",-16}{"SIZE",-10}{"MSIL",-8}MODULE");
        foreach (ModuleRecord module in modules
                     .OrderByDescending(m => m.IsManaged)
                     .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            // Two spaces keep the address and the size readable as separate columns.
            Console.WriteLine($"{(module.IsManaged ? "yes" : "-"),-8}0x{module.BaseAddress:X12}  {module.SizeLabel,-10}" +
                              $"{module.MetadataLabel,-8}{module.Name}");
        }
    }

    private static async Task<int> RunDumpAsync(ArgReader args)
    {
        int pid = args.Int("--pid");
        if (pid <= 0)
            throw new UsageException("missing required option --pid <n> (or --pid self)");

        string output = args.Value("--out") ?? DumpOptions.DefaultOutputDirectory();
        var options = new DumpOptions
        {
            OutputDirectory = output,
            IncludeFrameworkModules = args.Has("--include-framework"),
            DumpNativeModules = args.Has("--native"),
            RawImage = args.Has("--raw"),
            StripBoundImports = !args.Has("--keep-bound-imports"),
            OverwritePolicy = ParseOverwritePolicy(args),
        };
        bool json = args.Has("--json");
        bool interactive = !Console.IsOutputRedirected;

        using TargetSession? session = TargetSession.Attach(pid, out string? error);
        if (session == null)
        {
            Console.Error.WriteLine("attach failed: " + error);
            return ExitFailure;
        }

        string? name = args.Value("--name");
        List<ModuleRecord> targets;
        if (name != null)
        {
            targets = session.Modules
                .Where(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (targets.Count == 0)
            {
                Console.Error.WriteLine($"module not found: {name}");
                return ExitFailure;
            }
            options.IncludeFrameworkModules = true;
            options.DumpNativeModules = true;
        }
        else if (args.Has("--all"))
        {
            targets = session.Modules
                .Where(m => m.BaseAddress != 0)
                .Where(m => options.DumpNativeModules || m.IsManaged)
                .Where(m => options.IncludeFrameworkModules || !m.IsFramework)
                .ToList();
        }
        else
        {
            throw new UsageException("specify --all or --name <module.dll>");
        }

        if (targets.Count == 0)
        {
            Console.Error.WriteLine("no modules matched the selection (try --include-framework / --native).");
            return ExitFailure;
        }

        (CancellationTokenSource cts, ConsoleCancelEventHandler handler) = BeginCancellable("dump");
        try
        {
            if (!json)
                Console.WriteLine($"# {session.Process.Name} (pid {pid}) -> {targets.Count} module(s) -> " +
                                  Path.GetFullPath(output));

            Log.EntryWritten += entry =>
            {
                if (!json)
                    Console.WriteLine("  " + entry);
            };

            var dumper = new ModuleDumper(session, Log);
            int lastLength = 0;
            var progress = new Progress<DumpProgress>(p =>
            {
                if (json)
                    return;
                string line = $"[{p.Completed}/{p.Total}] {p.Current}";
                if (interactive)
                {
                    // Erase the previous (possibly longer) progress line.
                    Console.Write('\r' + new string(' ', Math.Max(lastLength, line.Length + 2)) + '\r');
                    Console.Write("  " + line);
                    lastLength = line.Length + 2;
                }
                else
                {
                    Console.WriteLine("  " + line);
                }
            });

            IReadOnlyList<DumpResult> results = await dumper
                .DumpAsync(targets, options, progress, cts.Token)
                .ConfigureAwait(false);

            if (!json)
            {
                if (interactive)
                    Console.Write('\r' + new string(' ', lastLength + 2) + '\r');
                else
                    Console.WriteLine();
            }
            int ok = results.Count(r => r.Success);
            List<DumpResult> failed = results.Where(r => !r.Success).ToList();

            if (json)
            {
                WriteJson(new
                {
                    process = session.Process,
                    outputDirectory = Path.GetFullPath(output),
                    dumped = ok,
                    failed = failed.Count,
                    results,
                });
            }
            else
            {
                Console.WriteLine($"# dumped {ok}/{results.Count}");
                foreach (DumpResult failure in failed)
                    Console.WriteLine($"  failed: {failure.Module.Name}: {failure.Error}");
            }

            return failed.Count == 0 ? ExitOk : ExitFailure;
        }
        finally
        {
            EndCancellable(cts, handler);
        }
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"unknown command '{command}'.");
        Console.Error.WriteLine("Run 'obsidian help' for the command list.");
        return ExitUsage;
    }

    // ---------------------------------------------------------------- injection

    private static async Task<int> RunInjectAsync(ArgReader args)
    {
        int pid = args.Int("--pid");
        if (pid <= 0)
            throw new UsageException("missing required option --pid <n> (or --pid self)");

        string? dllPath = args.Value("--dll");
        if (string.IsNullOrWhiteSpace(dllPath))
            throw new UsageException("missing required option --dll <path>");

        InjectionMethod method = ParseInjectionMethod(args.Value("--method"));
        bool json = args.Has("--json");

        if (!File.Exists(dllPath))
        {
            Console.Error.WriteLine("DLL not found: " + dllPath);
            return ExitFailure;
        }

        // Report wrong-bitness / missing-file problems before anything is allocated.
        if (!DllInjector.IsCompatible(pid, dllPath, out string? incompatibility))
        {
            Console.Error.WriteLine("cannot inject: " + (incompatibility ?? "the target and DLL are not compatible"));
            return ExitFailure;
        }

        (CancellationTokenSource cts, ConsoleCancelEventHandler handler) = BeginCancellable("injection");
        try
        {
            var injector = new DllInjector(Log);
            InjectionResult result = await Task.Run(
                () => injector.Inject(pid, dllPath, method, cts.Token), cts.Token).ConfigureAwait(false);

            if (json)
            {
                WriteJson(new { action = "inject", pid, dll = Path.GetFullPath(dllPath), result });
            }
            else
            {
                PrintInjectionResult("injection", result);
                if (!result.Success)
                    Console.Error.WriteLine("injection failed: " + result.Message);
            }

            if (result.Success)
                return ExitOk;
            return result.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase) ? ExitCancelled : ExitFailure;
        }
        finally
        {
            EndCancellable(cts, handler);
        }
    }

    private static async Task<int> RunUnloadAsync(ArgReader args)
    {
        int pid = args.Int("--pid");
        if (pid <= 0)
            throw new UsageException("missing required option --pid <n> (or --pid self)");

        string? module = args.Value("--module") ?? args.Value("--name");
        if (string.IsNullOrWhiteSpace(module))
            throw new UsageException("missing required option --module <name|path>");

        bool json = args.Has("--json");
        (CancellationTokenSource cts, ConsoleCancelEventHandler handler) = BeginCancellable("unload");
        try
        {
            var injector = new DllInjector(Log);
            InjectionResult result = await Task.Run(
                () => injector.Unload(pid, module, cts.Token), cts.Token).ConfigureAwait(false);

            if (json)
            {
                WriteJson(new { action = "unload", pid, module, result });
            }
            else
            {
                PrintInjectionResult("unload", result);
                if (!result.Success)
                    Console.Error.WriteLine("unload failed: " + result.Message);
            }

            if (result.Success)
                return ExitOk;
            return result.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase) ? ExitCancelled : ExitFailure;
        }
        finally
        {
            EndCancellable(cts, handler);
        }
    }

    private static InjectionMethod ParseInjectionMethod(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return InjectionMethod.CreateRemoteThread;

        return value.Trim().ToLowerInvariant() switch
        {
            "crt" or "createremotethread" or "create-remote-thread" => InjectionMethod.CreateRemoteThread,
            "ntcreate" or "ntcreatethread" or "ntcreatethreadex" => InjectionMethod.NtCreateThreadEx,
            "apc" or "queueuserapc" or "queue-user-apc" => InjectionMethod.QueueUserApc,
            _ => throw new UsageException(
                $"unknown injection method '{value}' (expected crt, ntcreate or apc)"),
        };
    }

    private static void PrintInjectionResult(string label, InjectionResult result)
    {
        Console.WriteLine($"{label}: {(result.Success ? "ok" : "failed")}");
        Console.WriteLine($"  method : {result.Method}");
        if (result.RemoteModuleBase != 0)
            Console.WriteLine($"  base   : 0x{result.RemoteModuleBase:X}");
        Console.WriteLine($"  time   : {result.DurationMs} ms");
        if (result.Win32Error != 0)
            Console.WriteLine($"  win32  : {result.Win32Error} ({DescribeWin32(result.Win32Error)})");
        if (result.NtStatus != 0)
            Console.WriteLine($"  status : 0x{result.NtStatus:X8}");
        foreach (string note in result.Notes)
            Console.WriteLine("  note   : " + note);
    }

    private static string DescribeWin32(uint error)
    {
        try
        {
            return new System.ComponentModel.Win32Exception((int)error).Message;
        }
        catch
        {
            return "Win32 error";
        }
    }

    private static DumpOverwritePolicy ParseOverwritePolicy(ArgReader args)
    {
        string? value = args.Value("--on-conflict");
        if (value != null)
        {
            return value.Trim().ToLowerInvariant() switch
            {
                "fail" => DumpOverwritePolicy.Fail,
                "rename" => DumpOverwritePolicy.Rename,
                "overwrite" or "replace" => DumpOverwritePolicy.Overwrite,
                _ => throw new UsageException(
                    $"unknown conflict policy '{value}' (expected fail, rename or overwrite)"),
            };
        }

        if (args.Has("--overwrite"))
            return DumpOverwritePolicy.Overwrite;
        if (args.Has("--fail-if-exists"))
            return DumpOverwritePolicy.Fail;
        return DumpOverwritePolicy.Rename;
    }

    // ---------------------------------------------------------------- help

    private static void PrintHelp()
    {
        Console.WriteLine($"OBSIDIAN {Version} - headless .NET dumper (CLI)");
        Console.WriteLine();
        Console.WriteLine("usage: obsidian <command> [options]");
        Console.WriteLine();
        Console.WriteLine("commands:");
        WriteHelpRow("list", "list running processes (managed first)");
        WriteHelpRow("info", "one process: identity, runtime, modules");
        WriteHelpRow("modules", "loaded modules of one process");
        WriteHelpRow("dump", "dump one or all modules of a process to disk");
        WriteHelpRow("deep", "deep dump through the in-memory managed agent (.NET Framework x64)");
        WriteHelpRow("inspect", "analyse a managed file: identity, strings, protections, C#/IL");
        WriteHelpRow("inject", "load a DLL into a live process (CRT / NtCreateThreadEx / APC)");
        WriteHelpRow("unload", "unload a previously injected module from a process");
        WriteHelpRow("token", "integrity / token facts of one process");
        WriteHelpRow("diag", "engine diagnostics (probes, enumeration)");
        WriteHelpRow("readtest", "read a module's memory page by page (support)");
        Console.WriteLine();
        Console.WriteLine("common options:");
        WriteHelpRow("--json", "machine-readable output (list/info/modules/dump/deep/inspect/inject/unload)");
        WriteHelpRow("-h, --help", "this help, or 'obsidian help <command>'");
        WriteHelpRow("--verbose", "print exception stack traces");
        Console.WriteLine();
        Console.WriteLine("examples:");
        Console.WriteLine("  obsidian list --managed");
        Console.WriteLine("  obsidian info --pid 1234");
        Console.WriteLine("  obsidian modules --pid 1234 --json");
        Console.WriteLine("  obsidian dump --pid 1234 --all --out C:\\dumps");
        Console.WriteLine("  obsidian dump --pid 1234 --name Client.dll");
        Console.WriteLine("  obsidian deep --pid 1234 --out C:\\dumps");
        Console.WriteLine("  obsidian inspect C:\\dumps\\Client.dll --type LicenseEngine");
        Console.WriteLine("  obsidian inspect C:\\dumps\\Client.dll --type LicenseEngine --il --strings");
        Console.WriteLine("  obsidian inject --pid 1234 --dll C:\\tools\\probe.dll");
        Console.WriteLine("  obsidian inject --pid 1234 --dll C:\\tools\\probe.dll --method apc --json");
        Console.WriteLine("  obsidian unload --pid 1234 --module probe.dll");
        Console.WriteLine();
        Console.WriteLine("exit codes: 0 success | 1 usage error | 2 operation failed | 130 cancelled");
        Console.WriteLine();
        Console.WriteLine("Use only on processes and files you are authorised to analyse.");
    }

    private static void WriteHelpRow(string name, string description)
        => Console.WriteLine($"  {name,-22}{description}");

    private static int PrintCommandHelp(string command)
    {
        string? usage = command switch
        {
            "list" => "obsidian list [--managed] [--json]",
            "info" => "obsidian info --pid <n|self> [--json]",
            "modules" => "obsidian modules --pid <n|self> [--managed] [--json]",
            "dump" => "obsidian dump --pid <n|self> (--all | --name <module.dll>) [--out <dir>]\n" +
                      "               [--include-framework] [--native] [--raw] [--overwrite | --fail-if-exists |\n" +
                      "               --on-conflict fail|rename|overwrite] [--keep-bound-imports] [--json]",
            "deep" => "obsidian deep --pid <n|self> [--out <dir>] [--json]",
            "inspect" => "obsidian inspect <assembly> [--type <name>] [--il] [--strings] [--json]",
            "inject" => "obsidian inject --pid <n|self> --dll <path> [--method crt|ntcreate|apc] [--json]",
            "unload" => "obsidian unload --pid <n|self> --module <name|path> [--json]",
            "token" => "obsidian token --pid <n|self>",
            "diag" => "obsidian diag",
            "readtest" => "obsidian readtest --pid <n|self> --name <module.dll>",
            _ => null,
        };

        if (usage == null)
        {
            Console.Error.WriteLine($"unknown command '{command}'.");
            Console.Error.WriteLine("Run 'obsidian help' for the command list.");
            return ExitUsage;
        }

        Console.WriteLine($"usage: {usage}");
        return ExitOk;
    }

    // ---------------------------------------------------------------- helpers

    private static void WriteJson<T>(T value)
        => Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    private static (CancellationTokenSource Cts, ConsoleCancelEventHandler Handler) BeginCancellable(string operation)
    {
        var cts = new CancellationTokenSource();
        int presses = 0;
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            presses++;
            if (presses == 1)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"cancelling {operation}... (press Ctrl+C again to exit immediately)");
                cts.Cancel();
            }
            else
            {
                Console.Error.WriteLine("aborted.");
                Environment.Exit(ExitCancelled);
            }
        };
        Console.CancelKeyPress += handler;
        return (cts, handler);
    }

    private static void EndCancellable(CancellationTokenSource cts, ConsoleCancelEventHandler handler)
    {
        Console.CancelKeyPress -= handler;
        cts.Dispose();
    }
}

/// <summary>Raised for invalid command lines; maps to exit code 1.</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>
/// Small, strict command-line reader. Unknown options are reported instead of
/// being silently ignored (a typo used to change behaviour without warning).
/// Supports <c>--option value</c>, <c>--option=value</c> and positionals.
/// </summary>
internal sealed class ArgReader
{
    private const string HelpText = "Run 'obsidian help' for usage.";

    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--pid", "--out", "--name", "--type", "--file", "--dll", "--module", "--method", "--on-conflict",
    };

    private static readonly HashSet<string> FlagOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--all", "--managed", "--json", "--native", "--raw", "--overwrite", "--include-framework",
        "--il", "--strings", "--verbose", "--fail-if-exists", "--keep-bound-imports",
    };

    private readonly List<string> _positionals = new();
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public static ArgReader Parse(string[] args)
    {
        var reader = new ArgReader();
        for (int i = 1; i < args.Length; i++)
        {
            string token = args[i];
            if (token == "--")
            {
                for (int j = i + 1; j < args.Length; j++)
                    reader._positionals.Add(args[j]);
                break;
            }

            if (token.Length == 0 || token[0] != '-' || token == "-")
            {
                reader._positionals.Add(token);
                continue;
            }

            string name = token;
            string? inlineValue = null;
            int equals = token.IndexOf('=');
            if (equals > 0)
            {
                name = token[..equals];
                inlineValue = token[(equals + 1)..];
            }

            if (name == "-h")
                name = "--help";
            if (name == "-v")
                name = "--verbose";

            if (ValueOptions.Contains(name))
            {
                string? value = inlineValue;
                if (value == null)
                {
                    if (i + 1 >= args.Length || (args[i + 1].StartsWith('-') && !IsNegativeNumber(args[i + 1])))
                        throw new UsageException($"option '{name}' requires a value. {HelpText}");
                    value = args[++i];
                }
                reader._options[name] = value;
            }
            else if (FlagOptions.Contains(name))
            {
                if (inlineValue != null)
                    throw new UsageException($"option '{name}' does not take a value. {HelpText}");
                reader._flags.Add(name);
            }
            else
            {
                throw new UsageException($"unknown option '{name}'. {HelpText}");
            }
        }

        return reader;
    }

    private static bool IsNegativeNumber(string token)
        => token.Length > 1 && token[0] == '-' && token.Skip(1).All(c => char.IsDigit(c));

    public bool Has(string name) => _flags.Contains(name) || _options.ContainsKey(name);

    public string? Value(string name)
        => _options.TryGetValue(name, out string? value) ? value : null;

    public int Int(string name)
    {
        string? value = Value(name);
        if (value == null)
            return -1;
        if (value.Equals("self", StringComparison.OrdinalIgnoreCase))
            return Environment.ProcessId;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : -1;
    }

    public string? Positional(int index) => index < _positionals.Count ? _positionals[index] : null;
}
