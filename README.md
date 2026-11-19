# OBSIDIAN

**A serious .NET dumper for Windows.** Live process inventory, memory-image
reconstruction, a managed agent for hidden assemblies, a DLL injection stack with
three techniques, a live memory map, page-walk assembly carving, anti-dump
metadata repair, byte scanner and a ClrMD runtime snapshot, and an ILSpy-powered
inspector that turns a running process into readable C#, IL and metadata —
wrapped in a themed WPF workspace that works in dark and light mode.

Current version: **1.4.1** · Windows 10/11 x64 · MIT licensed.

Repository: <https://github.com/Stanfi1/OBSIDIAN-DUMPER>

```
  ██████  ██████  ███████ ██ ██████  ██  █████  ███    ██
 ██    ██ ██   ██ ██      ██ ██   ██ ██ ██   ██ ████   ██
 ██    ██ ██████  ███████ ██ ██   ██ ██ ███████ ██ ██  ██
 ██    ██ ██   ██      ██ ██ ██   ██ ██ ██   ██ ██  ██ ██
  ██████  ██████  ███████ ██ ██████  ██ ██   ██ ██   ████
```

![OBSIDIAN in action — 15 second tour](docs/demo/obsidian-demo.gif)

*15-second tour: live process inventory, module map, memory carve, C#/IL inspector
and both themes · [MP4 version](docs/demo/obsidian-demo.mp4)*


## What it is

OBSIDIAN combines the classic .NET dumping strategies with a modern operator
workflow:

1. **Out-of-process image reconstruction** (ExtremeDumper class): the target is
   never suspended and no code is injected. The loader-mapped image is read with
   `ReadProcessMemory`, reserved gaps are handled per page, and a valid PE file is
   re-serialised with correct section offsets, `SizeOfHeaders`, checksum and
   strong-name flags.
2. **In-process managed agent** (MegaDumper class, optional): a dependency-free
   native DLL is loaded into a .NET Framework target with
   `CreateRemoteThread(LoadLibraryW)`. Its `DllMain` hands control to
   `ICLRRuntimeHost::ExecuteInDefaultAppDomain` in the target, which runs a tiny
   reflection-only payload that enumerates `AppDomain.CurrentDomain.GetAssemblies()`
   and dumps every module it can see — including assemblies loaded from byte arrays.
3. **DLL injection**: a first-class injection page and engine API. Pick a native
   DLL, get a live architecture/compatibility verdict, choose
   `CreateRemoteThread`, `NtCreateThreadEx` or `QueueUserAPC`, read the remote
   module base / Win32 error / NTSTATUS / duration, and unload the library again.

Everything the engine produces can be inspected immediately: metadata, type and
method inventory, IL with control-flow targets, decompiled C#, string literals,
references, resources, section entropy and protector fingerprints.

## Architecture

![OBSIDIAN architecture](docs/diagram/architecture.png)

The default dump path is **out-of-process and read-only**: the loader map is read
with `ReadProcessMemory`, rebuilt into a valid PE file on disk, verified with
dnlib and hashed. The **managed agent** is an optional in-process path for .NET
Framework targets — it finds assemblies that never appear in the loader list
(including `Assembly.Load(byte[])`). **DLL injection** is the only write-capable
path and always requires an explicit action. Everything the engine produces
opens in the Inspector or exports to CSV/JSON/Markdown.

More detail: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) ·
[demo video (MP4)](docs/demo/obsidian-demo.mp4).

## Features

### Targets
- Live process inventory with CLR detection (.NET Framework and .NET 5+),
  architecture, integrity level, session, company/version, working set, start time
  and command line read from the target PEB.
- Filters: managed, x86, x64, elevated; free text search across name, pid, path,
  company and runtime; sortable columns.
- Auto-refresh (off / 2 / 5 / 10 / 30 s, persisted), optional lazy command-line
  loading, highlight-elevated and show-integrity options.
