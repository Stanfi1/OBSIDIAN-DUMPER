# OBSIDIAN engine audit (`engine` scope)

Area owner: `engine` teammate.
Scope audited (read every file, then fixed): `src/Obsidian.Dumper.Core/` **except** `Inspection/`, plus
`src/Obsidian.Dumper.Shared/`, `src/Obsidian.Dumper.Payload/`, `src/Obsidian.Dumper.Native/`.

| | |
|---|---|
| Builds | `tools/build.ps1 -Target core` → **BUILD OK, 0 warnings, 0 errors**; `-Target payload` → BUILD OK; `-Target native` → rebuilt x64 DLL (see below); `-Target app` → **BUILD OK, 0 warnings** (first attempt hit a transient `OBSIDIAN.exe` file lock from another teammate's running app instance, retry clean) |
| Verification harness | Isolated TEMP project referencing the built `Obsidian.Dumper.Core.dll` (never touches `tests/` — inspector owns the shared harness per Lead instruction). **61/61 checks passed** incl. live injection into short-lived throws-away processes, x64 + WOW64 dumps, PE rebuild, and a **deep dump end-to-end** run against a copy of the net48 sample in `%TEMP%`. |
| Native agent | Rebuilt `src/Obsidian.Dumper.Native/bin/Obsidian.Injector.x64.dll`, size **149,504 bytes**, SHA-256 `711f68139faf278fce659dd34707fd735bdf42669b71e9ffeb11dbfb7540ea8a` (MSVC, zero warnings); staged into the app output and exercised by the deep-dump run |
| Tests | `-Target tests -RunTests` intentionally **not** executed by me (shared-harness ownership); inspector validates after the parallel phase. |

## Methodology

1. Read every file in scope line by line; cross-checked P/Invoke signatures/struct layouts against the Microsoft
   headers, and PE/Windows structures against the documented layouts.
2. Built small P/Invoke probes in an isolated TEMP directory to verify platform assumptions empirically instead of
   trusting review: Toolhelp module list layout for a WOW64 target (mixed 64-bit + 32-bit module lists with the
   64-bit `MODULEENTRY32W` layout), existence of ntdll exports, export-RVA parsing from `SysWOW64\kernel32.dll`,
   `NtSuspendProcess`/`NtResumeProcess` + `ProcessThread.WaitReason` behaviour, and all four injection mechanics.
3. Implemented the new injection/process-control surface (task-5) and validated it live (CreateRemoteThread x64 +
   WOW64, NtCreateThreadEx, QueueUserAPC, unload, suspend/resume/terminate).
4. Ran the same live harness again after every fix batch (engine internals, PE rebuild, dump policies,
   `ReloadModules`, PEB command lines for both bitnesses).
5. Fixed only defects reproduced by code inspection or a failing probe/test; anything not verifiable here is
   listed under "intentionally left" instead of being silently changed.

## Summary

| Severity | Fixed |
|---|---|
| Critical | 3 |
| Major | 20 |
| Minor | 6 |
| Suggestion (documented, not changed) | 6 |
| **Total fixed** | **29** |

**Post-audit live verification:** base harness 32 checks + engine/PE/dump 29 checks + 4 deep-dump checks = **61/61
PASS**, including the deep dump (see "Verification evidence").

## Findings

IDs: E##. "Verified" = how the fix was confirmed. Lines are the **fixed** file locations.

| ID | Sev | File:line | Problem | Fix | Verified |
|---|---|---|---|---|---|
| E01 | critical | `Core/Native/NativeMethods.cs:180`, `Core/RemoteProcess.cs:398` | P/Invoke `NtQueryInformationProcessWow64` declared as a separate ntdll export. **No such export exists** → `EntryPointNotFoundException` on every WOW64 target, caught by the outer catch, so command lines of 32-bit processes were *always* null. | Removed the bogus import; use `NtQueryInformationProcess(..., ProcessWow64Information=26, out ULONG_PTR PEB32)` via a correctly typed overload (`EntryPoint = "NtQueryInformationProcess"`). | Smoke: "WOW64 command line parsed from PEB" → `"C:\WINDOWS\SysWOW64\...\powershell.exe" -NoProfile ...` |
| E02 | critical | `Shared/PeImageLayout.cs:93` | `PeHeaders.Characteristics` was read at `optionalHeaderOffset + 18`, i.e. **MajorSubsystemVersion**, not the COFF `Characteristics` field (`peOffset + 4 + 18 = peOffset + 22`). Consequence: every real DLL was reported as "does not carry IMAGE_FILE_DLL", and `ModuleDumper.ResolveExtension` chose `.exe` for extension-less modules; `PayloadEntry.UniqueFileName` had the mirror-image bug (see E23). | Read `ReadU16(image, peOffset + 22)` with an explanatory comment. | Smoke: winhttp.dll parsed as `machine=0x8664 chars=0x2022` (DLL bit `0x2000` set); no more spurious note in injection results |
| E03 | major | `Core/ProcessDiscovery.cs:205` | `Enumerate` did `Inspect(...) ?? Placeholder(...)` and then filtered nulls, but `Inspect` can never return null (every branch returns a record) → dead code path + a second (unused) placeholder record shape. | `Inspect` now returns non-nullable `ProcessRecord` (documented contract); `Placeholder` and the null filter removed; list built directly from the array. | Compiles; smoke `Inspect` on both bitnesses; enumeration used by `Targets` page via UI |
| E04 | major | `Core/ProcessDiscovery.cs:28` | Integrity level was derived by **scanning the token blob for a SID-shaped byte pattern** (`revision=1`, authority byte 16). Fragile: any token payload can contain those bytes; also lost the real sub-authority on unusual labels. | Pin the buffer, read the `PSID` at offset 0 of `TOKEN_MANDATORY_LABEL`, validate it points inside the buffer (fallback to the deterministic `IntPtr.Size`-based SID offset), validate revision/authority, then `GetSidSubAuthority(sid, count-1)` (imports added). | Smoke: x64 and WOW64 both report `Medium` (correct for an unelevated user) |
| E05 | major | `Core/ProcessDiscovery.cs:353` | `FileVersionInfo.GetVersionInfo(module.Path)` was called unguarded inside `DetectClr`; an empty/removed path throws and, because enumeration runs in `Parallel.For`, a single bad module aborted the whole process refresh. | New `TryReadVersionInfo` guard; empty-path handling; `RuntimeDirectory` computed safely. | Smoke: CLR detected on both targets (`.NET Framework 4.8.10008.0`) |
| E06 | major | `Core/ProcessDiscovery.cs:226` | `QueryFullProcessImageNameW` used a fixed 1024-char buffer with no retry; long image paths returned null (no `ImagePath`, no version info). | Retry with the size reported by the API (up to 1 MB, 4 attempts). | Code review + normal paths still resolve |
| E07 | major | `Core/RemoteProcess.cs:129` | `ReadInto` public method did not validate `offset`/`count` against the buffer — an over-long range let `fixed` + native writes run **past the managed buffer** (memory corruption / crash). | Bounds validation with `ArgumentOutOfRangeException`; docs state the contract. | Compiles; all existing call sites use valid ranges (dumps verified) |
| E08 | major | `Core/TargetSession.cs:80` | `ReloadModules` (a) carried `Status/DumpedPath/Note` **only for managed modules**, so a dumped *native* module showed as idle after "Rescan modules"; (b) `_modules.ToDictionary(m => m.Name, ...)` threw `ArgumentException` on targets with two same-named modules (different directories). | Carry state for **every** module by name; duplicate-safe dictionary build; `ManagedModules` cache invalidated. | Smoke: "ReloadModules carries native dump state" passes on x64 + WOW64 (Lead-reported bug) |
| E09 | major | `Core/TargetSession.cs:124` | `ReadModuleImage` cast `module.Size` (uint, up to 4 GB) to `int` after the 512 MB header check — a corrupt header could make the fallback size exceed `int.MaxValue` and produce an empty/invalid buffer. | Reject sizes > `int.MaxValue`. | Code review |
| E10 | major | `Core/Dumping/ModuleDumper.cs:91`, `:131` | (a) Native modules skipped by policy were reported as **failures** and `DumpStatus.Skipped` was never used; (b) `DumpOptions.RebuildPe` (exposed in Settings) was **ignored** — a dead settings switch. | Skip path sets `Skipped` + note, no error log; `RawImage || !RebuildPe` selects raw mode with an explanatory warning. | Code review + build; settings wiring is ui-core's consumer |
| E11 | major | `Core/Dumping/ModuleDumper.cs:267` | Output-path selection was check-then-write (`File.Exists` loop) and wrote with `File.WriteAllBytes` → two parallel dumps of same-named modules could silently overwrite each other; new `Fail` overwrite policy needs atomic reservation. | `WriteOutputFile`: `FileMode.CreateNew` reserves the name for Rename/Fail; Overwrite uses `File.WriteAllBytes`; `Fail` returns clear "already exists" error without touching the file. | Smoke for both targets: Rename picks `_2`, Fail reports `already exists` and leaves bytes identical, Overwrite replaces |
| E12 | major | `Core/Dumping/ModuleDumper.cs:196` | `DumpOne` had no cancellation support; cancellation only prevented *starting* new tasks in `DumpAsync`, an in-flight large dump could not be interrupted. | `DumpOne(..., CancellationToken = default)` with checks between phases; `OperationCanceledException` rethrown (not reported as a dump failure); `DumpAsync` passes its token. | Compiles; smoke runs use default token |
| E13 | major | `Shared/PeImageLayout.cs:26` | `TryReadHeaders` validation was weak: `peOffset + 24` int overflow for corrupt `e_lfanew`, `SizeOfOptionalHeader < 64` accepted truncated optional headers, directory count not bounded by the optional header size. | Long-arithmetic `e_lfanew` check; PE32/PE32+ minimum optional header (96/112) validated **before** reading directories; directory count clamped by both 16 and the optional-header extent. | All existing dumps still parse; smoke PE checks pass |
| E14 | major | `Shared/PeImageLayout.cs:401` | `Rebuild` arithmetic could wrap: `Align` on a corrupt `SizeOfHeaders`/`VirtualSize` could return a tiny value, placing section data over the headers and writing a bogus `SizeOfHeaders`; section raw size/cursor could overflow; skipped sections kept stale `PointerToRawData/SizeOfRawData`; source RVA was cast to `int`. | Saturating `Align`; explicit `SizeOfHeaders ≤ SizeOfImage`, aligned header size, section size and total size limits; skipped sections get zeroed raw fields; `long` source offsets; header region must exist in the mapped buffer. | Smoke: real 23 MB (x64 PE32+) and 21 MB (WOW64 PE32) `mscorlib.ni.dll` dumps rebuilt into valid PE files |
| E15 | major | `Shared/PeImageLayout.cs:141,175,565` | `VirtualAddress + extent` / `RawPointer + delta` uint arithmetic could wrap for corrupt sections, mis-mapping RVAs (and returning bogus file offsets). | Subtraction-form range checks; `ulong` file-offset arithmetic with `int.MaxValue` guard; `Sections == null` guard. | Code review; metadata parse paths still work (smoke dumps) |
| E16 | major | `Shared/PeImageLayout.cs:661` | `PeHeaders.ImageBase` was `uint`, so x64/ARM64 image bases were **truncated** on every parse (a future consumer would compute wrong addresses). | Added `ulong ImageBase64` (full value); `ImageBase` kept for compatibility as the low 32 bits. | Code review; no existing consumers of `ImageBase` |
| E17 | minor | `Shared/PeImageLayout.cs:574` | `MachineToArchitecture` returned undocumented ad-hoc codes (`0..3`, `255`) that looked like `ProcessArchitecture` but were not; unused dead public API. | New documented `PeArchitecture` enum; method returns it. | Build; grep confirmed no callers |
| E18 | major | `Core/Injection/DllInjector.cs:759` | `GetExitCodeThread` returns a **DWORD**, so an x64 `HMODULE` from `LoadLibraryW` was truncated (`0xDA5B0000` instead of `0x7FFFDA5B0000`) → wrong `RemoteModuleBase`. | Resolve the authoritative base from the target module list by bitness + file name and match the low 32 bits of the exit code; fall back to the exit code with a documented note. | Smoke: base reported `0x7FFFDA5B0000` (full pointer) and matches the loader's module entry |
| E19 | critical | `Core/Injection/DllInjector.cs:569` | **QueueUserAPC into a WOW64 target corrupts the target.** Live testing: 32-bit `LoadLibraryW` APCs loaded the DLL, but a following remote `FreeLibrary` thread exited `0xC0000409` (fastfail) and the target terminated; Toolhelp module snapshots also became unavailable. Root cause is cross-bitness APC delivery/loader interaction. | `QueueUserApc` is now **x64-target-only**, rejected before any allocation or APC queuing, with an honest explanation and a pointer to `CreateRemoteThread`. `CreateRemoteThread` remains fully supported on WOW64. | Smoke: WOW64 APC returns the guidance message, target stays alive; x64 APC still loads/unloads |
| E20 | major | `Core/Injection/DllInjector.cs` | `NtCreateThreadEx` method selection was rejected only *after* kernel32/export resolution, producing a misleading "kernel32.dll was not found" on WOW64. | Early method/architecture rejection in `InjectCore` (before export resolution/`VirtualAllocEx`), plus a defence-in-depth check in the worker. | Smoke: "NtCreateThreadEx on WOW64 target rejected with guidance" |
| E21 | major | `Core/Injection/DeepDumper.cs:438` | Manifest `Unescape` replaced short escapes before `\\\\` (and vice versa), corrupting Windows paths: `C:\temp\new` became `C:<TAB>emp<newline>ew`. Deep-dump manifest fields (assembly/module/location) were affected. | Single left-to-right escape decoder; unknown escapes preserved. | Deep dump end-to-end passed: manifest read back with 6 assemblies and correct names/sizes (`HiddenPayload`, `mscorlib`, `System`, `System.Core`, …) |
| E22 | major | `Payload/PayloadEntry.cs:202` | Deep-dump agent copied a loader-mapped image with one `Marshal.Copy` over `SizeOfImage`; reserved gaps (normal for 0x2000-aligned .NET images) fault. The resulting `AccessViolationException` is not catchable under .NET Framework 4 by default → **could crash the inspected process**. | Page-wise copy via `VirtualQuery`: committed/readable regions are copied, reserved/no-access/guard pages stay zero-filled. | `-Target payload` BUILD OK; deep dump end-to-end dumped 6 assemblies (10,605,568 bytes total) from a live net48 target, including the byte[]-loaded hidden assembly — no target crash |
| E23 | minor | `Payload/PayloadEntry.cs:269` | Extension picker used `Characteristics & 0x0002` (IMAGE_FILE_EXECUTABLE_IMAGE — set for DLLs too) instead of `0x2000` (IMAGE_FILE_DLL), so extension-less modules were saved as `.exe`. | Use `0x2000` (with E02 fixed this is now meaningful). | `-Target payload` BUILD OK; code review |
| E24 | major | `Core/Diagnostics/Logging.cs:66` | `LogBus.Write` invoked subscribers directly; one throwing subscriber (e.g. UI bridge during shutdown) propagated into the engine operation that was merely logging progress — a dump could be aborted by a log handler. | Iterate `GetInvocationList()` and isolate each subscriber in try/catch; logging is best effort by contract. | Code review + build |
| E25 | minor | `Core/RemoteProcess.cs:318` | `DescribeMemory` skipped regions with `(int)(RegionSize / 0x1000)`; a huge region could wrap the loop counter negative → infinite/incorrect output. | Clamp skip to `int.MaxValue`. | Code review (diagnostics-only path) |
| E26 | minor | `Core/RemoteProcess.cs:443` | `UNICODE_STRING.Length` was used unvalidated; a bogus odd length produced a trailing replacement char / mis-decoded command line. | Mask `length & ~1` in both 64- and 32-bit readers. | Smoke: command lines decode correctly on both bitnesses |
| E27 | major | `Native/Obsidian.Injector.cpp:251` | `DllMain` ignored `CreateThread` failure: agent never started, load reported success, host waited the full 90 s result timeout before a generic error. | `DllMain` returns `FALSE` when the agent thread cannot be created, so `LoadLibraryW` fails immediately and `DllInjector` reports "loader rejected" deterministically. | `-Target native` rebuilt: BUILD OK, 0 warnings, new DLL staged (see header) |
| E28 | minor | `Native/build-native.ps1:35` | `-Configuration` and `-Architecture` parameters were completely ignored (always Release/x64 compile flags) — misleading API and a trap for future contributors. | `ValidateSet`; Debug uses `/Od /Zi /DEBUG /OPT:NOREF`, Release uses `/O2 /DEBUG:NONE /OPT:REF /OPT:ICF`; non-x64 rejected explicitly. | `-Target native` rebuilt with Release default, 0 warnings |
| E29 | minor | `Core/ProcessDiscovery.cs:441` | `VersionCache` grew without bound (a long-running UI session refreshing many distinct image paths). | Bounded to 4096 entries (cleared when exceeded). | Code review |

### Task-5 new components (injection + process control)

`Core/Injection/DllInjector.cs`, `Core/ProcessControl.cs`, `Core/Native/Win32ErrorText.cs` are new. Live validation
(throw-away `powershell.exe` targets started by the harness, killed immediately afterwards — never the shared
sample app):

* x64 target: `IsCompatible`, CreateRemoteThread `LoadLibraryW` (winhttp.dll) → Success + full HMODULE; remote
  `FreeLibrary` unload → module disappears; NtCreateThreadEx (dbghelp.dll) → Success with `NTSTATUS 0`;
  QueueUserAPC (version.dll) → module observed; dead pid / missing DLL / architecture mismatch / unload-not-loaded
  all fail with honest messages and `Win32Error` where meaningful.
* WOW64 target: CreateRemoteThread resolves the 32-bit kernel32 base + `SysWOW64` export RVA, `VirtualAllocEx`
  returns a below-4 GB buffer, remote LoadLibraryW returns a 32-bit HMODULE, unload works; target stays alive.
  QueueUserAPC and NtCreateThreadEx are rejected with guidance (see E19/E20).
* `ProcessControl`: suspend → `IsSuspended` true; resume → false; non-existent pid reports "The process with id
  … does not exist."; terminate on a dedicated target → process gone.

`QueueUserAPC` caveat (documented in XML docs + `Notes`): delivery only happens when a target thread enters an
alertable wait, so the result is asynchronous — `Success` is reported only when the module is observed within ~5 s;
otherwise the failure message says the DLL may still load later. The remote path buffer is intentionally **not**
freed for the APC variant because pending APCs may still reference it (released when the process exits).

### Requested additions folded into the audit pass

* `DumpOptions.StripBoundImports` (default true) now feeds `ToRebuildOptions()` instead of the hardcoded value.
* `DumpOverwritePolicy { Rename, Overwrite, Fail }` + `DumpOptions.OverwritePolicy`; legacy bool
  `DumpOptions.Overwrite` maps onto the policy (`true`→Overwrite, `false`→Rename) so existing callers keep behaviour.
* `DumpOptions.WorkDirectory` honoured by `DeepDumper.CreateWorkDirectory`: creates `<WorkDirectory>\<guid8>` (parent
  created if needed) and falls back to the historical candidate list with a warning when the configured path is
  unusable; null/empty keeps the old behaviour.
* `TargetSession.ReloadModules` native-state carry-over + duplicate-name safety (E08).

### Public API changes (all deliberate, consumers informed)

1. `ProcessDiscovery.Inspect(int, CancellationToken)` → **`ProcessRecord`** (was `ProcessRecord?`). Annotation-only
   change; it never returned null, so callers keep compiling.
2. `PeHeaders` gained **`ulong ImageBase64`**; `MachineToArchitecture` now returns the documented **`PeArchitecture`**
   enum (no callers existed).
3. `DumpOptions` gained `StripBoundImports`, `OverwritePolicy`, `WorkDirectory`; `Overwrite` is now a compatibility
   shim over the policy.
4. `ModuleDumper.DumpOne` gained an optional `CancellationToken` parameter (source/binary compatible).
5. `RemoteProcess.ReadInto` now throws `ArgumentOutOfRangeException` for out-of-buffer ranges (it used to corrupt
   memory); valid callers are unaffected.
6. New public types (task-5): `InjectionMethod`, `InjectionResult`, `DllInjector`, `ProcessControl`.

Final task-5 API contract (stable, consumed by the Injection page/CLI):

```csharp
namespace Obsidian.Dumper.Core.Injection;

public enum InjectionMethod { CreateRemoteThread = 0, NtCreateThreadEx = 1, QueueUserApc = 2 }

public sealed class InjectionResult
{
    public bool Success { get; init; }
    public InjectionMethod Method { get; init; }
    public string Message { get; init; }
    public uint Win32Error { get; init; }
    public uint NtStatus { get; init; }
    public ulong RemoteModuleBase { get; init; }
    public int DurationMs { get; init; }
    public IReadOnlyList<string> Notes { get; init; }
}

public sealed class DllInjector
{
    public DllInjector(ILogSink? log = null);
    public InjectionResult Inject(int processId, string dllPath,
        InjectionMethod method = InjectionMethod.CreateRemoteThread, CancellationToken cancellationToken = default);
    public InjectionResult Inject(TargetSession session, string dllPath,
        InjectionMethod method = InjectionMethod.CreateRemoteThread, CancellationToken cancellationToken = default);
    public InjectionResult Unload(int processId, string moduleName, CancellationToken cancellationToken = default);
    public static bool IsCompatible(int processId, string dllPath, out string? reason);
}

namespace Obsidian.Dumper.Core;

public static class ProcessControl
{
    public static bool TrySuspend(int processId, out string? error);
    public static bool TryResume(int processId, out string? error);
    public static bool TryTerminate(int processId, out string? error);
    public static bool IsSuspended(int processId);
}
```

## Verified clean (inspected, no defect found)

* **P/Invoke struct layouts**: `PROCESSENTRY32W`, `MODULEENTRY32W` (incl. mixed WOW64 module snapshots),
  `THREADENTRY32`, `MEMORY_BASIC_INFORMATION64`, `PROCESS_BASIC_INFORMATION`, `PROCESS_MEMORY_COUNTERS`, `FILETIME`
  — verified empirically against real processes (Toolhelp counts/names/paths, memory counters, x64 + WOW64).
* **P/Invoke flags/constants**: `OpenProcess` access masks, `TH32CS_*`, `MEM_*`, `PAGE_*`, `WAIT_*`,
  `STILL_ACTIVE`, thread access. `OpenProcess` rights are minimal for read paths; injection opens extra rights only
  for the short-lived handle.
* **ProcessDiscovery**: snapshot creation/close in `finally`, PID filter, `Parallel.For` cancellation, managed-first
  sort, progress reporting, CLR detection module matching (`coreclr.dll`/`clr.dll`/`mscorwks.dll`), runtime version
  trimming, `IsFrameworkPath`/`IsFrameworkName` heuristics.
* **RemoteProcess**: access-denied retry with `PROCESS_QUERY_LIMITED_INFORMATION`, two-pass architecture detection
  (`IsWow64Process2` → `IsWow64Process` fallback), `IsAlive` via `GetExitCodeProcess` (avoids needing SYNCHRONIZE),
  `WriteBytes` contract, `ProbeManagedImage` bounds, PEB x64 offsets (`ProcessParameters +0x20`, `CommandLine +0x70`)
  and WOW64 offsets (`PEB+0x10`, parameters `+0x40`) — both empirically verified.
* **Sparse-page `ReadInto` design**: 1 MB chunk, partial-copy advance, 256 KB window page-by-page fallback,
  zero-filled gaps — validated by dumping 20 MB+ managed images with reserved gaps into valid PEs.
* **TargetSession**: single handle ownership/dispose, `Dispose` idempotence, module-state carry-over semantics.
* **ModuleDumper**: header/mapped reads, unreadable-page warning, strong-name invalidation through the file-based
  CLI header, metadata verification (`ModuleDefMD`) integration, SHA-256, filename template/sanitisation,
  `{pid}`/`{base}`/`{ext}`/`{module}` substitution.
* **PeImageLayout**: CLI header (COR20) and metadata root parsing including stream-table alignment tolerance,
  section-table parse, `IsManaged`/`LooksLikeImage` probes, `DescribeMachine`, `PeRebuildOptions` defaults.
* **Logging**: ring buffer capacity clamp, thread-safe snapshot, `NullLogSink`.
* **Payload**: manifest version header, escaping scheme (with the E21/E23 fixes), per-assembly enumeration,
  dynamic/unreadable classification, file fallback for single-module assemblies, log fallback in `finally`,
  entry-point contract (`public static int DumpAll(string)`).
* **Native loader**: job-file line reading (`\r\n` handling), result file write ordering (result written *after*
  `ExecuteInDefaultAppDomain` returns, so the host never reads a partial manifest), CLR hosting fallbacks
  (`CLRCreateInstance` → `ICLRRuntimeInfo` → `ICLRRuntimeHost`), CoreCLR detection, `DisableThreadLibraryCalls`,
  loader-lock-safe "keep the DLL loaded" policy.
* **Injection**: architecture compatibility matrix (native machine vs target, managed AnyCPU IL-only note),
  SysWOW64 vs System32 path selection, export-RVA parsing from on-disk kernel32, 15 s loader-thread timeout with
  cancellation polling, remote buffer freed only after a completed thread (kept + documented when the thread is
  still running), handles always closed in `finally`, no path throws.
* **ProcessControl**: NTSTATUS mapping, `PROCESS_SUSPEND_RESUME` rights, `PROCESS_TERMINATE` for terminate,
  access-denied text, `IsSuspended` best-effort note (validated with a live suspend/resume).

## Intentionally left (with reason)

| Item | Why |
|---|---|
| `TargetSession.Attach` opens the process twice (`Inspect` then its own handle) | Two cheap `OpenProcess` calls; refactoring would change the public inspection contract for all callers for no functional gain. |
| `DumpAsync` lets an invalid `OutputDirectory` throw before task start | Callers (UI/CLI) validate the folder; a thrown `IOException` with the real path is more useful than synthesising per-module failures. |
| `DllInjector.Unload` opens with VM write/operation rights although only `CREATE_THREAD`/query are needed | Keeps a single `RemoteProcess` opening path; works for normal and elevated/admin targets; a narrower overload can be added later without breaking the API. |
| Deep-dump work directories are not deleted after a run | The agent DLL intentionally stays loaded in the target (unloading from an agent thread risks the loader lock), and the payload may write logs after the result appears; deleting the folder could break late diagnostics. |
| `LogBus.CreateObservableMirror` cannot unsubscribe and `Clear()` does not notify mirrors | Signature/ownership belongs to the UI shell (ui-core); the mirror is created once per session. Documented here for the UI audit. |
| `PayloadEntry` may overwrite same-named files when the deep-dump output directory is reused | The deep-dump directory is a per-run work directory in normal operation; changing the protocol would require host-side contract changes. |
| `ReadJobLine` in the native agent truncates a single job line at 4096 bytes | Job paths are short (`%TEMP%`/Documents work dir + fixed file names); agent is x64-only and the host controls the job file. |
| `ProcessControl.IsSuspended` remains a heuristic (per-thread wait reason) | No documented public API exposes a process-wide suspend count; the heuristic was validated live (all threads report `Suspended` after `NtSuspendProcess`, none after resume) and errs on the safe side (returns false if unsure). |

## Verification evidence

* `pwsh`-equivalent builds via `powershell -NoProfile -ExecutionPolicy Bypass -File tools/build.ps1 -Target core`
  → BUILD OK, 0 warnings, 0 errors (serialised build lock).
* `-Target payload` → BUILD OK, 0 warnings.
* `-Target native` → BUILD OK, 0 warnings → `Obsidian.Injector.x64.dll` 149,504 bytes,
  SHA-256 `711f68139faf278fce659dd34707fd735bdf42669b71e9ffeb11dbfb7540ea8a`.
* Isolated live harness (TEMP project referencing the built Core DLL, own short-lived targets, no shared sample):
  **61/61 PASS**, including: injection x64 (CRT/NtCreateThreadEx/APC + unload), injection WOW64 (CRT + unload),
  architecture-mismatch and failure-path messages, `IsCompatible`, suspend/resume/`IsSuspended`/terminate,
  `Inspect` + `Attach` on both bitnesses, PEB command lines (x64 + WOW64), integrity level parsing, PE header
  `Characteristics`, memory dump + PE rebuild of real 64-bit **and** 32-bit managed images, `Rename`/`Fail`/
  `Overwrite` dump policies, and `ReloadModules` native dump-state carry-over.
* **Deep dump end-to-end** (rebuilt native agent + net48 payload + `DeepDumper.RunAsync`, target = a copy of
  `samples/Net48Target` started from `%TEMP%`, never the shared sample bin): **Success, 6 assemblies /
  10,605,568 bytes** dumped from inside the target — `HiddenPayload` (a byte[]-loaded assembly that is invisible to
  the module list), `mscorlib`, `Obsidian.Dumper.Payload`, `ObsidianNet48Target`, `System`, `System.Core`; all files
  written and readable, no target crash.
* `-Target app` retry after the other teammate's app instance closed → **BUILD OK, 0 warnings** with the rebuilt
  native agent and payload staged under `native/` and `payload/net48/`.
* Not run by me: the shared test harness (`-Target tests -RunTests` — owned by `inspector` per Lead instruction).
