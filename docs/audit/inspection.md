# Inspection, CLI, tests and samples — audit report

**Scope:** `src/Obsidian.Dumper.Core/Inspection/`, `src/Obsidian.Dumper.Cli/`,
`tests/Obsidian.Dumper.Tests/`, `samples/`.
**Audit date:** 2026-10-28.
**Revision audited:** working tree after task-2 + task-6 fixes (line numbers refer to
the *fixed* revision unless the row says "pre-fix").

## Method

1. Read every file in scope end to end, plus the engine contracts they consume
   (`TargetSession`, `RemoteProcess`, `ModuleDumper`, `DumpOptions`, `DllInjector`,
   `ProcessControl`, `PeImageLayout`) and the WPF consumers
   (`InspectorViewModel`, `Converters`) to keep the public API compatible.
2. Checked every dnlib / ILSpy assumption against the actual package assemblies
   (reflection over `dnlib 4.4.0` and `ICSharpCode.Decompiler 9.1.0.7988`):
   `Machine` member names, `MethodImplAttributes.Runtime`, `ResourceType`,
   `ModuleDef.CustomAttributes`, `LanguageVersion` values, `CSharpDecompiler`
   thread-safety remarks.
3. Reproduced defects with deterministic fixtures and live processes before fixing.
4. Verified every fix with `powershell -NoProfile -File tools/build.ps1 -Target tests -RunTests`
   (serialised build lock; `pwsh` is not installed on this machine) and with
   repeated harness runs to catch timing flakes.
5. Proof-read CLI help / error copy and every test assertion message.

Verification baseline: 41 checks before the audit → **213 checks after**, 0 failed,
~12–13 s wall clock on this machine. `BUILD OK` with 0 warnings for Core, Cli and
Tests (Debug and the same files build in Release).

## Findings

Severity: **critical** = wrong result / crash / data loss; **major** = broken
behaviour, false positives/negatives, flakiness; **minor** = edge case, copy,
performance; **suggestion** = hardening/cleanup.

### AssemblyInspector (`src/Obsidian.Dumper.Core/Inspection/AssemblyInspector.cs`)