- Context menu: attach, attach + dump all managed modules, copy PID/name/path/
  command line, open file location, suspend, resume, terminate (with confirmation).
- CSV / JSON export of the filtered view.
- Access problems are surfaced instead of hidden (`Access denied — higher
  integrity or protected process`).

### Dump engine
- Module map from the loader (`Toolhelp32`), managed detection by parsing the
  in-memory CLI header, metadata root (`BSJB`) and runtime version.
- Sparse image reads: .NET assemblies map with `0x2000` section alignment, which
  leaves *reserved* gaps inside the image; `ReadProcessMemory` aborts on them.
  OBSIDIAN reads page-wise where the bulk read fails and keeps every committed byte.
- PE rebuild: sections are re-serialised on `FileAlignment` while RVAs are
  preserved, so imports, entry point and metadata stay valid.
- Options: verify with dnlib, invalidate the (now meaningless) strong-name
  signature, strip stale debug directories, strip bound imports, include
  framework/native modules, raw mapped-image mode for forensics, file-name
  templates, overwrite policy (`Rename`/`Fail`/`Overwrite`), parallel workers.
- Parallel, cancellable batch dumping with progress and per-module status, a dump
  summary (dumped/failed/bytes/ms) and CSV/JSON module export.

### Managed agent (deep dump)
- x64 .NET Framework 4.x targets; CoreCLR targets are reported as unsupported
  by design (a live CoreCLR cannot host a second runtime in-process — the
  out-of-process engine handles those).
- Job file protocol, result/HRESULT reporting, manifest of everything written.
- Fails gracefully when a hardened machine disables CLR hosting
  (e.g. `0x80131023`) instead of breaking the session.

### Injection
- Three techniques with honest descriptions: `CreateRemoteThread` (most
  compatible), `NtCreateThreadEx` (x64, bypasses user-mode hooks) and
  `QueueUserAPC` (x64, best effort — the library loads when a thread enters an
  alertable wait).
- Architecture validation before any allocation; x86 DLLs into WOW64 targets are
  supported through the real 32-bit `LoadLibraryW` address (x64 host).
- **Managed assembly injection**: run a .NET Framework 4.x x64
  `static int Method(string)` entry point through the native CLR host; the
  result panel reports the CLR HRESULT and method return value.
- Managed DLLs passed to the native picker are recognised and explained instead
  of producing a cryptic error 193.
- Unload (`FreeLibrary`) with module-name matching, bitness-aware.
- Confirmation dialog, opt-out setting, per-session history, copyable report,
  Activity log and toasts.
- Process control: suspend / resume / terminate / suspended probing.

### Inspector
- Assembly identity, target framework, entry point, MVID, strong-name state,
  section table with Shannon entropy and `r/w/x` flags.
- Type inventory grouped by namespace (nested types included), methods with
  signatures and IL sizes, fields, references, embedded resources with sizes.
- IL listing with resolved member names, branch labels, locals and exception
  handler ranges.
- C# decompilation through ILSpy 9.1 with assembly resolution against the
  original location, GAC and runtime directories; save to `.cs`.
- String literals extracted from IL (`ldstr`) plus metadata names, with usage
  counts and short-string filtering.
- PE headers, embedded resources with previews and the raw hex view.
- Protector heuristics: ConfuserEx, .NET Reactor, SmartAssembly, Babel,
  Eazfuscator, Dotfuscator, Themida/VMProtect markers; bodyless methods, mixed
  mode images, RWX sections, high-entropy sections, encrypted-looking literals,
  reflection-driven loaders.
- Open assemblies from disk, manage recent captures, in-code highlight search,
  expand/collapse types, copy IL/C#/strings/metadata, export a Markdown or JSON
  report.

### Memory & binary inspection
- **Assembly carving** (Memory → CARVE): page-walk over committed memory,
  mapped/file PE conversion, dnlib validation, SHA-256 deduplication and a
  per-assembly save into `carved/`. Finds manual mappings outside the module
  list.
