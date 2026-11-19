# OBSIDIAN 1.1.0 — full audit report

This is the consolidated record of the 1.1.0 audit: what was inspected, every
defect class that was found, what was fixed, and how each fix was verified. The
per-area reports with file/line detail live next to this file:

| Area report | Scope | Owner |
|---|---|---|
| [engine.md](audit/engine.md) | Core engine, PE/PEB maths, Win32 interop, shared PE layer, payload, native loader, `DllInjector`, `ProcessControl` | engine |
| [inspection.md](audit/inspection.md) | Inspection engine (dnlib/ILSpy), CLI, test harness, samples | inspector |
| [ui-shell.md](audit/ui-shell.md) | `App`, shell window, themes, settings infrastructure, shell view models | ui-core |
| [ui-pages.md](audit/ui-pages.md) | Targets / Workspace / Inspector / Activity pages, `CodeView`, converters | ui-pages |

## 1. Methodology

1. **Parallel area audits.** Four auditors read every file in their area against
   the 1.0.0 baseline, hunting for wrong behaviour (not style): P/Invoke and
   struct correctness, PE/PEB arithmetic, resource and handle lifetime, threading
   and cancellation, metadata edge cases, WPF binding correctness (WPF swallows
   binding errors), UI copy, persistence, concurrency and failure paths.
2. **Fix + proof.** Every finding was fixed in place and verified with at least
   one concrete check: a unit-style harness assertion, a live run against a real
   target, a screenshot, a CLI round-trip, or a documented code-path argument
   when a live check was impossible (e.g. hardened hosts). Findings that were
   deliberately left are listed with a reason.
3. **Lead review + integration.** All areas were merged, built together and
   exercised as one product. The Lead ran the GUI, verified both themes, and
   wrote an end-to-end injection acceptance test.
4. **Independent verification.** After the tree was frozen, the engine auditor
   re-verified the release build, native staging, CLI surface, test harness and
   deep-dump path without editing anything.

## 2. Severity summary

| Area | Critical | Major | Minor | Suggestions | Total |
|---|---|---|---|---|---|
| Engine | 3 | 20 | 6 | 6 (documented, intentionally left) | 29 fixed / 35 findings |
| Inspection + CLI + tests | 0 | 41 | 25 | 8 | 74 fixed |
| UI shell + app | 1 | 14 | 14 | 2 | 31 fixed |
| UI pages | 5 | 24 | 18 | 6 (wired in the pro-feature pass) | 53 findings / 46 fixes in the audit pass |
| **Total** | **9** | **99** | **63** | **22** | **187 findings / 180 fixes in the audit pass** |

The six UI-page suggestions became real features during the pro-workflow pass
(task-10), and the six engine suggestions are hardening ideas that are documented
instead of changed on purpose (for example, adding a full PE checksum
recalculation, which is unnecessary for analysis output).

## 3. The critical findings (all fixed)

| # | Area | Finding | Fix |
|---|---|---|---|
| 1 | Engine | `PeImageLayout.Characteristics` read the optional-header offset instead of the COFF header, so **every real DLL was classified as non-DLL** and `.dll`/`.exe` naming and dump rules were wrong. | Read `peOffset + 22`; verified against real DLLs. |
| 2 | Engine | `NtQueryInformationProcessWow64` was P/Invoked as a separate ntdll export — **no such export exists**, so WOW64 command lines were always empty and the query failed silently. | Replaced with the documented `ProcessWow64Information` (26) query; verified on x86 and x64 targets. |
| 3 | Engine | Cross-bitness `QueueUserAPC` delivery into a WOW64 target **destabilised the target loader** (a following remote `FreeLibrary` fast-failed the process). | APC is refused for non-x64 targets before any allocation, with the reason reported; `CreateRemoteThread` stays supported for x64 + WOW64. |
| 4 | UI shell | `AsyncRelayCommand` executed handlers as `async void` with no exception path: any command fault became an unhandled dispatcher exception (crash dialog / lost operation). | Caught as a normal outcome, routed to a shell exception handler that logs and toasts; installed at startup. |
| 5 | UI pages | Workspace "no target" empty state was bound to a sentinel string, so it **could never be displayed** (blank page instead of guidance). | Bound to `HasTarget`; space is now an actionable empty state. |
| 6 | UI pages | `WorkspaceViewModel` contained **19 lines of mojibake** (`â€”` instead of `—`, `â€¦`, `Â·`) that were rendered verbatim in the UI. | Byte-verified repair of every string; encoding confirmed UTF-8 without BOM. |
| 7 | UI pages | The Targets list lost the selection on **every rescan**: `ProcessRecord` is a value-equality record and the ListBox compared by value after a new snapshot was created. | Reference-identity selection with PID restore across refreshes. |
| 8 | UI pages | Filter/search forced `ICollectionView.Refresh()`, which cleared the selection on all three lists and closed the decompiled type in the Inspector. | Selection is preserved/restored through the filters; Inspector keeps the type when it still exists. |
| 9 | UI pages | Inspector had a stale-IL race: a slow disassembly from the previously selected method could overwrite the newly selected method's IL. | Method identity checks before applying results; loading/error states are surfaced. |

## 4. Verification evidence

All checks below were run on the final tree (`2026-10-28`, Windows 10/11 x64,
.NET SDK 11.0.100-rc).

### Builds