| ID | Sev | File:line | Problem | Fix | Verification |
|---|---|---|---|---|---|
| INSP-01 | major | AssemblyInspector.cs:299 (pre-fix: 143) | Metadata strings were collected **after** the `_strings` snapshot was materialised, so they never reached the UI/CLI (dead code); parameters, generic parameters, events, references and resource names were not collected either. | Collect metadata names before materialising; add assembly/module/target-framework names, generics, events, assembly refs and resource names; mark them `metadata`/`reference`/`resource` kinds. | Test "metadata names are collected" (runtime-built probe name so the assertion itself cannot flip the kind); `obsidian inspect --strings` shows metadata entries. |
| INSP-02 | major | AssemblyInspector.cs:714 | `TotalMethodCount` only summed one nesting level, so nested-of-nested methods were lost. | Recursive `CountMethodsRecursive` with cycle/duplicate guard. | Test "nested method total is recursive". |
| INSP-03 | major | AssemblyInspector.cs:169 | `File.ReadAllBytes` uses `FileShare.Read`, so an assembly that is loaded/being written by another process could not be inspected ("file is in use"). | `ReadFileBytesShared` uses `FileShare.ReadWrite \| Delete`, looped read, explicit size guard (2 GB). | Test "file open by another reader is still inspectable" (hold a read/write handle while opening). |
| INSP-04 | major | AssemblyInspector.cs:106 | Malformed/protected modules could throw during `Build` (reference/resource/attribute/section tables) and `Open` leaked the `ModuleDefMD` on failure; there was no non-throwing entry point. | `TryOpen` (readable error), per-phase `Safe*` helpers + diagnostics list, `BadImageFormatException` wrapping, module disposed on failure, `AssemblySummary.Diagnostics`. | Tests: empty file, `MZ` stub, truncated assembly — all rejected with readable errors and no exception; healthy file has no diagnostics. |
| INSP-05 | major | AssemblyInspector.cs:877 | ReadyToRun detection scanned the whole file for `"RTR0"` — random string/payload bytes produced false positives. | Read the CLI header's `ManagedNativeHeader` RVA (offset 64) and verify the `RTR0` magic at the resolved RVA (file *and* mapped layout). | Test "not detected as ready-to-run" on a normal assembly; code path reviewed against `IMAGE_COR20_HEADER`. |
| INSP-06 | minor | AssemblyInspector.cs:1187 | `CustomAttributeCount` counted only type-level attributes; the name implies all tables. | Count assembly, module, type, field, method, property, event, parameter and generic-parameter attributes, each guarded. | Test "custom attributes counted"; spot-check against dnlib counts. |
| INSP-07 | minor | AssemblyInspector.cs:1030 | Section entropy allocated a copy of every section (`new byte[rawSize]` + `BlockCopy`), doubling peak memory for large assemblies. | Compute Shannon entropy directly over the file buffer (`AsSpan(pointer, size)`). | Core builds 0 warnings; entropy tests unchanged; memory behaviour inspected in code. |
| INSP-08 | minor | AssemblyInspector.cs:1215 | Resource kind reported assembly-linked resources as `linked`. | `ResourceType` switch: embedded / assembly-linked / linked / unknown. | Code reviewed against dnlib `ResourceType` enum. |
| INSP-09 | minor | AssemblyInspector.cs:403, :477 | Identity fields (`Machine`, `Kind`, `Mvid`, entry point, metadata version) were read unguarded; a corrupt CLI header could throw out of `Open`. | `SafeMachine`/`SafeSubsystem`/`SafeMvid`/`SafeEntryPoint`/`SafeMetadataString`. | Malformed-input tests; code review. |
| INSP-10 | suggestion | AssemblyInspector.cs:104 | `FindType`/`FindType(MethodEntry)` re-enumerated all types (`GetTypes()`) per disassembly call — O(n) per click on large assemblies. | Token maps (`_typesByToken`, `_methodsByToken`) built during the single type walk; lookups are O(1). | IL formatter tests exercise the path; behaviour identical. |
| INSP-11 | minor | AssemblyInspector.cs:412 | Nested type namespace/depth walk did not guard pathological/corrupt declaring-type chains. | 128-step guards and try/catch fallbacks. | Nested-type tests pass; code review. |
| INSP-12 | minor | AssemblyModels.cs:81 | `FormatBytes` used the current culture (`"1,5 KB"` on sk-SK) and had no TB unit; negative `long.MinValue` could recurse. | Invariant culture, TB/PB units, sign handled without recursion; `EntropyLabel`/entropy evidence also invariant. | Tests for 0/1024/1536/negative/TB; full run on sk-SK culture displays `1.5 KB`. |
| INSP-13 | minor | AssemblyModels.cs:174 | `StringEntry.Display` did not escape backslashes/NUL, so `"\n"` and `"\\n"` were indistinguishable in the inspector. | Escape `\` first, plus `\0`; existing CR/LF/TAB escapes kept. | Test "string display escapes control characters". |
| INSP-14 | suggestion | AssemblyModels.cs:141 | `ReferenceEntry.IsFramework` missed `WindowsBase`, `PresentationCore/Framework` and the bare `System` reference. | Added the missing framework identities (consistent with `RemoteProcess.IsFrameworkName`). | Code review; UI grouping uses the same notion. |
| INSP-15 | minor | AssemblyInspector.cs:179 | `IsManagedFile` read the file without sharing and parsed it twice with no size guard. | Shared reader + file-length bounds + single metadata-root probe before dnlib load. | Test "IsManagedFile accepts a managed assembly" + malformed tests. |

### IlFormatter (`src/Obsidian.Dumper.Core/Inspection/IlFormatter.cs`)

| ID | Sev | File:line | Problem | Fix | Verification |
|---|---|---|---|---|---|
| IL-01 | major | IlFormatter.cs:161, :170 | `Local` operands printed `loc_?` — locals had no index, so `ldloc`-style operands were useless for reading or diffing dumps. | Resolve the `Local` to its index in `CilBody.Variables`, append the name when present. | Deterministic fixtures cover `.locals init` + local operands; tests for locals/IL formatting. |
| IL-02 | major | IlFormatter.cs:85 | Exception-handler rendering dereferenced `TryStart`/`HandlerStart` directly (NRE on truncated bodies) and omitted the filter range. | Null-safe `Label()` helper for every range; filter range shown for `Filter` handlers; EH start/end also added as labels. | Try/catch/finally fixture asserts `// EH`, `Finally`, catch type; code review for filter. |
| IL-03 | minor | IlFormatter.cs:71 | Operand dispatch matched `IMethod` before `MemberRef` field/type refs, so a field `MemberRef` (valid for `ldtoken`) was printed as a method with an empty signature. | Classify `MemberRef` first: field refs → field form, type refs → type name, otherwise method. | Compiles clean; operand classification reviewed against dnlib `MemberRef.IsFieldRef/IsMethodRef`. |
| IL-04 | minor | IlFormatter.cs:70 | Integer operands used culture-sensitive `ToString()`; 12-character opcodes (`constrained.`) had no separating space because `PadRight(11)` did not pad. | Invariant numeric formatting; explicit at-least-one-space padding computed from the opcode width (12). | IL tests compile/format real methods; output inspected. |
| IL-05 | minor | IlFormatter.cs:23 | The output builder started at 4 KB for every method, including 100k-instruction stubs; huge bodies caused repeated growth. | Pre-size from instruction count (capped at 4 MB) — formatting of a large body is now linear with fewer reallocations. | Dumped sample and harness IL formatting; allocation behaviour reviewed. |
| IL-06 | minor | IlFormatter.cs:195 | Failure path returned only `"// IL unavailable: message"` without exception type; escape logic handled control characters one by one and truncated *after* escaping. | Keep partial output, report exception type, truncate first then escape, escape all control chars incl. NUL. | Tests assert truncation (`…`), escaped quote and newline. |