- **Live memory map** of the attached process: base, size, protection, type,
  owning module and flags; committed regions by default with Modules /
  Executable / Suspicious / Reserved filters, search and a committed-bytes
  summary. Dump a region (up to 256 MB), copy its base or open it in hex.
- **Byte scanner** over committed readable memory: text, UTF-16 and hex patterns
  with `??` wildcards, case-insensitive ASCII, executable-only, progress,
  cancellation and hit context; bounded at 512 MB and 2,000 hits.
- **ClrMD runtime snapshot**: runtime version, threads, modules, object count,
  heap size and walkability, plus top types by count, size and heap share, under
  a 10 s budget.
- **PE headers**: machine, subsystem, timestamp, image/header sizes, section
  table with Shannon entropy and flags, data directories and CLR metadata.
- **Resources**: embedded and linked resources with text, image and hex previews,
  copy and save.
- **Hex viewer** for assemblies, regions and scan hits: offset / hex / ASCII
  columns, text or hex search with wildcards, go-to-offset, themed window.
- **Anti-dump metadata repair** (Settings → Dumping): when a managed dump has a
  wiped COR20 directory or BSJB root, the runtime metadata is copied back through
  ClrMD and the patched file is re-validated with dnlib.

### Themes, settings and experience
- A purpose-built **OBSIDIAN visual identity**: graphite surfaces, hairline
  structure, obsidian-red accent and a dense, data-first layout.
- Dark / light / follow-Windows themes with five accent presets (Ember by
  default), switchable live without a restart; the window chrome follows the
  theme.
- 40+ persisted, validated settings: dumping policy, overwrite mode, parallel
  threads, auto-open output folder, deep-dump work directory, process options,
  injection defaults, activity limits, diagnostics — with forward-compatible
  schema migration and a one-click reset.
- Custom WPF shell: 224px sidebar, command bar, flat panels, compact data tables,
  inspector split view, bottom-right toasts, status bar, page transitions (can be
  disabled).
- `Ctrl+1…7` page switch, `Ctrl+F` filter, `Enter` attach, `Ctrl+D` dump
  selected, `Ctrl+R` reload, `F5` refresh, `Esc` dismiss toasts / clear filters.
- Structured activity log with level filters (Trace/All, Info+, Warn+), search,
  copy, export and timestamps.
- Settings persist per user and degrade gracefully on locked-down machines.
- Headless CLI with the same engine for scripting.

## Screenshots

| | |
|---|---|
| ![Targets](docs/screenshots/targets.png) | ![Workspace](docs/screenshots/workspace.png) |
| Live process inventory with CLR detection | Module map, dump policy and per-module details |
| ![Injection](docs/screenshots/injection.png) | ![Overview](docs/screenshots/inspector-overview.png) |
| DLL injection with compatibility verdict | Assembly overview, entropy and protector hints |
| ![Explorer](docs/screenshots/inspector-explorer.png) | ![Strings](docs/screenshots/inspector-strings.png) |
| Types, methods, decompiled C# and IL | Literal extraction from the dumped IL |
| ![Memory map](docs/screenshots/memory-map.png) | ![Memory scan](docs/screenshots/memory-scan.png) |
| Live address space with module/executable/suspicious filters | Read-only byte scan with context and hit details |
| ![Runtime](docs/screenshots/memory-runtime.png) | ![Hex](docs/screenshots/hex-viewer.png) |
| ClrMD heap snapshot by type, size and share | Hex viewer for assemblies, regions and scan hits |
| ![Carve](docs/screenshots/memory-carve.png) | ![Managed injection](docs/screenshots/managed-injection.png) |
| Page-walk recovery of assemblies outside the module list | Managed entry point executed in a .NET Framework target |
| ![PE](docs/screenshots/inspector-pe.png) | ![Resources](docs/screenshots/inspector-resources.png) |
| PE headers, section entropy and data directories | Resource previews with save and copy |
| ![Settings](docs/screenshots/settings.png) | ![Activity](docs/screenshots/activity.png) |
| Dumping, process, injection and activity settings | Structured engine log with level filters |
| ![Workspace light](docs/screenshots/workspace-light.png) | ![Targets light](docs/screenshots/targets-light.png) |
| The workspace in the light theme | The target inventory in the light theme |
| ![Injection light](docs/screenshots/injection-light.png) | ![Inspector light](docs/screenshots/inspector-overview-light.png) |
| DLL injection in the light theme | The inspector in the light theme |
| ![Settings light](docs/screenshots/settings-light.png) | |
| The settings page in the light theme | |