| Check | Result |
|---|---|
| `tools/build.ps1 -Target sln -Release` | **BUILD OK**, 0 warnings, 0 errors |
| `tools/build.ps1 -Target app` | **BUILD OK**, 0 warnings, 0 errors |
| `tools/build.ps1 -Target core` / `-Target cli` | BUILD OK, 0 warnings, 0 errors |
| `tools/build.ps1 -Target native` | BUILD OK → `Obsidian.Injector.x64.dll`, 149,504 bytes, SHA-256 `a7ca954a1b5b3470bf6d284e30bc79a163c8cb8dee0140b4f8f6dbc31db56c9d` for the published 1.1.0 folder (MSVC embeds a per-link PE timestamp, so rebuilding from the same sources produces a different hash with identical behaviour) |
| Agent staging | `native\Obsidian.Injector.x64.dll` + `payload\net48\Obsidian.Dumper.Payload.dll` present in Debug and Release app outputs |

### Automated tests

| Suite | Result |
|---|---|
| Engine verification harness (`tools/build.ps1 -Target tests -RunTests`) | **213 passed, 0 failed** (~12.2 s) — PE rebuild, inspector metadata, IL formatter, EH/switch, decompiler, protections, malformed inputs, CLI surface, live injection + process control, live dump, agent assets |
| Injection UI acceptance (`tools/verify-injection-ui.ps1`) | **7 passed, 0 failed** — page reachable, DLL accepted, confirmation dialog opens, OK accepted, module appears in the *target's* module list, unload button enabled, module disappears after unload |
| Same, automated non-interactive path | 6 passed, 0 failed (confirmation pre-disabled) |
| Independent CLI verification (engine auditor) | **19/19 checks** — list/info/dump/inspect/inject/unload incl. `--json`, wrong-bitness rejection (exit 2), bad arguments (exit 1 + usage) |
| Deep dump end-to-end (net48 target) | **Success**, 6 assemblies / 10.6 MB written, including the `Assembly.Load(byte[])` payload that never appears in the module list; target stayed alive |
| UI export helpers (ui-pages harness) | 16/16 — CSV fields/quoting, JSON structure/camelCase, Markdown report escaping |

### Visual verification

`tools/ui-capture.ps1` captures every page in both themes (14 screenshots,
`artifacts/shots/`):

- Dark: targets, workspace, inspector (overview/explorer/strings), injection,
  activity, settings.
- Light (`--theme light`): workspace, inspector, injection, settings, targets.
- Theme acceptance evidence (`artifacts/task7/`): persisted light+cyan theme loads
  before the window, live dark+green switch, restart persistence, maximised
  rectangle equals the monitor work area, Windows 11 Snap Layouts flyout.
- Settings acceptance evidence (`artifacts/task8/`): v1→v2 migration with clamping,
  compact density + engine-block toggle, restart persistence.
- Injection evidence (`artifacts/inject-verify/`): ready state, confirmation
  dialog, result card with remote module base, unloaded state.

## 5. Behavioural changes worth knowing

- Injection writes into a live process and loads native code by design; the
  confirmation dialog defaults to on, the operation is logged, and only native
  DLLs are offered (managed assemblies are detected and explained).
- `NtCreateThreadEx` and `QueueUserAPC` are x64-only in the engine; the UI
  explains this up front instead of failing after allocation.
- Dump output naming is now policy-driven (`Rename` by default, `Fail`,
  `Overwrite`), and parallel dumps can no longer race on the same file name.
- Settings are clamped, migrated and forward-compatible; a corrupt settings file
  falls back to defaults and surfaces a warning instead of breaking a dump.
- Light mode is a first-class theme: every surface, code view, converter and
  window-chrome element resolves palette tokens dynamically.

## 6. Intentionally left / known limits

| Item | Reason |
|---|---|
| Six engine hardening suggestions (e.g. optional PE checksum recalculation) | Not needed for analysis output; documented in `docs/audit/engine.md`. |
| Deep dump for CoreCLR targets | A live CoreCLR cannot host a second runtime in-process by design; the out-of-process engine covers those targets. |
| Deep dump requires the prebuilt native agent + net48 payload | They are staged by the build/publish scripts; without them the feature reports "unavailable" instead of failing. |
| Dump cancellation granularity | Cancellation happens between modules, not inside a single module read; the UI says so. |
| `samples/Net48Target` has no csproj | Prebuilt binary is enough for the deep-dump smoke run; a reproducible net48 build recipe is future work. |
| Cross-bitness injection beyond WOW64 (e.g. ARM64 host → x64 target) | Not tested on this machine; the engine reports unsupported rather than guessing. |
| UI automation of `SaveFileDialog` | Exports are verified by invoking the export helpers directly (16/16) instead of driving the OS dialog. |
| VM-based / virtualised IL protectors | Analysis work, not a click; protector heuristics report evidence. |

## 7. How to re-run the verification

```powershell
cd OBSIDIAN-DUMPER

# serialised build (four agents used the same lock)
powershell -NoProfile -ExecutionPolicy Bypass -File tools/build.ps1 -Target sln
powershell -NoProfile -ExecutionPolicy Bypass -File tools/build.ps1 -Target tests -RunTests

# end-to-end injection through the real UI (exclusive; uses artifacts/.gui-lock)
powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-injection-ui.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-injection-ui.ps1 -WithConfirmation

# dark + light screenshots of every page
powershell -NoProfile -ExecutionPolicy Bypass -File tools/ui-capture.ps1
```