### DecompilerService (`src/Obsidian.Dumper.Core/Inspection/DecompilerService.cs`)

| ID | Sev | File:line | Problem | Fix | Verification |
|---|---|---|---|---|---|
| DEC-01 | major | DecompilerService.cs:68, :97 | Cache key was the path only: after re-dumping a module (same path, new bytes) ILSpy kept returning the **old** C#. | Cache entry carries a file stamp (length + last-write UTC ticks); a changed stamp retires and disposes the stale decompiler. | Tests "cached decompilation is repeatable" + "works after cache reset"; re-dump scenario covered by stamp logic. |
| DEC-02 | major | DecompilerService.cs:112 | `Reset()` cleared the dictionary without disposing `CSharpDecompiler`/`PEFile` → metadata readers leaked until GC. | Dispose cached instances under the lock, guarded by `is IDisposable` (ILSpy 9.1 API verified) and exceptions swallowed per entry. | Test "decompiler works after cache reset". |
| DEC-03 | major | DecompilerService.cs:169 | The universal resolver was rooted only at the main file; dumped folders with sibling modules, runtime directories and GAC paths resolved poorly, degrading output for referenced types. | Seed `UniversalAssemblyResolver.AddSearchDirectory` with the file folder, base dir, runtime directory, Framework/Framework64 and `DOTNET_ROOT/shared` roots that exist. | Decompilation of both a dumped sample and the harness's own assembly succeeds with resolved types. |
| DEC-04 | minor | DecompilerService.cs:222 | Target-framework detection reopened the file (extra IO/parse, fails for locked dumps) and mapped all unknown metadata versions to `.NETCoreApp,Version=v8.0`, including `v4.0.30319`. | Reuse the already-open module; map `v2.0.50727`→v3.5, `v4.0.30319`→v4.8, Core→v8.0, with folder-name sniffing as fallback. | Decompilation tests; framework mapping reviewed against `Metadata.VersionString` values. |
| DEC-05 | minor | DecompilerService.cs:27 | Failure paths (`null` path, missing file) returned unhelpful text; decompile exceptions could surface after the cache was populated. | Explicit readable diagnostics; creation failure removes the cache entry so the next call retries; exception message includes type. | Tests "in-memory module reports a readable diagnostic", "unknown type returns a readable diagnostic". |
| DEC-06 | major | DecompilerService.cs:74 | ILSpy documents that `CSharpDecompiler` instances are **not thread-safe**; the UI decompiles on the thread pool, so two concurrent type opens could corrupt the transform state. | All cache access and every `DecompileTypeAsString` call are serialised through `_gate`; `Reset` also takes the lock. | Code review of ILSpy XML docs (`Instances of this class are not thread-safe`); tests run sequential decompiles. |