## Requirements

| | |
|---|---|
| **OS** | Windows 10 or Windows 11, x64 |
| **Self-contained ZIP** | Nothing else — `OBSIDIAN-1.4.1-win-x64.zip` is ready to run |
| **Framework-dependent ZIP** | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) — `OBSIDIAN-1.4.1-win-x64-framework-dependent.zip` is smaller but needs it |
| **Building from source** | [.NET 10 SDK](https://dotnet.microsoft.com/) (or newer) |
| **Native agent (optional)** | Visual Studio with the *Desktop development with C++* workload — only needed to rebuild `Obsidian.Injector.x64.dll` (managed deep dump). Everything else works without it. |

## Download and run (no build)

1. Open the [latest release](https://github.com/Stanfi1/OBSIDIAN-DUMPER/releases/latest)
   and download one of the two ZIPs:
   - **`OBSIDIAN-1.4.1-win-x64.zip`** — portable self-contained build, no
     installed .NET runtime needed (recommended);
   - **`OBSIDIAN-1.4.1-win-x64-framework-dependent.zip`** — smaller download,
     requires the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Extract the ZIP into any folder.
3. Run `OBSIDIAN.exe`. Windows may show *Windows protected your PC* because the
   binary is not code-signed — choose **More info → Run anyway** (or run
   `Unblock-File .\OBSIDIAN.exe` from PowerShell).
4. Pick a managed process, click **Attach**, then **Dump all managed** or select
   individual modules.

## Build from source

The repository is source-only. Everything is built with the standard .NET CLI, and ready-made
builds are available on the Releases page.

```powershell
# build the whole solution (UI, engine, CLI, payload, tests)
dotnet build Obsidian.Dumper.sln -c Release

# run the WPF application from source
dotnet run --project src\OBSIDIAN\OBSIDIAN.csproj -c Release

# publish a portable, self-contained folder (no .NET runtime needed on the target PC)
dotnet publish src\OBSIDIAN\OBSIDIAN.csproj -c Release -r win-x64 --self-contained true

# publish a smaller framework-dependent folder (needs the .NET 10 Desktop Runtime)
dotnet publish src\OBSIDIAN\OBSIDIAN.csproj -c Release -r win-x64 --self-contained false
```

- The published folder is a build artefact; it is not committed to this
  repository.
- The native deep-dump loader is the C++ project in
  `src/Obsidian.Dumper.Native/`; a complete developer build requires the MSVC
  toolchain. The release ZIPs already contain the compiled loader, and the
  managed deep dump degrades gracefully when it is absent.
- The project has no separate `Obsidian.Dumper.Shared` project file — the shared
  PE code is compiled directly into the Core and Payload projects via the
  `Compile Include="..\Obsidian.Dumper.Shared\**\*.cs"` items.

### Tests

```powershell
# engine verification harness (real processes, live dumping and injection)
dotnet run --project tests\Obsidian.Dumper.Tests -c Release
```

## CLI usage

Building the solution produces `obsidian.exe` under
`src\Obsidian.Dumper.Cli\bin\x64\Release\net10.0\`. Add that folder to `PATH` or
call it by full path; the examples below use `obsidian`.

```powershell
# list CLR hosts
obsidian list --managed

# modules + identities of one target
obsidian info --pid 1234
obsidian modules --pid 1234 --json

# dump all managed modules to a folder
obsidian dump --pid 1234 --all --out C:\dumps --on-conflict overwrite

# deep dump (managed agent, .NET Framework 4.x x64)
obsidian deep --pid 1234 --out C:\dumps

# inspect a dumped assembly
obsidian inspect C:\dumps\Client.dll --type LicenseEngine --il --strings

# inject a native library and unload it again
obsidian inject --pid 1234 --dll C:\libs\probe.dll
obsidian unload --pid 1234 --module probe.dll
```

Exit codes: `0` success, `1` usage error, `2` operation failed, `130` cancelled.
`--json` is available on `list`, `info`, `modules`, `dump`, `deep`, `inspect`,
`inject` and `unload`; `obsidian help` prints the command list and
`obsidian help <command>` the options of one command. `token`, `diag` and
`readtest` are smaller diagnostic commands.

## Output

```
<output>\
  Client.dll                  reconstructed PE, RVAs preserved
  Client_2.dll                collision-safe second dump (Rename policy)
  carved\                     assemblies recovered by the page walk (Memory → CARVE)
  obsidian-manifest.tsv       only for deep dumps: assembly/module/hash map
  obsidian-payload.log        agent diagnostics
```

Naming template tokens: `{name}`, `{pid}`, `{base}`, `{ext}`, `{module}`.

## How it works

```
┌─────────────┐   Toolhelp32    ┌───────────────┐   ReadProcessMemory   ┌────────────┐
│  process    │ ──────────────► │ module map    │ ────────────────────► │ PE rebuild │
│  inventory  │                 │ + CLI probe   │                       │ (mapped→file)
└─────────────┘                 └───────────────┘                       └─────┬──────┘
                                                                              │ dnlib
                                                                              ▼
┌─────────────┐   inject + CLR hosting   ┌──────────────┐            ┌────────────┐
│ native agent│ ───────────────────────► │ AppDomain    │            │  verify +  │
│ (optional)  │                          │ assemblies   │            │  hashes    │
└─────────────┘                          └──────────────┘            └─────┬──────┘
                                                                           ▼
┌─────────────┐  CreateRemoteThread /    ┌──────────────┐        ┌────────────────────────┐
│ DllInjector │  NtCreateThreadEx / APC   │ target DLL   │        │ Inspector: dnlib + ILSpy│
│ + Unload    │ ────────────────────────► │ loaded       │        └────────────────────────┘
└─────────────┘                          └──────────────┘
```

A deeper write-up (including the PE maths, sparse-page reader, agent protocol,
injection stack, theme service, settings schema and UI architecture) lives in
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Limitations

- **Assembly carving** recovers valid PE images; an assembly with its metadata
  completely destroyed in memory needs the runtime-metadata repair path and may
  still be unrecoverable.
- **Metadata repair** patches a zeroed COR20 directory and a wiped BSJB root
  from ClrMD. It does not reconstruct arbitrary encrypted stream layouts.
- **Loader hook** (`Assembly.Load(byte[])` at load time) is not implemented yet;
  the managed deep dump and the CARVE page walk cover already-loaded and
  manually mapped assemblies.
- **Managed injection** is .NET Framework 4.x x64 only; the native host does not
  load into CoreCLR. x86 managed hosts are not staged yet.
- **Deep dump is .NET Framework 4.x x64 only.** CoreCLR cannot host a second
  runtime in a live process; those targets use the out-of-process engine.
- **Some hardened hosts disable CLR hosting** (HRESULT `0x80131023`). The
  feature then reports the condition instead of forcing it.
- **Anti-dump protections** can invalidate metadata at rest. OBSIDIAN reads
  memory, so decrypted-at-runtime metadata is usually fine; heavily virtualised
  IL (VM protectors) remains analysis work, not a click.
- **Injection:** `NtCreateThreadEx` and `QueueUserAPC` are x64-only (cross-bitness
  APC delivery is refused because it can destabilise a WOW64 target); managed
  assemblies are not loadable via `LoadLibrary` and are reported as such.
- **Cross-bitness out-of-process dumping** works for 32-bit targets; deep dump
  and injection are not supported for ARM targets from an x64 host.
- **Full minidump / thread stacks / object roots** are outside the current scope
  (use `dotnet-dump` or WinDbg + SOS for those).
- Dump cancellation is between modules, not inside a single module read.
- The GUI targets Windows because it uses WPF and Win32 process APIs.
- The `samples/Net48Target` folder contains the source of the .NET Framework
  test target but no project file; its prebuilt binaries are not part of the
  repository.

## Safety & legal

OBSIDIAN is an analysis tool for software you own or are authorised to assess.
The default dump engine is strictly read-only: it opens handles with
`PROCESS_QUERY_INFORMATION | PROCESS_VM_READ`, never suspends the target and never
writes to it. The managed agent and **DLL injection** are opt-in, require an
explicit confirmation, and only operate on the target you select; injection writes
into a live process and loads code in it, so treat it like any other debugger or
instrumentation tool. Use it responsibly and in line with the laws and licences
that apply to you.

## Repository layout

```
src/Obsidian.Dumper.Core/            engine: Win32 interop, PE rebuild, dumpers, injection, inspection
src/Obsidian.Dumper.Core/Memory/       virtual address map and byte scanner
src/Obsidian.Dumper.Core/Dumping/      module dump, memory carving and metadata repair
src/Obsidian.Dumper.Core/PE/           PE header reader
src/Obsidian.Dumper.Core/Resources/    embedded resource extractor
src/Obsidian.Dumper.Core/Runtime/      ClrMD runtime snapshot
src/Obsidian.Dumper.Core/Injection/    native + managed injection
src/Obsidian.Dumper.Shared/            portable PE code compiled into Core and Payload (no project file)
src/Obsidian.Dumper.Payload/           managed agent (net48, reflection only)
src/Obsidian.Dumper.Native/            native loader source (MSVC, C++)
src/OBSIDIAN/                          WPF application (net10.0-windows), themes, settings
src/OBSIDIAN/Controls/                 reusable HexView and code view
src/Obsidian.Dumper.Cli/               headless client
tests/Obsidian.Dumper.Tests/           verification harness
samples/                               test targets (net10 buildable; net48 sources)
docs/ARCHITECTURE.md                   engine, injection, theme and settings architecture
docs/diagram/architecture.png          the pipeline diagram used above
docs/demo/                             15-second demo (GIF + MP4)
docs/screenshots/                      dark and light screenshots of every page
```

The repository contains only project sources, tests, samples, screenshots and a
small set of documents. Build instructions above use the plain .NET CLI.

## Documentation

- [README.md](README.md) — this overview: features, requirements, quick start,
  build, CLI, architecture summary and limitations.
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — how the engine, injection stack,
  themes and settings fit together.
- [docs/diagram/architecture.png](docs/diagram/architecture.png) — the pipeline
  diagram in one image.
- [docs/demo/obsidian-demo.gif](docs/demo/obsidian-demo.gif) — 15-second UI tour
  (also available as [MP4](docs/demo/obsidian-demo.mp4)).
- [CHANGELOG.md](CHANGELOG.md) — release notes and version history.
- [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) — bundled third-party
  components and their licences.
- [LICENSE](LICENSE) — MIT licence.
- [docs/screenshots/](docs/screenshots/) — the screenshots used above.

## Third-party

- [dnlib](https://github.com/0xd4d/dnlib) 4.4 — metadata reading/writing.
- [ILSpy / ICSharpCode.Decompiler](https://github.com/icsharpcode/ILSpy) 9.1 —
  C# decompilation.
- [ClrMD / Microsoft.Diagnostics.Runtime](https://github.com/microsoft/clrmd) 4.2 —
  managed heap snapshots and runtime metadata repair.

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). OBSIDIAN itself is MIT
licensed — see [LICENSE](LICENSE).
