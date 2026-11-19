using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using dnlib.DotNet;
using Obsidian.Dumper.Core;
using Obsidian.Dumper.Core.Diagnostics;
using Obsidian.Dumper.Core.Dumping;
using Obsidian.Dumper.Core.Injection;
using Obsidian.Dumper.Core.Inspection;
using Obsidian.Dumper.Core.Model;
using Obsidian.Dumper.Shared;

namespace Obsidian.Dumper.Tests;

/// <summary>
/// Self-contained verification harness (no test framework dependency, runs with
/// `dotnet run`). Exercises the real engine against a real .NET process and
/// checks every artefact with independent readers (dnlib, ILSpy).
///
/// Optional argument: --filter &lt;text&gt; runs only the test groups whose name
/// contains the text, which keeps the harness usable while iterating.
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;
    private static int _skippedGroups;

    private static readonly string SelfAssemblyPath =
        Path.Combine(AppContext.BaseDirectory, "Obsidian.Dumper.Tests.dll");

    private static readonly List<(string Name, Func<Task> Run)> Groups =
    [
        ("PE rebuild round-trip", Sync(TestPeRebuildRoundTrip)),
        ("Inspector metadata", Sync(TestInspectorMetadata)),
        ("IL formatter", Sync(TestIlFormatter)),
        ("Protection heuristics", Sync(TestProtectionHeuristics)),
        ("Decompiler", Sync(TestDecompiler)),
        ("Malformed inputs", Sync(TestMalformedInputs)),
        ("CLI surface", TestCliSurface),
        ("Live injection + process control", TestInjectionAsync),
        ("Live dump of a running .NET 10 process", TestLiveDumpAsync),
        ("Agent asset staging", Sync(TestAgentAssets)),
    ];

    private static Func<Task> Sync(Action action)
        => () =>
        {
            action();
            return Task.CompletedTask;
        };

    private static async Task<int> Main(string[] args)
    {
        Console.WriteLine("OBSIDIAN engine verification");
        Console.WriteLine("============================");

        string? filter = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--filter" or "-f" && i + 1 < args.Length)
                filter = args[++i];
        }

        var stopwatch = Stopwatch.StartNew();
        foreach ((string name, Func<Task> run) in Groups)
        {
            if (filter != null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                _skippedGroups++;
                continue;
            }

            Console.WriteLine();
            Console.WriteLine("- " + name);
            try
            {
                await run().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Fail("unhandled exception", ex.ToString());
            }
        }

        Console.WriteLine();
        string skipped = _skippedGroups > 0 ? $", {_skippedGroups} group(s) skipped by filter" : string.Empty;
        Console.WriteLine($"{_passed} passed, {_failed} failed ({stopwatch.ElapsedMilliseconds} ms{skipped})");
        return _failed == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- PE rebuild
    private static void TestPeRebuildRoundTrip()
    {
        byte[] file = File.ReadAllBytes(SelfAssemblyPath);

        byte[] mapped = MapFileToImage(file);
        Check("mapped image is larger than the file", mapped.Length >= file.Length);
        Check("mapped image starts with MZ", mapped[0] == (byte)'M' && mapped[1] == (byte)'Z');

        byte[]? rebuilt = PeImageLayout.Rebuild(mapped, PeRebuildOptions.Default, out string? error);
        Check("rebuild succeeds", rebuilt != null, error);
        if (rebuilt == null)
            return;

        Check("rebuilt starts with MZ", rebuilt[0] == (byte)'M' && rebuilt[1] == (byte)'Z');

        using ModuleDefMD original = ModuleDefMD.Load(file);
        using ModuleDefMD roundTrip = ModuleDefMD.Load(rebuilt);

        int originalTypes = original.GetTypes().Count();
        int rebuiltTypes = roundTrip.GetTypes().Count();
        Check($"type count preserved ({originalTypes})", originalTypes == rebuiltTypes,
            $"original={originalTypes} rebuilt={rebuiltTypes}");

        int originalMethods = original.GetTypes().Sum(t => t.Methods.Count);
        int rebuiltMethods = roundTrip.GetTypes().Sum(t => t.Methods.Count);
        Check($"method count preserved ({originalMethods})", originalMethods == rebuiltMethods,
            $"original={originalMethods} rebuilt={rebuiltMethods}");

        bool metadataRoot = PeImageLayout.TryGetMetadataRootFromFile(rebuilt, out MetadataRoot root, out _);
        Check("rebuilt image carries BSJB metadata", metadataRoot && root.RuntimeVersion.StartsWith('v'),
            root.RuntimeVersion ?? "(no root)");

        bool mappedRoot = PeImageLayout.TryGetMetadataRoot(mapped, out MetadataRoot mappedMetadata, out _);
        Check("mapped image carries BSJB metadata", mappedRoot && mappedMetadata.Streams.Length > 0,
            mappedMetadata.RuntimeVersion ?? "(no root)");
    }

    /// <summary>Converts a file image to the mapped representation the loader creates.</summary>
    private static byte[] MapFileToImage(byte[] file)
    {
        if (!PeImageLayout.TryReadHeaders(file, out PeHeaders headers))
            throw new InvalidOperationException("test assembly is not a PE file");

        var mapped = new byte[headers.SizeOfImage];
        Buffer.BlockCopy(file, 0, mapped, 0, (int)Math.Min(headers.SizeOfHeaders, (uint)file.Length));

        for (int i = 0; i < headers.SectionCount; i++)
        {
            PeSection section = headers.Sections[i];
            if (section.RawSize == 0 || section.RawPointer >= file.Length)
                continue;

            int available = (int)Math.Min(section.RawSize, (uint)(file.Length - (int)section.RawPointer));
            available = Math.Min(available, mapped.Length - (int)section.VirtualAddress);
            if (available > 0)
                Buffer.BlockCopy(file, (int)section.RawPointer, mapped, (int)section.VirtualAddress, available);
        }

        return mapped;
    }

    // ---------------------------------------------------------------- inspector
    private static void TestInspectorMetadata()
    {
        Check("self assembly exists", File.Exists(SelfAssemblyPath), SelfAssemblyPath);
        using var inspector = AssemblyInspector.Open(SelfAssemblyPath);
        AssemblySummary summary = inspector.Summary;

        Check("identity is present", !string.IsNullOrWhiteSpace(summary.Name), summary.Name);
        Check("full name is present", summary.FullName.Contains("Obsidian.Dumper.Tests"), summary.FullName);
        Check("kind is a plausible PE kind", summary.Kind is "DLL" or "EXE", summary.Kind);
        Check("machine is x64", summary.Machine == "x64", summary.Machine);
        Check("module name is present", summary.ModuleName.Length > 0, summary.ModuleName);
        Check("metadata runtime version parsed", summary.RuntimeVersion.StartsWith('v'), summary.RuntimeVersion);
        Check("target framework parsed", summary.TargetFramework?.Contains(".NETCoreApp") == true,
            summary.TargetFramework ?? "(null)");
        Check("mvid is a full guid", Guid.TryParse(summary.Mvid, out _), summary.Mvid ?? "(null)");
        Check("sha256 is 64 hex chars", summary.Sha256.Length == 64 && IsHex(summary.Sha256), summary.Sha256);
        Check("file size matches", summary.FileSize == new FileInfo(SelfAssemblyPath).Length,
            $"{summary.FileSize} vs {new FileInfo(SelfAssemblyPath).Length}");
        Check("IL-only flag detected", summary.IsIlOnly && !summary.IsMixedMode,
            $"ilonly={summary.IsIlOnly} mixed={summary.IsMixedMode}");
        Check("not detected as ready-to-run", !summary.IsReadyToRun);
        Check("not detected as strong named", !summary.IsStrongNamed);
        Check("no diagnostics on a healthy assembly", !summary.HasDiagnostics, summary.Diagnostics);
        Check("sections were parsed", summary.Sections.Count > 0, summary.Sections.Count.ToString());
        Check("section entropy stays below the encrypted threshold",
            summary.Sections.All(s => s.Entropy < 7.5),
            string.Join(", ", summary.Sections.Select(s => $"{s.Name}={s.Entropy:0.00}")));
        Check("assembly references parsed",
            summary.References.Any(r => r.Kind == "assembly" && r.Name == "System.Runtime"),
            string.Join(", ", summary.References.Where(r => r.Kind == "assembly").Select(r => r.Name)));
        Check("custom attributes counted", summary.CustomAttributeCount > 0,
            summary.CustomAttributeCount.ToString());
        Check("sorts and counts are consistent", summary.TypeCount == inspector.Types.Count,
            $"{summary.TypeCount} vs {inspector.Types.Count}");

        // Nested types must inherit the outer namespace (a previous defect left
        // them with an empty namespace and a separate bogus group).
        TypeEntry? nested = inspector.Types.FirstOrDefault(t => t.Name == "NestedProbe");
        Check("nested probe type is present", nested != null);
        if (nested != null)
        {
            Check("nested type inherits namespace", nested.Namespace == "Obsidian.Dumper.Tests",
                nested.Namespace.Length == 0 ? "(empty)" : nested.Namespace);
            Check("nested type reports nesting depth", nested.NestedLevel >= 1, nested.NestedLevel.ToString());
            Check("nested type full name uses '/' separator", nested.FullName.Contains('/'), nested.FullName);
        }

        TypeEntry? program = inspector.Types.FirstOrDefault(t => t.Name == "Program" && t.Methods.Count > 0);
        Check("test program type is present", program != null);
        TypeEntry? fixtures = inspector.Types.FirstOrDefault(t => t.Name == "IlFixtures");
        Check("IL fixture type is present", fixtures != null);
        if (fixtures != null)
        {
            Check("nested method total is recursive",
                fixtures.TotalMethodCount >= fixtures.MethodCount + (nested?.MethodCount ?? 0),
                $"total={fixtures.TotalMethodCount} direct={fixtures.MethodCount} nested={nested?.MethodCount}");
        }
        if (program != null)
        {
            Check("method formatting includes signature",
                program.Methods.Any(m => m.Signature.Contains('(')),
                program.Methods.FirstOrDefault()?.Signature ?? "(none)");
        }

        // String inventory: user literals AND metadata names (metadata names used
        // to be collected after the snapshot was built and never appeared).
        Check("user string literals are collected",
            inspector.Strings.Any(s => s.Kind == "user" && s.Value.Contains("OBSIDIAN-TEST-LITERAL")),
            "marker literal missing");
        Check("metadata names are collected",
            inspector.Strings.Any(s => s.Kind == "metadata" && s.Value == MetadataOnlyProbe()),
            "metadata string missing");
        Check("string usage counts are present",
            inspector.Strings.All(s => s.Count >= 1),
            "count < 1");
        Check("duplicate literals are deduped",
            inspector.Strings.Select(s => s.Value).Distinct(StringComparer.Ordinal).Count() == inspector.Strings.Count,
            "duplicate string entries");
        Check("string display escapes control characters",
            new StringEntry { Value = "a\tb\r\nc\\d" }.Display == "a\\tb\\r\\nc\\\\d",
            new StringEntry { Value = "a\tb\r\nc\\d" }.Display);

        Check("FormatBytes 0", AssemblySummary.FormatBytes(0) == "0 B", AssemblySummary.FormatBytes(0));
        Check("FormatBytes 1024", AssemblySummary.FormatBytes(1024) == "1 KB", AssemblySummary.FormatBytes(1024));
        Check("FormatBytes 1536", AssemblySummary.FormatBytes(1536) == "1.5 KB", AssemblySummary.FormatBytes(1536));
        Check("FormatBytes negative", AssemblySummary.FormatBytes(-2048) == "-2 KB",
            AssemblySummary.FormatBytes(-2048));
        Check("FormatBytes terabyte", AssemblySummary.FormatBytes(1024L * 1024 * 1024 * 1024) == "1 TB",
            AssemblySummary.FormatBytes(1024L * 1024 * 1024 * 1024));

        Check("IsManagedFile accepts a managed assembly", AssemblyInspector.IsManagedFile(SelfAssemblyPath));

        // A real class library (the engine) must be classified as DLL; the
        // harness itself is an Exe assembly and reports EXE.
        string coreDll = Path.Combine(AppContext.BaseDirectory, "Obsidian.Dumper.Core.dll");
        Check("engine library is present next to the harness", File.Exists(coreDll), coreDll);
        if (File.Exists(coreDll))
        {
            using var core = AssemblyInspector.Open(coreDll);
            Check("class library classified as DLL", core.Summary.Kind == "DLL", core.Summary.Kind);
            Check("class library exposes types", core.Summary.TypeCount > 10, core.Summary.TypeCount.ToString());
        }
    }

    // ---------------------------------------------------------------- IL formatter
    private static void TestIlFormatter()
    {
        using var inspector = AssemblyInspector.Open(SelfAssemblyPath);
        List<MethodEntry> allMethods = inspector.Types.SelectMany(t => t.Methods).ToList();

        MethodEntry? switchProbe = allMethods.FirstOrDefault(m => m.Name == "SwitchProbe");
        Check("switch probe method found", switchProbe != null);
        if (switchProbe != null)
        {
            string il = inspector.Disassemble(switchProbe);
            Check("disassembly contains instructions", il.Contains("IL_"), Head(il));
            Check("switch opcode is formatted", il.Contains("switch"), Head(il));
            Check("switch targets are labelled",
                Regex.IsMatch(il, @"switch\s+\(IL_[0-9A-F]{4}, IL_[0-9A-F]{4}"),
                Head(il));
            Check("branch labels are emitted",
                Regex.IsMatch(il, @"^IL_[0-9A-F]{4}:", RegexOptions.Multiline),
                "no label lines");
            Check("locals are listed for methods with locals",
                !il.Contains(".locals init") || il.Contains("] "),
                Head(il));
        }

        MethodEntry? tryCatch = allMethods.FirstOrDefault(m => m.Name == "TryCatchProbe");
        Check("try/catch probe method found", tryCatch != null);
        if (tryCatch != null)
        {
            string il = inspector.Disassemble(tryCatch);
            Check("exception handlers are reported", il.Contains("// EH"), Head(il));
            Check("finally handler is reported", il.Contains("Finally"), Head(il));
            Check("catch type is resolved", il.Contains("System.NullReferenceException"), Head(il));
            Check("locals declare init block", il.Contains(".locals init ("), Head(il));
        }

        MethodEntry? longLiteral = allMethods.FirstOrDefault(m => m.Name == "LongLiteral");
        Check("long literal method found", longLiteral != null);
        if (longLiteral != null)
        {
            string il = inspector.Disassemble(longLiteral);
            Check("long literals are truncated", il.Contains('…'), Head(il));
            Check("quotes are escaped", il.Contains("\\\""), Head(il));
            Check("newlines are escaped", il.Contains("\\n"), Head(il));
        }

        TypeEntry? nested = inspector.Types.FirstOrDefault(t => t.Name == "NestedProbe");
        Check("nested type found", nested != null);
        if (nested != null)
        {
            string listing = inspector.DisassembleType(nested);
            Check("type disassembly lists methods", listing.Contains("Echo") || listing.Contains("Value"), Head(listing));
        }

        Check("disassembly of an unknown method token is handled gracefully",
            inspector.Disassemble(new MethodEntry { Token = 0x7FFFFFFF }).Contains("not found"));
    }

    // ---------------------------------------------------------------- protections
    private static void TestProtectionHeuristics()
    {
        // Entropy sanity
        double entropy = AssemblyInspector.Entropy(new byte[] { 1, 2, 3, 4 });
        Check("entropy of unique bytes", Math.Abs(entropy - 2.0) < 0.001, entropy.ToString("0.000"));
        double flat = AssemblyInspector.Entropy(new byte[1024]);
        Check("entropy of a zero block is zero", flat == 0, flat.ToString("0.000"));
        var random = new Random(42);
        var noisy = new byte[4096];
        random.NextBytes(noisy);
        Check("entropy of random data is near 8", AssemblyInspector.Entropy(noisy) > 7.9,
            AssemblyInspector.Entropy(noisy).ToString("0.000"));

        using var inspector = AssemblyInspector.Open(SelfAssemblyPath);
        Check("clean assembly raises no suspicious/critical hints",
            inspector.Summary.Protections.All(p => p.Severity == ProtectionSeverity.Info),
            string.Join(", ", inspector.Summary.Protections.Select(p => $"{p.SeverityLabel}:{p.Name}")));

        using ModuleDefMD module = ModuleDefMD.Load(SelfAssemblyPath);
        var emptySections = Array.Empty<PeSectionInfo>();
        var emptyResources = Array.Empty<ResourceEntry>();

        // Known protector fingerprint + duplicate suppression.
        var protectorTypes = new List<TypeEntry>
        {
            new() { FullName = "Evil.ConfusedByAttribute" },
            new() { FullName = "Evil.ConfusedByIt" },
        };
        var hints = ProtectionAnalyzer.Analyze(module, protectorTypes, emptySections, emptyResources, EmptyStrings());
        Check("ConfuserEx fingerprint detected",
            hints.Any(h => h.Name == "ConfuserEx" && h.Severity == ProtectionSeverity.Suspicious),
            Describe(hints));
        Check("duplicate protector markers produce one hint",
            hints.Count(h => h.Name == "ConfuserEx") == 1, Describe(hints));

        // Generic words must not produce protector false positives.
        var benignTypes = new List<TypeEntry>
        {
            new() { FullName = "Acme.ReactorControl" },
            new() { FullName = "Acme.EnigmaMachine" },
            new() { FullName = "Acme.PoweredByAttribute" },
            new() { FullName = "Acme.BabelFish" },
        };
        hints = ProtectionAnalyzer.Analyze(module, benignTypes, emptySections, emptyResources, EmptyStrings());
        Check("generic words do not raise protector hints",
            hints.All(h => h.Severity == ProtectionSeverity.Info), Describe(hints));

        // Name mangling threshold.
        var mangledTypes = Enumerable.Range(0, 12)
            .Select(i => new TypeEntry { FullName = "\u4e2d\u6587" + i, IsObfuscated = true })
            .ToList();
        hints = ProtectionAnalyzer.Analyze(module, mangledTypes, emptySections, emptyResources, EmptyStrings());
        Check("name mangling detected",
            hints.Any(h => h.Name == "Name mangling" && h.Severity == ProtectionSeverity.Suspicious),
            Describe(hints));

        // Stripped bodies: runtime-implemented methods must not count.
        var strippedTypes = new List<TypeEntry>
        {
            new()
            {
                FullName = "A.B",
                Methods =
                [
                    new MethodEntry { Name = "M1", HasBody = false },
                    new MethodEntry { Name = "M2", HasBody = false },
                ],
            },
        };
        hints = ProtectionAnalyzer.Analyze(module, strippedTypes, emptySections, emptyResources, EmptyStrings());
        Check("stripped method bodies detected",
            hints.Any(h => h.Name == "Stripped method bodies"), Describe(hints));

        var runtimeTypes = new List<TypeEntry>
        {
            new()
            {
                FullName = "A.C",
                Methods = Enumerable.Range(0, 5)
                    .Select(i => new MethodEntry { Name = "R" + i, HasBody = false, IsRuntime = true })
                    .ToList(),
            },
        };
        hints = ProtectionAnalyzer.Analyze(module, runtimeTypes, emptySections, emptyResources, EmptyStrings());
        Check("runtime methods are not treated as stripped bodies",
            hints.All(h => h.Name != "Stripped method bodies"), Describe(hints));

        // Section heuristics: RWX and entropy severity calibration.
        var sections = new List<PeSectionInfo>
        {
            new() { Name = ".text", RawSize = 0x4000, Entropy = 7.9, Characteristics = 0xE0000020 },
            new() { Name = ".data", RawSize = 0x4000, Entropy = 7.9, Characteristics = 0x40000040 },
        };
        hints = ProtectionAnalyzer.Analyze(module, Array.Empty<TypeEntry>(), sections, emptyResources, EmptyStrings());
        Check("RWX section reported as suspicious",
            hints.Any(h => h.Name == "Writable + executable section" && h.Severity == ProtectionSeverity.Suspicious),
            Describe(hints));
        Check("executable high-entropy section is suspicious",
            hints.Any(h => h.Name == "High entropy section" && h.Severity == ProtectionSeverity.Suspicious),
            Describe(hints));
        Check("non-executable high-entropy section is only informational",
            hints.Any(h => h.Name == "High entropy section" && h.Severity == ProtectionSeverity.Info),
            Describe(hints));

        // Encrypted literal heuristics: prose must not trip it, ciphertext must.
        var prose = new Dictionary<string, (int Count, string Kind)>(StringComparer.Ordinal);
        for (int i = 0; i < 30; i++)
            prose[$"This is an ordinary sentence number {i} with spaces."] = (1, "user");
        hints = ProtectionAnalyzer.Analyze(module, Array.Empty<TypeEntry>(), emptySections, emptyResources, prose);
        Check("ordinary literals are not flagged as encrypted",
            hints.All(h => h.Name != "Encrypted string table"), Describe(hints));

        var cipher = new Dictionary<string, (int Count, string Kind)>(StringComparer.Ordinal);
        for (int i = 0; i < 30; i++)
        {
            var payload = new byte[16];
            payload[0] = (byte)i;
            payload[1] = (byte)(i / 256);
            for (int j = 2; j < payload.Length; j++)
                payload[j] = (byte)(i * 7 + j);
            cipher[Convert.ToBase64String(payload)] = (1, "user");
        }
        hints = ProtectionAnalyzer.Analyze(module, Array.Empty<TypeEntry>(), emptySections, emptyResources, cipher);
        Check("cipher fixture has more than the detection threshold of distinct literals", cipher.Count > 20,
            cipher.Count.ToString());
        Check("base64-like literals flag an encrypted string table",
            hints.Any(h => h.Name == "Encrypted string table"), Describe(hints));
    }

    private static Dictionary<string, (int Count, string Kind)> EmptyStrings()
        => new(StringComparer.Ordinal);

    private static string Describe(IReadOnlyList<ProtectionHint> hints)
        => hints.Count == 0
            ? "(no hints)"
            : string.Join(", ", hints.Select(h => $"{h.SeverityLabel}:{h.Name}"));

    // ---------------------------------------------------------------- decompiler
    private static void TestDecompiler()
    {
        using var inspector = AssemblyInspector.Open(SelfAssemblyPath);
        TypeEntry? nested = inspector.Types.FirstOrDefault(t => t.Name == "NestedProbe");
        Check("nested probe type available for decompilation", nested != null);
        if (nested != null)
        {
            string source = inspector.DecompileType(nested);
            Check("decompilation returns C# source", source.Contains("NestedProbe") && source.Contains("class"),
                Head(source));
            string again = inspector.DecompileType(nested);
            Check("cached decompilation is repeatable", again == source, "output changed between calls");
        }

        TypeEntry missing = new() { FullName = "Nope.Missing", Name = "Missing" };
        string failure = inspector.DecompileType(missing);
        Check("unknown type returns a readable diagnostic",
            failure.Contains("decompilation failed") && !failure.Contains("   at "), Head(failure));

        using (ModuleDefMD module = ModuleDefMD.Load(SelfAssemblyPath))
        {
            string noPath = DecompilerService.Shared.DecompileType(module, null, "Any.Type", "Type");
            Check("in-memory module reports a readable diagnostic",
                noPath.Contains("requires the assembly to exist on disk"), Head(noPath));

            DecompilerService.Shared.Reset();
            string afterReset = DecompilerService.Shared.DecompileType(module, SelfAssemblyPath,
                "Obsidian.Dumper.Tests.Program", "Program");
            Check("decompiler works after cache reset",
                afterReset.Contains("class Program") || afterReset.Contains("Program"), Head(afterReset));
        }
    }

    // ---------------------------------------------------------------- malformed
    private static void TestMalformedInputs()
    {
        string directory = CreateTempDirectory("malformed");
        try
        {
            string empty = Path.Combine(directory, "empty.dll");
            File.WriteAllBytes(empty, Array.Empty<byte>());
            Check("empty file is rejected", !AssemblyInspector.TryOpen(empty, out _, out string? emptyError),
                "accepted an empty file");
            Check("empty file error is readable", !string.IsNullOrWhiteSpace(emptyError), emptyError);
            Check("empty file is not managed", !AssemblyInspector.IsManagedFile(empty));

            string junk = Path.Combine(directory, "junk.dll");
            File.WriteAllBytes(junk, [0x4D, 0x5A, 0, 0, 0, 0, 0, 0]);
            Check("'MZ' stub is rejected", !AssemblyInspector.TryOpen(junk, out _, out string? junkError),
                "accepted an MZ stub");
            Check("'MZ' stub error is readable", !string.IsNullOrWhiteSpace(junkError), junkError);
            Check("'MZ' stub is not managed", !AssemblyInspector.IsManagedFile(junk));

            string truncated = Path.Combine(directory, "truncated.dll");
            byte[] full = File.ReadAllBytes(SelfAssemblyPath);
            File.WriteAllBytes(truncated, full[..Math.Min(2048, full.Length)]);
            Check("truncated assembly is rejected", !AssemblyInspector.TryOpen(truncated, out _, out string? truncError),
                "accepted a truncated assembly");
            Check("truncated assembly error is readable", !string.IsNullOrWhiteSpace(truncError), truncError);

            // A file that another process keeps open for read+write must still be
            // inspectable (the loader's sharing mode used to make it fail).
            string shared = Path.Combine(directory, "shared.dll");
            File.Copy(SelfAssemblyPath, shared, true);
            using (var holder = new FileStream(shared, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                Check("file open by another reader is still inspectable",
                    AssemblyInspector.TryOpen(shared, out AssemblyInspector? sharedInspector, out string? sharedError),
                    sharedError);
                sharedInspector?.Dispose();
            }
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    // ---------------------------------------------------------------- CLI
    private static async Task TestCliSurface()
    {
        string? cli = FindCliBinary();
        if (cli == null)
        {
            Info("CLI binary is not built; run tools/build.ps1 -Target cli first (group skipped)");
            return;
        }

        CliResult help = await RunCliAsync(cli, "--help");
        Check("help exits 0", help.ExitCode == 0, help.Describe());
        Check("help documents the usage line", help.Stdout.Contains("usage: obsidian <command>"), Head(help.Stdout));
        Check("help documents exit codes", help.Stdout.Contains("exit codes"), Head(help.Stdout));
        Check("help mentions every command",
            new[] { "list", "info", "modules", "dump", "deep", "inspect", "inject", "unload", "token", "diag" }
                .All(c => help.Stdout.Contains(" " + c)),
            Head(help.Stdout));

        CliResult injectHelp = await RunCliAsync(cli, "inject", "--help");
        Check("inject --help exits 0", injectHelp.ExitCode == 0, injectHelp.Describe());
        Check("inject --help shows usage", injectHelp.Stdout.Contains("obsidian inject --pid"),
            Head(injectHelp.Stdout));

        CliResult injectMissingDll = await RunCliAsync(cli, "inject", "--pid", "self");
        Check("inject without --dll exits 1", injectMissingDll.ExitCode == 1, injectMissingDll.Describe());

        CliResult injectMissingFile = await RunCliAsync(cli, "inject", "--pid", "self", "--dll",
            Path.Combine(Path.GetTempPath(), "obsidian-no-such-inject.dll"));
        Check("inject with a missing DLL exits 2", injectMissingFile.ExitCode == 2, injectMissingFile.Describe());
        Check("inject reports the missing DLL", injectMissingFile.Stderr.Contains("DLL not found"),
            Head(injectMissingFile.Stderr));

        string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string validSystemDll = Path.Combine(system32, "winhttp.dll");
        if (File.Exists(validSystemDll))
        {
            CliResult badMethod = await RunCliAsync(cli, "inject", "--pid", "self", "--dll", validSystemDll,
                "--method", "bogus");
            Check("inject with an unknown method exits 1", badMethod.ExitCode == 1, badMethod.Describe());
            Check("inject reports the unknown method",
                badMethod.Stderr.Contains("unknown injection method"), Head(badMethod.Stderr));
        }

        string sysWow64 = Path.Combine(Path.GetDirectoryName(system32) ?? system32, "SysWOW64", "kernel32.dll");
        if (File.Exists(sysWow64))
        {
            CliResult mismatch = await RunCliAsync(cli, "inject", "--pid", "self", "--dll", sysWow64);
            Check("inject reports an architecture mismatch with exit 2", mismatch.ExitCode == 2,
                mismatch.Describe());
            Check("inject mismatch message is honest", mismatch.Stderr.Contains("cannot inject"),
                Head(mismatch.Stderr));
        }

        CliResult unloadMissingModule = await RunCliAsync(cli, "unload", "--pid", "self");
        Check("unload without --module exits 1", unloadMissingModule.ExitCode == 1,
            unloadMissingModule.Describe());

        CliResult unloadUnknown = await RunCliAsync(cli, "unload", "--pid", "self", "--module", "no-such-module.dll");
        Check("unload of an unknown module exits 2", unloadUnknown.ExitCode == 2, unloadUnknown.Describe());
        Check("unload failure is explained", unloadUnknown.Stderr.Contains("unload failed"),
            Head(unloadUnknown.Stderr));

        CliResult commandHelp = await RunCliAsync(cli, "help", "dump");
        Check("command help exits 0", commandHelp.ExitCode == 0, commandHelp.Describe());
        Check("command help shows dump usage", commandHelp.Stdout.Contains("usage: obsidian dump"),
            Head(commandHelp.Stdout));

        CliResult unknown = await RunCliAsync(cli, "definitely-not-a-command");
        Check("unknown command exits 1", unknown.ExitCode == 1, unknown.Describe());

        CliResult badOption = await RunCliAsync(cli, "list", "--bogus");
        Check("unknown option exits 1", badOption.ExitCode == 1, badOption.Describe());
        Check("unknown option is reported", badOption.Stderr.Contains("unknown option"), Head(badOption.Stderr));

        CliResult missingValue = await RunCliAsync(cli, "info", "--pid");
        Check("missing option value exits 1", missingValue.ExitCode == 1, missingValue.Describe());
        Check("missing option value is reported", missingValue.Stderr.Contains("requires a value"),
            Head(missingValue.Stderr));

        CliResult missingPid = await RunCliAsync(cli, "modules");
        Check("missing --pid exits 1", missingPid.ExitCode == 1, missingPid.Describe());

        CliResult dumpNoSelection = await RunCliAsync(cli, "dump", "--pid", "self");
        Check("dump without --all/--name exits 1", dumpNoSelection.ExitCode == 1, dumpNoSelection.Describe());
        Check("dump selection error is reported", dumpNoSelection.Stderr.Contains("--all"), Head(dumpNoSelection.Stderr));

        CliResult listJson = await RunCliAsync(cli, "list", "--json");
        Check("list --json exits 0", listJson.ExitCode == 0, listJson.Describe());
        Check("list --json returns a JSON array", ParseJsonArray(listJson.Stdout), Head(listJson.Stdout));

        CliResult inspectMissing = await RunCliAsync(cli, "inspect", Path.Combine(Path.GetTempPath(), "no-such-file.dll"));
        Check("inspect of a missing file exits 2", inspectMissing.ExitCode == 2, inspectMissing.Describe());

        CliResult inspectJson = await RunCliAsync(cli, "inspect", SelfAssemblyPath, "--json");
        Check("inspect --json exits 0", inspectJson.ExitCode == 0, inspectJson.Describe());
        Check("inspect --json has summary and types",
            HasJsonProperty(inspectJson.Stdout, "summary") && HasJsonProperty(inspectJson.Stdout, "types"),
            Head(inspectJson.Stdout));

        CliResult inspectIl = await RunCliAsync(cli, "inspect", SelfAssemblyPath, "--type", "Program", "--il");
        Check("inspect --type --il exits 0", inspectIl.ExitCode == 0, inspectIl.Describe());
        Check("inspect --il prints IL instructions", inspectIl.Stdout.Contains("IL_"), Head(inspectIl.Stdout));

        CliResult inspectUnknownType = await RunCliAsync(cli, "inspect", SelfAssemblyPath, "--type", "No.Such.Type");
        Check("inspect of an unknown type exits 2", inspectUnknownType.ExitCode == 2, inspectUnknownType.Describe());

        CliResult infoJson = await RunCliAsync(cli, "info", "--pid", "self", "--json");
        Check("info --pid self --json exits 0", infoJson.ExitCode == 0, infoJson.Describe());
        Check("info --json has process and modules",
            HasJsonProperty(infoJson.Stdout, "process") && HasJsonProperty(infoJson.Stdout, "modules"),
            Head(infoJson.Stdout));

        CliResult modulesSelf = await RunCliAsync(cli, "modules", "--pid", "self");
        Check("modules --pid self exits 0", modulesSelf.ExitCode == 0, modulesSelf.Describe());
        Check("modules lists a module table", modulesSelf.Stdout.Contains("MANAGED"), Head(modulesSelf.Stdout));

        CliResult modulesJson = await RunCliAsync(cli, "modules", "--pid", "self", "--json");
        Check("modules --json exits 0", modulesJson.ExitCode == 0, modulesJson.Describe());
        Check("modules --json returns a JSON array", ParseJsonArray(modulesJson.Stdout), Head(modulesJson.Stdout));

        CliResult diag = await RunCliAsync(cli, "diag");
        Check("diag exits 0", diag.ExitCode == 0, diag.Describe());
        Check("diag prints enumeration count", diag.Stdout.Contains("enumerated="), Head(diag.Stdout));
    }

    private static bool ParseJsonArray(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasJsonProperty(string json, string property)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(property, out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? FindCliBinary()
    {
        string? root = FindRepositoryRoot();
        if (root == null)
            return null;
        string bin = Path.Combine(root, "src", "Obsidian.Dumper.Cli", "bin");
        if (!Directory.Exists(bin))
            return null;

        try
        {
            return Directory.EnumerateFiles(bin, "obsidian.exe", SearchOption.AllDirectories)
                .Select(p => new FileInfo(p))
                .Where(f => f.Exists)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.FullName)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr, bool TimedOut)
    {
        public string Describe()
            => $"exit={ExitCode}{(TimedOut ? " (timed out)" : string.Empty)}" +
               (string.IsNullOrWhiteSpace(Stderr) ? string.Empty : $" stderr={Head(Stderr)}");
    }

    private static async Task<CliResult> RunCliAsync(string cli, params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = cli,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(cli)!,
        };
        foreach (string argument in args)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        bool exited = process.WaitForExit(20_000);
        if (!exited)
        {
            try
            {
                process.Kill(true);
            }
            catch
            {
                // best effort
            }
            return new CliResult(-1, string.Empty, "timeout", TimedOut: true);
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        return new CliResult(process.ExitCode, stdout, stderr, TimedOut: false);
    }

    // ---------------------------------------------------------------- injection
    private static async Task TestInjectionAsync()
    {
        string? targetPath = FindSampleTarget();
        Check("sample target exists for injection", targetPath != null, targetPath ?? "not found");
        if (targetPath == null)
            return;

        using var target = StartSampleProcess(targetPath, 180);
        Check("injection target started", target != null);
        if (target == null)
            return;

        try
        {
            ProcessRecord? record = await WaitForRecordAsync(target.Id, r => r.IsManaged, TimeSpan.FromSeconds(20))
                .ConfigureAwait(false);
            Check("injection target is managed and visible", record?.IsManaged == true,
                record == null ? "(not in inventory)" : record.RuntimeLabel);

            TargetSession? session = await AttachWithRetryAsync(target.Id, TimeSpan.FromSeconds(15))
                .ConfigureAwait(false);
            Check("injection target attaches", session != null);
            if (session == null)
                return;

            using (session)
            {
                // Let the runtime finish loading its own assemblies before the
                // first injection so the loader state is representative.
                await WaitForAsync(
                    () => session.ReloadModules().Any(m => m.IsManaged &&
                                                          m.Name.Equals("ObsidianTestTarget.dll",
                                                              StringComparison.OrdinalIgnoreCase)),
                    TimeSpan.FromSeconds(15)).ConfigureAwait(false);

                var injector = new DllInjector(NullLogSink.Instance);
                string? dll = FindInjectableSystemDll(session.Modules);
                Check("a not-yet-loaded System32 DLL is available", dll != null, "(no candidate found)");
                if (dll == null)
                    return;

                string moduleName = Path.GetFileName(dll);

                Check("compatibility check accepts a matching DLL",
                    DllInjector.IsCompatible(target.Id, dll, out string? compatibleReason), compatibleReason);

                InjectionResult injected = injector.Inject(target.Id, dll);
                Check("CreateRemoteThread injection succeeds", injected.Success,
                    $"{injected.Message} win32={injected.Win32Error}");
                Check("injection reports a remote module base", injected.RemoteModuleBase != 0,
                    "base=0x0");
                Check("injection duration is reported", injected.DurationMs >= 0);
                Check("injection message is human readable",
                    !string.IsNullOrWhiteSpace(injected.Message), "(empty)");

                bool appeared = await WaitForModuleAsync(session, moduleName, present: true,
                    TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                Check("injected module appears in the target module list", appeared, moduleName);

                InjectionResult unloaded = injector.Unload(target.Id, moduleName);
                Check("unload succeeds", unloaded.Success, unloaded.Message);
                bool disappeared = await WaitForModuleAsync(session, moduleName, present: false,
                    TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                if (!disappeared)
                {
                    // Loader timing: one retry is allowed before failing the check.
                    injector.Unload(target.Id, moduleName);
                    disappeared = await WaitForModuleAsync(session, moduleName, present: false,
                        TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                Check("module disappears from the module list after unload", disappeared, moduleName);
                Check("target is still alive after inject/unload", IsProcessAlive(target.Id));

                // NtCreateThreadEx is expected to work on x64, but hardened
                // machines can block it; report rather than fail.
                InjectionResult nt = injector.Inject(target.Id, dll, InjectionMethod.NtCreateThreadEx);
                Check("NtCreateThreadEx returns a result without throwing", nt.Message.Length > 0,
                    "(empty message)");
                Check("NtCreateThreadEx reports its method", nt.Method == InjectionMethod.NtCreateThreadEx);
                if (nt.Success)
                {
                    await WaitForModuleAsync(session, moduleName, present: true, TimeSpan.FromSeconds(10))
                        .ConfigureAwait(false);
                    injector.Unload(target.Id, moduleName);
                    await WaitForModuleAsync(session, moduleName, present: false, TimeSpan.FromSeconds(5))
                        .ConfigureAwait(false);
                }
                else
                {
                    Info("NtCreateThreadEx not usable here: " + nt.Message);
                }

                // QueueUserAPC is deliberately best effort: threads must enter an
                // alertable wait. Only the contract is asserted, never success.
                // It runs last so a late APC delivery cannot unbalance the
                // deterministic load/unload round-trips above.

                // Architecture mismatch: a 32-bit System32 DLL must be rejected
                // before anything is allocated in the target.
                string? wrongBitness = FindWrongBitnessSystemDll();
                if (wrongBitness != null)
                {
                    bool compatible = DllInjector.IsCompatible(target.Id, wrongBitness, out string? mismatch);
                    Check("wrong-bitness DLL is rejected by IsCompatible",
                        !compatible && !string.IsNullOrWhiteSpace(mismatch), mismatch ?? "(no reason)");
                    Check("wrong-bitness reason explains the mismatch",
                        mismatch != null && (mismatch.Contains("x86", StringComparison.OrdinalIgnoreCase) ||
                                             mismatch.Contains("x64", StringComparison.OrdinalIgnoreCase) ||
                                             mismatch.Contains("bitness", StringComparison.OrdinalIgnoreCase)),
                        mismatch ?? "(null)");

                    InjectionResult mismatchInjection = injector.Inject(target.Id, wrongBitness);
                    Check("wrong-bitness injection fails without throwing",
                        !mismatchInjection.Success && mismatchInjection.Message.Length > 0,
                        mismatchInjection.Message);
                }
                else
                {
                    Info("no 32-bit kernel32.dll available for the mismatch check (skipped)");
                }

                bool missingFileRejected = DllInjector.IsCompatible(target.Id,
                    Path.Combine(Path.GetTempPath(), "obsidian-no-such-" + Guid.NewGuid().ToString("N")[..6] + ".dll"),
                    out string? missingFileReason);
                Check("missing DLL file is rejected by IsCompatible",
                    !missingFileRejected && !string.IsNullOrWhiteSpace(missingFileReason),
                    missingFileReason);

                Check("target survives rejected injections", IsProcessAlive(target.Id));

                // End-to-end CLI injection path, using the same owned target.
                string? cli = FindCliBinary();
                if (cli != null)
                {
                    // Drain references left by earlier best-effort attempts so the
                    // CLI round-trip starts from a clean loader state.
                    for (int attempt = 0; attempt < 6 && IsModuleLoaded(session, moduleName); attempt++)
                    {
                        injector.Unload(target.Id, moduleName);
                        await Task.Delay(100).ConfigureAwait(false);
                    }
                    Check("module is not loaded before the CLI round-trip",
                        !IsModuleLoaded(session, moduleName), moduleName);

                    CliResult cliInject = await RunCliAsync(cli, "inject", "--pid", target.Id.ToString(),
                        "--dll", dll, "--method", "crt", "--json").ConfigureAwait(false);
                    Check("CLI inject exits 0", cliInject.ExitCode == 0, cliInject.Describe());
                    Check("CLI inject reports success in JSON",
                        cliInject.Stdout.Contains("\"success\": true", StringComparison.OrdinalIgnoreCase),
                        Head(cliInject.Stdout));
                    await WaitForModuleAsync(session, moduleName, present: true, TimeSpan.FromSeconds(10))
                        .ConfigureAwait(false);

                    CliResult cliUnload = await RunCliAsync(cli, "unload", "--pid", target.Id.ToString(),
                        "--module", moduleName, "--json").ConfigureAwait(false);
                    Check("CLI unload exits 0", cliUnload.ExitCode == 0, cliUnload.Describe());
                    bool cliGone = await WaitForModuleAsync(session, moduleName, present: false,
                        TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    Check("CLI-injected module disappears after CLI unload", cliGone, moduleName);
                }
                else
                {
                    Info("CLI binary not built; end-to-end injection via CLI skipped");
                }

                // Process control: suspend / resume must keep the target intact.
                Check("suspend succeeds", ProcessControl.TrySuspend(target.Id, out string? suspendError),
                    suspendError);
                Check("suspended process is reported as suspended", ProcessControl.IsSuspended(target.Id));
                Check("resume succeeds", ProcessControl.TryResume(target.Id, out string? resumeError),
                    resumeError);
                bool runningAgain = await WaitForAsync(
                    () => !ProcessControl.IsSuspended(target.Id), TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                Check("process is reported as running after resume", runningAgain);
                bool responsive = await WaitForModuleAsync(session, "ObsidianTestTarget.dll", present: true,
                    TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Check("target is responsive after suspend/resume", responsive);
                Check("target is alive after suspend/resume", IsProcessAlive(target.Id));

                Check("suspend of a nonexistent process fails honestly",
                    !ProcessControl.TrySuspend(999_999, out string? badSuspend) &&
                    !string.IsNullOrWhiteSpace(badSuspend), badSuspend);
                Check("resume of a nonexistent process fails honestly",
                    !ProcessControl.TryResume(999_999, out string? badResume) &&
                    !string.IsNullOrWhiteSpace(badResume), badResume);
                Check("IsSuspended is false for a nonexistent process",
                    !ProcessControl.IsSuspended(999_999));
                Check("terminate rejects an invalid pid",
                    !ProcessControl.TryTerminate(0, out string? terminateError) &&
                    !string.IsNullOrWhiteSpace(terminateError), terminateError);

                // QueueUserAPC is deliberately best effort: threads must enter an
                // alertable wait, and it is the riskiest variant. It runs last and
                // its liveness is reported without cascading failures into the
                // deterministic checks above if the target happens to terminate.
                InjectionResult apc = injector.Inject(target.Id, dll, InjectionMethod.QueueUserApc);
                Check("APC injection returns a meaningful result",
                    apc.Method == InjectionMethod.QueueUserApc && apc.Message.Length > 0,
                    apc.Message);
                Check("APC injection does not throw or report raw stack traces",
                    !apc.Message.Contains("   at ", StringComparison.Ordinal), Head(apc.Message));
                if (apc.Success)
                {
                    await WaitForModuleAsync(session, moduleName, present: true, TimeSpan.FromSeconds(10))
                        .ConfigureAwait(false);
                    injector.Unload(target.Id, moduleName);
                    await WaitForModuleAsync(session, moduleName, present: false, TimeSpan.FromSeconds(5))
                        .ConfigureAwait(false);
                }
                else
                {
                    Info("APC injection pending (expected on non-alertable threads): " + apc.Message);
                    await Task.Delay(400).ConfigureAwait(false);
                    if (await WaitForModuleAsync(session, moduleName, present: true, TimeSpan.FromMilliseconds(600))
                            .ConfigureAwait(false))
                    {
                        injector.Unload(target.Id, moduleName);
                        await WaitForModuleAsync(session, moduleName, present: false, TimeSpan.FromSeconds(5))
                            .ConfigureAwait(false);
                    }
                }
                if (!IsProcessAlive(target.Id))
                    Info("target terminated during/after the best-effort APC attempt (isolated: no other checks depend on it)");
            }
        }
        finally
        {
            KillProcess(target);
        }
    }

    private static Process? StartSampleProcess(string targetPath, int seconds)
    {
        try
        {
            Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = targetPath,
                Arguments = seconds.ToString(CultureInfo.InvariantCulture),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(targetPath)!,
            });
            if (process != null)
            {
                // Drain the pipes so the child can never block on a full buffer.
                process.OutputDataReceived += (_, _) => { };
                process.ErrorDataReceived += (_, _) => { };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            return process;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindInjectableSystemDll(IReadOnlyList<ModuleRecord> loadedModules)
    {
        string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        foreach (string name in new[] { "winhttp.dll", "dbghelp.dll", "wininet.dll", "urlmon.dll", "dwmapi.dll" })
        {
            string path = Path.Combine(system32, name);
            if (!File.Exists(path))
                continue;
            if (loadedModules.Any(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                continue;
            return path;
        }
        return null;
    }

    private static string? FindWrongBitnessSystemDll()
    {
        string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string? windows = Path.GetDirectoryName(system32);
        if (windows == null)
            return null;
        string sysWow64 = Path.Combine(windows, "SysWOW64", "kernel32.dll");
        return File.Exists(sysWow64) ? sysWow64 : null;
    }

    private static bool IsModuleLoaded(TargetSession session, string moduleName)
    {
        try
        {
            return session.ReloadModules()
                .Any(m => m.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> WaitForModuleAsync(TargetSession session, string moduleName, bool present,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                IReadOnlyList<ModuleRecord> modules = session.ReloadModules();
                bool found = modules.Any(m => m.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase));
                if (found == present)
                    return true;
            }
            catch
            {
                // The target may be between states; keep polling until timeout.
            }
            await Task.Delay(150).ConfigureAwait(false);
        }
        return false;
    }

    private static async Task<bool> WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                if (predicate())
                    return true;
            }
            catch
            {
                // transient state
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
        return false;
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- live dump
    private static async Task TestLiveDumpAsync()
    {
        string? targetPath = FindSampleTarget();
        Check("sample target exists", targetPath != null, targetPath ?? "not found");
        if (targetPath == null)
            return;

        using var target = Process.Start(new ProcessStartInfo
        {
            FileName = targetPath,
            Arguments = "120",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(targetPath)!,
        });

        Check("target started", target != null);
        if (target == null)
            return;

        target.OutputDataReceived += (_, _) => { };
        target.ErrorDataReceived += (_, _) => { };
        target.BeginOutputReadLine();
        target.BeginErrorReadLine();

        string output = CreateTempDirectory("live-dump");
        try
        {
            // Poll instead of a fixed sleep: build machines are slow and a fixed
            // 1200 ms wait used to make this test flaky. Wait until the process
            // inventory also reports the CLR, which is what the managed filter
            // and the UI rely on.
            ProcessRecord? record = await WaitForRecordAsync(target.Id, r => r.IsManaged, TimeSpan.FromSeconds(20))
                .ConfigureAwait(false);
            Check("target appears in the process inventory", record != null);
            Check("target is detected as managed", record?.IsManaged == true, record?.RuntimeLabel);
            Check("architecture detected", record?.Architecture == ProcessArchitecture.X64,
                record?.Architecture.ToString());
            Check("working set is reported", record?.WorkingSet is > 0,
                record?.WorkingSet?.ToString() ?? "(null)");

            TargetSession? session = await AttachWithRetryAsync(target.Id, TimeSpan.FromSeconds(15))
                .ConfigureAwait(false);
            Check("attach succeeds", session != null);
            if (session == null)
                return;

            using (session)
            {
                // The CLR can be reported before the application/framework
                // assemblies finish loading; wait for a stable module inventory
                // instead of racing runtime startup.
                bool appLoaded = await WaitForAsync(
                    () => session.ReloadModules().Any(m => m.IsManaged &&
                                                          m.Name.Equals("ObsidianTestTarget.dll",
                                                              StringComparison.OrdinalIgnoreCase)),
                    TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                Check("application assembly is loaded in the target", appLoaded);
                await WaitForAsync(() => session.ReloadModules().Count(m => m.IsManaged) >= 3,
                    TimeSpan.FromSeconds(10)).ConfigureAwait(false);

                Check("module map is populated", session.Modules.Count > 5, session.Modules.Count.ToString());
                Check("managed modules detected", session.ManagedModules.Count >= 5,
                    session.ManagedModules.Count.ToString());
                Check("command line was read from the PEB",
                    !string.IsNullOrEmpty(session.CommandLine) &&
                    session.CommandLine.Contains("ObsidianTestTarget", StringComparison.OrdinalIgnoreCase),
                    session.CommandLine ?? "(null)");

                var options = new DumpOptions
                {
                    OutputDirectory = output,
                    Overwrite = true,
                    IncludeFrameworkModules = false,
                };

                var dumper = new ModuleDumper(session, NullLogSink.Instance);
                var targets = session.Modules
                    .Where(m => m.IsManaged && !m.IsFramework)
                    .ToList();
                Check("application assembly found", targets.Count >= 1, targets.Count.ToString());

                IReadOnlyList<DumpResult> results = await dumper.DumpAsync(targets, options).ConfigureAwait(false);
                Check("all application modules dumped", results.All(r => r.Success),
                    string.Join("; ", results.Where(r => !r.Success).Select(r => r.Error)));

                DumpResult? app = results.FirstOrDefault(r =>
                    r.Module.Name.Equals("ObsidianTestTarget.dll", StringComparison.OrdinalIgnoreCase));
                Check("test assembly was dumped", app is { Success: true });
                if (app?.OutputPath is not { } dumped)
                    return;

                Check("dumped file exists", File.Exists(dumped));
                Check("sha256 returned", app.Sha256?.Length == 64);
                Check("dnlib verification succeeded", app.Verified, app.Verification);

                using ModuleDefMD module = ModuleDefMD.Load(dumped);
                int types = module.GetTypes().Count();
                Check("dumped assembly has expected types", types >= 8, types.ToString());

                using var inspector = AssemblyInspector.Open(dumped);
                Check("inspector reads the dump", inspector.Summary.TypeCount >= 8,
                    inspector.Summary.TypeCount.ToString());
                Check("hidden dynamic assembly strings are present",
                    inspector.Strings.Any(s => s.Value.Contains("ObsidianDynamicPayload")),
                    "no dynamic assembly marker");
                Check("string literals survive the dump",
                    inspector.Strings.Any(s => s.Value == "s3cr3t-s4lt-value"),
                    "salt literal missing");
                Check("protections report is present", inspector.Summary.Protections != null);
                Check("embedded resource survives the dump",
                    inspector.Summary.Resources.Any(r => r.Name.Contains("payload.txt", StringComparison.OrdinalIgnoreCase)),
                    string.Join(", ", inspector.Summary.Resources.Select(r => r.Name)));

                TypeEntry? nestedSaltState = inspector.Types.FirstOrDefault(t => t.Name == "SaltState");
                Check("dumped nested type inherits namespace",
                    nestedSaltState != null && nestedSaltState.Namespace == "ObsidianTestTarget" &&
                    nestedSaltState.NestedLevel == 1,
                    nestedSaltState == null
                        ? "(missing)"
                        : $"ns={nestedSaltState.Namespace} depth={nestedSaltState.NestedLevel}");

                TypeEntry? switchProbeType = inspector.Types.FirstOrDefault(t =>
                    t.Methods.Any(m => m.Name == "SwitchProbe"));
                MethodEntry? switchProbe = switchProbeType?.Methods.FirstOrDefault(m => m.Name == "SwitchProbe");
                Check("dumped switch probe has a body", switchProbe?.HasBody == true);
                if (switchProbe != null)
                {
                    string il = inspector.Disassemble(switchProbe);
                    Check("dumped IL contains a resolved switch", il.Contains("switch") && il.Contains("IL_"),
                        Head(il));
                }

                MethodEntry? tryCatchProbe = inspector.Types
                    .SelectMany(t => t.Methods)
                    .FirstOrDefault(m => m.Name == "TryCatchProbe");
                if (tryCatchProbe != null)
                {
                    string il = inspector.Disassemble(tryCatchProbe);
                    Check("dumped IL exposes exception handlers", il.Contains("// EH"), Head(il));
                }

                TypeEntry? license = inspector.Types.FirstOrDefault(t => t.Name == "LicenseEngine");
                Check("type list contains LicenseEngine", license != null);
                if (license != null)
                {
                    string source = inspector.DecompileType(license);
                    Check("ILSpy decompilation contains the class",
                        source.Contains("class LicenseEngine", StringComparison.Ordinal) &&
                        source.Contains("Validate", StringComparison.Ordinal),
                        Head(source));
                }

                TypeEntry? vault = inspector.Types.FirstOrDefault(t => t.Name == "SecretVault");
                if (vault != null)
                {
                    MethodEntry? method = vault.Methods.FirstOrDefault(m => m.HasBody);
                    Check("method has a body after dump", method != null);
                    if (method != null)
                    {
                        string il = inspector.Disassemble(method);
                        Check("IL disassembly includes instructions", il.Contains("IL_"), Head(il));
                    }
                }

                var deep = new DeepDumper(NullLogSink.Instance);
                DeepDumpResult deepResult = await deep.RunAsync(session, options).ConfigureAwait(false);
                Check("deep dump on CoreCLR is reported as unsupported",
                    deepResult.Status is DeepDumpStatus.Unsupported or DeepDumpStatus.Unavailable
                        or DeepDumpStatus.Failed,
                    deepResult.Status + ": " + deepResult.Message);
            }
        }
        finally
        {
            KillProcess(target);
            TryDeleteDirectory(output);
        }
    }

    private static async Task<ProcessRecord?> WaitForRecordAsync(int pid, Func<ProcessRecord, bool> predicate,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            ProcessRecord? record = ProcessDiscovery.Enumerate().FirstOrDefault(p => p.Id == pid);
            if (record != null && predicate(record))
                return record;
            await Task.Delay(150).ConfigureAwait(false);
        }
        return null;
    }

    private static async Task<TargetSession?> AttachWithRetryAsync(int pid, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        string? lastError = null;
        while (stopwatch.Elapsed < timeout)
        {
            TargetSession? session = TargetSession.Attach(pid, out lastError);
            if (session != null)
                return session;
            await Task.Delay(250).ConfigureAwait(false);
        }
        Info("last attach error: " + (lastError ?? "(none)"));
        return null;
    }

    private static void TestAgentAssets()
    {
        bool available = DeepDumper.ResolveAssets(out string? agent, out string? payload);
        if (available)
        {
            Check("agent DLL exists", agent != null && File.Exists(agent));
            Check("payload DLL exists", payload != null && File.Exists(payload));
        }
        else
        {
            Info("agent assets are not staged in this build");
        }
    }

    // ---------------------------------------------------------------- helpers
    private static string? FindSampleTarget()
    {
        string? root = FindRepositoryRoot();
        if (root == null)
            return null;

        // Accept Debug and Release so the harness also works with -Release builds.
        foreach (string configuration in new[] { "Debug", "Release" })
        {
            string candidate = Path.Combine(root, "samples", "TestTarget", "bin", configuration, "net10.0",
                "ObsidianTestTarget.exe");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static string? FindRepositoryRoot()
    {
        string? root = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && root != null; i++)
        {
            if (File.Exists(Path.Combine(root, "Obsidian.Dumper.sln")))
                return root;
            root = Path.GetDirectoryName(root);
        }
        return null;
    }

    private static string CreateTempDirectory(string purpose)
    {
        string directory = Path.Combine(Path.GetTempPath(), "obsidian-tests", purpose + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (process.HasExited)
                return;
            process.Kill(true);
            process.WaitForExit(5000);
        }
        catch
        {
            // best effort
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
                return;
            }
            catch
            {
                // Files can stay locked by the operating system for a moment.
                Thread.Sleep(100);
            }
        }
    }

    private static bool IsHex(string value)
        => value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>
    /// Builds a metadata-only name at runtime so the check itself does not embed
    /// the string literal in the assembly (which would flip its kind to "user").
    /// </summary>
    private static string MetadataOnlyProbe()
    {
        char[] name = ['g', 'e', 't', '_', 'V', 'a', 'l', 'u', 'e'];
        return new string(name);
    }

    private static string Head(string? text, int length = 300)
    {
        if (string.IsNullOrEmpty(text))
            return "(empty)";
        string single = text.Replace("\r", string.Empty).Replace('\n', ' ');
        return single.Length <= length ? single : single[..length] + "...";
    }

    private static void Info(string message)
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.Write("  info ");
        Console.ResetColor();
        Console.WriteLine(message);
    }

    private static void Check(string name, bool condition, string? detail = null)
    {
        if (condition)
        {
            _passed++;
            Console.ForegroundColor = ConsoleColor.DarkGreen;
            Console.Write("  ok   ");
            Console.ResetColor();
            Console.WriteLine(name);
        }
        else
        {
            _failed++;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write("  FAIL ");
            Console.ResetColor();
            Console.WriteLine(name + (detail == null ? string.Empty : "  ->  " + detail));
        }
    }

    private static void Fail(string name, string detail) => Check(name, false, detail);
}

// ---------------------------------------------------------------- test fixtures

/// <summary>
/// Deterministic control-flow shapes for the IL formatter checks. These are
/// compiled into the harness assembly itself, so the checks do not depend on a
/// running sample process.
/// </summary>
internal static class IlFixtures
{
    /// <summary>576 characters: exercises the IL formatter's 512-char truncation.</summary>
    private const string LongText =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" +
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" +
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" +
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" +
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" +
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" +
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" +
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" +
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>Ten arms guarantee a real IL <c>switch</c> opcode (not a compare chain).</summary>
    public static string SwitchProbe(int value) => value switch
    {
        0 => "zero",
        1 => "one",
        2 => "two",
        3 => "three",
        4 => "four",
        5 => "five",
        6 => "six",
        7 => "seven",
        8 => "eight",
        9 => "nine",
        _ => "many",
    };

    public static int TryCatchProbe(string? text)
    {
        int length = 0;
        try
        {
            length = text!.Length;
        }
        catch (NullReferenceException)
        {
            length = -1;
        }
        finally
        {
            length += 1;
        }
        return length;
    }

    /// <summary>Escaped characters first, then the &gt;512-char payload for truncation.</summary>
    public static string LongLiteral()
        => "\"quoted\"\nsecond line " + LongText;

    /// <summary>Nested type fixture: the inspector must inherit its namespace.</summary>
    internal sealed class NestedProbe
    {
        public int Value => 42;

        public static string Echo(string text) => text;
    }
}