### ProtectionAnalyzer (`src/Obsidian.Dumper.Core/Inspection/ProtectionAnalyzer.cs`)

| ID | Sev | File:line | Problem | Fix | Verification |
|---|---|---|---|---|---|
| PROT-01 | major | ProtectionAnalyzer.cs:24 (pre-fix: 11-32) | Generic markers `"Reactor"`, `"Enigma"`, `"PoweredByAttribute"` matched ordinary application types/attributes — false "protector" findings on clean apps. | Replaced with distinctive markers (`Eziriz`, `DotNetReactor`, `.NET Reactor`, `EnigmaProtector`, `Enigma Protector`, `SmartAssembly`, …). | Test "generic words do not raise protector hints" (`ReactorControl`, `EnigmaMachine`, `PoweredByAttribute`, `BabelFish`). |
| PROT-02 | major | ProtectionAnalyzer.cs:74 | Multiple markers for the same protector produced duplicate hints (ConfuserEx, SmartAssembly, .NET Reactor). | Deduplicate by protector display name; evidence names the matched marker. | Test "duplicate protector markers produce one hint". |
| PROT-03 | major | ProtectionAnalyzer.cs:408 | Encrypted-string heuristic flagged ordinary text: spaces were not counted as symbols and any 16-char alphanumeric string of length %4==0 counted as base64. | Whitespace disqualifies prose; strict base64 charset + mixed case + `%4==0`; symbol-gibberish only when alphanumeric content is below half. | Tests: 30 ordinary sentences → no hint; 30 distinct base64 blobs → "Encrypted string table". |
| PROT-04 | major | ProtectionAnalyzer.cs:313 | "Reflection driven loader" matched **any** member named `Load`/`LoadFrom`/`GetTypes`/`GetString` regardless of the declaring type; e.g. `ResourceManager.GetString` + any `.Load` fired on normal apps. | Resolve `MemberRef.Class.FullName` and require the real `System.Reflection.Assembly`/`Module`/`Activator` members. | Clean-assembly test raises no suspicious/critical hints; synthetic protector matrix stays clean. |
| PROT-05 | major | ProtectionAnalyzer.cs:174 | "Stripped method bodies" counted runtime/internal-call (and other no-body) methods as stripped, producing false positives on interop-heavy assemblies. | `MethodEntry.IsRuntime` added from `ImplAttributes`; abstract, P/Invoke and runtime methods skipped. | Tests "runtime methods are not treated as stripped bodies" and "stripped method bodies detected". |
| PROT-06 | minor | ProtectionAnalyzer.cs:196 | Mixed-mode assemblies were marked **suspicious**, although C++/CLI is a legitimate build mode. | Downgraded to `Info` with neutral wording; severity still escalated when combined with other evidence. | Code review; clean-assembly test asserts no suspicious hints. |
| PROT-07 | minor | ProtectionAnalyzer.cs:210 | High entropy in any section (e.g. compressed resources in `.data`/`.rsrc`) was suspicious. | Suspicious only for executable/writable sections; non-code high entropy is `Info`; evidence includes entropy and section flags. | Tests for `.text` (exec) suspicious, `.data` (non-exec) informational. |
| PROT-08 | minor | ProtectionAnalyzer.cs:262 | "Missing core library reference" fired for netmodules and resource-only modules (no assembly refs by design). | Only for real assemblies (`Assembly != null`, `Kind != NetModule`). | Code review; netmodule path unreachable in current dumps. |
| PROT-09 | minor | ProtectionAnalyzer.cs:364 | "No managed entry point" claimed executables without checking the module kind; netmodules were misreported as native exes. | Only `ModuleKind.Console`/`Windows`. | Code review. |
| PROT-10 | minor | ProtectionAnalyzer.cs:120 | `SuppressIldasm` was only searched in assembly attributes; it is also valid on the module. | Check module custom attributes too. | Code review against attribute usage. |
| PROT-11 | suggestion | ProtectionAnalyzer.cs:34 | A single malformed table could abort the whole analysis. | Every phase is individually guarded and returns partial hints; severity ordering kept stable. | Malformed-input group + clean-assembly tests. |

### CLI (`src/Obsidian.Dumper.Cli/Program.cs`)

| ID | Sev | File:line | Problem | Fix | Verification |
|---|---|---|---|---|---|
| CLI-01 | major | Program.cs:700 (pre-fix: 60-70) | Usage banner documented only `list`, `info`, `dump`; `deep`, `inspect`, `token`, `diag`, `readtest`, `modules`, `inject`, `unload` were invisible, and no exit-code/help contract existed. | Complete help with per-command `obsidian help <cmd>`, common options, examples, exit codes, authorised-use note; version banner. | CLI tests: help exit 0, usage line, exit codes, every command mentioned, `inject --help`. |
| CLI-02 | major | Program.cs:760 | Unknown options were silently ignored (`--al` typo ran a full dump). | Strict `ArgReader` with known value/flag tables; `--` terminator; `--key=value`; unknown options → exit 1 with message. | Tests "unknown option exits 1", "unknown option is reported". |
| CLI-03 | major | Program.cs:836 | Option values swallowed the next token even when it was another option (`--out --raw` created a directory named `--raw`). | Value options require a real next value (dash-prefixed rejected except negative numbers) and reject missing values. | Test "missing option value exits 1/reported". |
| CLI-04 | major | Program.cs:520 (pre-fix: 313) | Dump banner printed a mojibake arrow (`â†’`) because UTF-8 bytes were emitted as ANSI. | ASCII `->` in all CLI text. | Help/dump output read with the `read` tool after rebuilding; no non-ASCII in CLI strings. |
| CLI-05 | major | Program.cs:495 (pre-fix: 304) | Module filter contained `!IsFramework \|\| (IsManaged && !IsFramework)` — dead condition that hid intent. | Simplified to `IncludeFrameworkModules \|\| !IsFramework`; native/framework handling verified. | Live dump test dumps exactly the app modules. |
| CLI-06 | major | Program.cs:565 | Long operations ignored Ctrl+C (`CancellationToken.None`). | `Console.CancelKeyPress` → linked CTS for dump/deep/inject/unload; second Ctrl+C aborts; cancellation → exit 130 and a clear message; token passed into the engine. | Code review + cancellation path unit-inspected; exit-code table documented. |
| CLI-07 | major | Program.cs:18 (pre-fix: scattered) | Exit codes were inconsistent (attach 1, dump partial 3, fatal 2, usage 1) and undocumented. | Unified 0/1/2/130 with documented meanings; every command returns those; dump partial failures return 2. | CLI tests assert 1 vs 2 for usage/operation errors; help lists the codes. |
| CLI-08 | major | Program.cs:619 (pre-fix: 48) | `Main` printed the raw exception (`fatal: System.X: … at …`) to users. | `fatal: Type: message`; stack only with `--verbose`/`OBSIDIAN_DEBUG=1`. | CLI error tests assert no stack traces (`   at `) in injection errors. |
| CLI-09 | major | Program.cs:207 (pre-fix: 112) | `inspect` accepted the path only as `args[1]`, so `inspect --type X file.dll` failed; no JSON, no strings, no graceful bad-file path. | Positional path anywhere, `--type`, `--il`, `--strings`, `--json`, readable `TryOpen` failure (exit 2). | CLI tests: `inspect --json`, `inspect --type --il`, missing file, unknown type. |
| CLI-10 | major | Program.cs:669 (pre-fix: 219) | `list` column alignment: `SessionId` was printed unpadded, skewing the table. | Width-aligned session column; header/data verified. | `obsidian list` output proof-read. |
| CLI-11 | minor | Program.cs:503 | `dump --all` with an empty selection printed `dumped 0/0` and exited 0. | Explicit "no modules matched" error, exit 2. | Code review; test not needed for empty frame list. |
| CLI-12 | major | Program.cs:472 | `--overwrite` used the legacy boolean; the engine grew an explicit `DumpOverwritePolicy`. | Map `--overwrite`→Overwrite, `--fail-if-exists`→Fail, `--on-conflict fail\|rename\|overwrite`, default Rename. | Engine API read on disk; CLI compiles against the new enum and the legacy shim. |
| CLI-13 | minor | Program.cs:532 | Progress used `\r` even when output was redirected, polluting logs; leftover text remained after completion. | `\r` only on interactive consoles; redirected output uses lines; progress line erased before results. | Live CLI dump exercised in tests/spot runs. |
| CLI-14 | major | Program.cs:640 (pre-fix: absent) | No `inject`/`unload` commands (task-6). | `inject --pid --dll [--method crt\|ntcreate\|apc] [--json]` + `unload --pid --module` using `DllInjector`; compatibility pre-check, human-readable failures with Win32/NTSTATUS decoded, notes printed, exit 0/2/130. | Live injection + CLI tests (inject → module appears → unload → module disappears, mismatch exit 2, missing DLL). |
| CLI-15 | major | Program.cs:436 (pre-fix: absent) | `modules` command was missing; `info` lacked JSON. | `modules --pid [--managed] [--json]`, `info --pid --json`; both share one table printer. | CLI tests "modules --json", "info --json". |
| CLI-16 | minor | Program.cs:111 | Setting `Console.OutputEncoding` could throw on hosts without a console. | Guarded; commands still work with redirected stdio. | Tests run every CLI command with redirected pipes. |
| CLI-17 | minor | Program.cs:147 | `readtest` accepted a missing `--name` and printed `module not found: ` (blank). | Requires `--pid` and `--name` with usage errors. | Code review; usage errors are covered by the shared exit-code tests. |
| CLI-18 | suggestion | Program.cs:846 | Placeholder `--timeout`/`--filter` options were accepted but unused. | Removed; every accepted option now has a consumer. | Code review of `ValueOptions`/`FlagOptions`. |

### Test harness (`tests/Obsidian.Dumper.Tests/Program.cs`)

| ID | Sev | File:line | Problem | Fix | Verification |
|---|---|---|---|---|---|
| TEST-01 | major | Program.cs:1100 (pre-fix: 155) | Fixed `Task.Delay(1200)` before inspecting the target; slow machines intermittently produced `managed modules detected = 1`. | Poll process inventory (managed predicate), attach with retry, and wait for the application/framework assemblies to load before module assertions. | Full harness run 3× green; the race was reproduced before the fix and is gone afterwards. |
| TEST-02 | major | Program.cs:1000 (pre-fix: 176) | Live-dump output went to `artifacts/tests-tmp` inside the repo and was deleted only on the success path; early returns leaked directories and absolute machine paths. | `%TEMP%/obsidian-tests/<purpose>-<guid>`, deleted in `finally` with retries; child processes killed in `finally` with `WaitForExit`. | No files under the repo after runs; no hardcoded absolute paths in the harness (all derived from `AppContext.BaseDirectory`/`Environment.SpecialFolder`). |
| TEST-03 | major | Program.cs:257 | No tests for the IL formatter (labels, switch tables, EH ranges, locals, truncation/escaping) — the area with the most defects. | Added deterministic fixtures compiled into the harness (`IlFixtures`) and 19 formatter assertions. | 19 new checks. |
| TEST-04 | major | Program.cs:186 | Inspector tests only asserted "identity present"/"no critical hints"; MVID, target framework, nested namespaces, string kinds, FormatBytes and counts were untested. | Added 40+ metadata/string/helper assertions incl. nested-type namespace inheritance and metadata-vs-user string kinds. | 40+ new checks. |
| TEST-05 | major | Program.cs:503 | No malformed-input tests at all; `Open` could throw or hang on junk files. | Empty file, `MZ` stub, truncated assembly, file held open by another reader, `IsManagedFile` negatives. | 9 new checks. |
| TEST-06 | major | Program.cs:392 | No decompiler tests; cache/reset/failure paths unverified. | Decompile a nested type twice (cache), reset and decompile again, unknown type, in-memory module, missing path diagnostics. | 6 new checks. |
| TEST-07 | major | Program.cs:318 | Protection analyzer had no false-positive tests; only "no critical hints" on one assembly. | Synthetic matrix: protector fingerprints + dedupe + generic-word negatives, name mangling, stripped bodies, runtime-method skip, RWX/high-entropy severity calibration, prose vs base64 strings. | 15 new checks. |
| TEST-08 | major | Program.cs:600 (pre-fix: absent) | No CLI tests; the CLI was never exercised as a process. | 45+ process-level checks: help/command help/unknown command/unknown option/missing value/JSON validity/list/info/modules/inspect/il/missing file/exit codes/inject/unload/mismatch; skips gracefully when the CLI binary is absent. | 45+ new checks; also catches the UTF-8/mojibake class of failures via exact string assertions. |
| TEST-09 | major | Program.cs:1060 (pre-fix: 256) | Target process was `Kill(true)`'d without waiting; a following build could race a still-running sample (`MSB3021` lock observed). | `KillProcess` kills and waits up to 5 s. | Harness runs + builds no longer race a live sample; lock observed only from other agents' manual processes. |
| TEST-10 | minor | Program.cs:965 | Sample target discovery only looked in `bin/Debug`; `-Release` runs silently skipped the live group. | Debug and Release candidates. | Code review; Debug verified live. |
| TEST-11 | minor | Program.cs:1127 (pre-fix: 172) | "command line was read" passed when the value was null (weak assertion). | Requires a non-empty command line containing the target name. | Live dump group. |
| TEST-12 | major | Obsidian.Dumper.Tests.csproj | `-Target tests -RunTests` did not build the sample target, so on a clean tree the live tests would fail ("sample target exists"). | ProjectReference to `samples/TestTarget` with `ReferenceOutputAssembly=false` (build dependency only, no build.ps1 change needed). | Build output shows TestTarget built as part of `-Target tests`. |
| TEST-13 | suggestion | Program.cs:63 | No way to run a subset while iterating. | `--filter <text>` group filter; summary reports skipped groups. | Used during the audit; `--filter Protection` runs 15 checks. |
| TEST-14 | suggestion | Program.cs:90 | No runtime visibility. | Summary prints elapsed ms and filtered-group count. | Reports ~13.5 s full run. |

### Samples (`samples/`)

| ID | Sev | File:line | Problem | Fix | Verification |
|---|---|---|---|---|---|
| SAMPLE-01 | major | samples/TestTarget/Program.cs:42 | `Task.Delay(TimeSpan.FromSeconds(seconds))` threw for non-positive/oversized argument — the sample crashed instead of being a stable target. | Clamp to 1..3600 s and flush stdout so the harness sees the banner immediately. | Target starts with `120`/`180` in tests and stays alive. |
| SAMPLE-02 | major | samples/Net48Target/Program.cs:30 | The sample looked up the embedded payload as exactly `"embedded.bin"`, but a default SDK build names resources `<RootNamespace>.<file>`, so the hidden assembly was never found; the read was a single `stream.Read` call that could also truncate the payload. | Discover the payload by resource-name suffix and `CopyTo` into a `MemoryStream`; degrade gracefully with a clear message when no payload is embedded. | Code review; net48 sample cannot be rebuilt through `tools/build.ps1` (see open items), prebuilt target still runs. |
| SAMPLE-03 | suggestion | samples/TestTarget/Program.cs:70 | No deterministic control-flow in the sample to verify IL/EH/nested-type formatting end-to-end after a real dump. | Added `SwitchProbe` (10-arm switch → real `switch` opcode, 10 arms required), `TryCatchProbe` (try/catch/finally) and nested `SecretVault.SaltState`. | Live dump tests assert resolved switch, EH output, and inherited nested namespace. |
| SAMPLE-04 | suggestion | samples/TestTarget/Program.cs | `payload.txt` is embedded but unused; it is valuable as a resource-survives-dump fixture. | Kept deliberately; documented here. | Test "embedded resource survives the dump". |

### Summary by severity

| Severity | Count |
|---|---|
| Critical | 0 |
| Major | 41 |
| Minor | 25 |
| Suggestion | 8 |
| **Total** | **74** |

(Some findings are grouped: e.g. INSP-04/INSP-09/INSP-15 are separate defects in the
same malformed-input robustness area.)

## Verified clean (inspected, no defect found)

- **AssemblyInspector identity**: MVID GUID parsing, strong-name flag/public key
  token extraction, `Machine`/subsystem mapping, deterministic-build timestamp
  guard (no fake 1970/2106 dates), entry-point formatting, assembly
  company/product/version/copyright attributes, target-framework attribute,
  assembly/module/typeref/memberref counts.
- **AssemblyInspector sections**: characteristics bit masks (R/W/X), Shannon
  entropy over raw bytes (verified against hand-computed 2.0 bits for 4 unique
  bytes and 0 for a zero block), SHA-256 lowercase hex (verified 64 chars), file
  size vs actual file length.
- **AssemblyInspector strings**: `ldstr` extraction with per-literal usage counts,
  ordinal dedupe, metadata-vs-user kind precedence (user wins), deterministic
  ordering (length desc, value asc), 20k cap.
- **IlFormatter**: instruction-offset label dedupe (branches, switch tables, EH
  boundaries, filter start), operand cases for IMethod/IField/ITypeDefOrRef/
  `calli` signatures/parameter/local/numeric/float/double/string, ctor return-type
  suppression, token/RVA header, `.locals init` declaration, EH output ordering
  (dnlib metadata order) — all exercised by fixtures.
- **DecompilerService**: ILSpy `LanguageVersion.CSharp12_0` is the newest value in
  ILSpy 9.1 (reflection-checked enum); `ThrowOnAssemblyResolveErrors=false`,
  `RemoveDeadCode/RemoveDeadStores=true`, `UseDebugSymbols=false`,
  `LoadInMemory=false` are all valid ILSpy 9.1 settings; `Shared` is a `Lazy<T>`
  singleton; `BuildFullTypeName` handes nested/generic arity (verified against a
  nested generic-free fixture and the ILSpy type-name rules).
- **ProtectionAnalyzer**: severity ordering (Critical→Suspicious→Info then name),
  evidence text always populated, W+X detection, large-resource info threshold,
  and section flag masks.
- **AssemblyModels**: `EntropyPercent` math, token/RVA labels, kind/visibility
  labels, indentation helpers, `IsProtected` definition (info-only = not
  protected).
- **CLI**: every documented command works (`list`, `info`, `modules`, `dump`,
  `deep`, `inspect`, `inject`, `unload`, `token`, `diag`, `readtest`),
  JSON output parses, stdout/stderr separation is consistent, exit codes match the
  help, no absolute paths in generated messages, Ctrl+C wiring reviewed.
- **Samples**: `TestTarget` metadata is rich enough for end-to-end verification
  (nested types, async state machine, interface/abstract types, hidden dynamic
  assembly) and now includes deterministic IL shapes; `Net48Target` hidden-assembly
  path reviewed.
- **WPF consumers**: `InspectorViewModel`/`Converters` compile unchanged against
  the inspection API; `Open`/`TryOpen` split keeps the UI's
  `BadImageFormatException` handling intact; no public member was removed.

## Verification evidence

- `powershell -NoProfile -ExecutionPolicy Bypass -File tools/build.ps1 -Target core`
  → BUILD OK, 0 warnings.
- `... -Target cli` → BUILD OK, 0 warnings.
- `... -Target tests -RunTests` → BUILD OK; **213 passed, 0 failed** (~12–13 s),
  including live dump of a real .NET 10 process and live
  inject/unload/suspend/resume against a spawned sample.
- Harness groups: PE rebuild, Inspector metadata, IL formatter, Protection
  heuristics, Decompiler, Malformed inputs, CLI surface, Live injection + process
  control, Live dump, Agent asset staging.
- Repeated full runs (3×) green after the flakiness fixes. One intermediate run
  showed the spawned sample target being terminated by an external process during
  the injection group (the engine reported `STATUS_PROCESS_IS_TERMINATING`, i.e.
  another agent's cleanup), which cascaded into process-control failures; the
  group now runs process-control before the best-effort APC check and APC is
  last/isolated, so external interference cannot cascade through deterministic
  assertions.

## Open items / limitations (reported, not silently left)

1. **`samples/Net48Target` has no project file** and is not built by
   `tools/build.ps1`; its `bin/` artefacts predate this audit, so the hidden
   payload is not embedded in the current exe. The runtime sample was fixed to
   find a payload under any manifest-resource name, but a reproducible net48
   build recipe (targeting pack + embedding `HiddenPayload.dll` as `embedded.bin`)
   needs a build-script target — reported to the Lead.
2. `AssemblyInspector.Open(byte[])` expects an **on-disk file image** for exact
   section entropy; loader-mapped buffers are accepted for identity/RTR0
   detection but their section entropy is meaningless (documented on the method).
3. `QueueUserAPC` is best-effort by design (threads must enter an alertable
   wait); the live test asserts the contract and never asserts APC success, per
   the engine contract.
4. Pre-existing `artifacts/test-dumps/` and `artifacts/tests-tmp/` directories in
   the repository were left untouched (outside this task's write scope); the new
   harness only writes to `%TEMP%` and cleans up.
