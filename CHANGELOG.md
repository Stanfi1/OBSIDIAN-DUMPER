# Changelog

All notable changes to OBSIDIAN are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
semantic versioning.

## [1.4.1] — 2026-11-18

Interface and reliability fixes reported after the 1.4.0 release. The dump
engine and the feature set are unchanged; two release packages are published
(self-contained and framework-dependent).

### Fixed

- **Combo box hover painted a white rectangle.** The `ObsidianComboBox` template
  embedded a raw `ToggleButton` without a control template, so WPF's default
  light chrome was drawn over the themed surface. The drop-down toggle now has a
  transparent template; popup, keyboard navigation and the context menu are
  unchanged.
- **Dark-mode text inherited the default black.** WPF does not apply an implicit
  `Style TargetType="Window"` to `Window` subclasses, so `ShellWindow` and
  `HexWindow` never received the theme typography. The window roots now set the
  palette foreground, font family, font size and text rendering, which fixes the
  sidebar session card, page headings and plain text in dark mode.
- **Empty states overlapped running operations.** The CARVE "No carved
  assemblies yet" panel stayed visible during a page walk and drew over the
  progress panel; SCAN had the same defect. Both empty states are now shown only
  when there are no results and the operation is not running.
- **Inspector C# / IL / STRINGS tab headers ignored the selected state.** A
  dedicated `TabHeaderText` style now follows the theme, so the active header is
  accent-coloured like the other tabs.
- **Inspector PE tab:** the PE-error banner no longer overlaps the PE header
  summary — it gets its own row below the label and summary.
- **Memory MAP read errors** are now reported in the status line instead of
  leaving an empty table without an explanation.
- **Memory SCAN / RUNTIME busy and empty states:** a completed zero-hit scan no
  longer shows the "Enter a pattern…" hint, the snapshot placeholder is hidden
  while a capture is running, and the states no longer overlap.
- **Settings theme/density chips** no longer appear unchecked when the already
  active chip is clicked; the active state stays visible.
- **Activity:** the TIME column is hidden together with the timestamps, so no
  blank gutter is left in the header or the rows.
- **Hex viewer** shows an empty-state message when no bytes are loaded.
- **Injection page:** the right-hand column (result + history) scrolls at small
  window sizes, so the history is no longer clipped.
- **CARVE:** the busy icon was a "photo" glyph and is replaced; the progress bar
  now follows scanned bytes instead of the number of MZ candidates.
- **Theme contract:** the close-button hover glyph uses a palette token instead
  of a hard-coded white, and the tooltip rows in `docs/THEME-CONTRACT.md` were
  synced with the shipped light palette.
- **Settings:** the dead-end "Open log folder" button opened the settings folder
  although OBSIDIAN writes no log files — it is now labelled "Open output folder"
  and opens the dump output folder.
- **Deep dump no longer reports success when the in-target agent fails.** A
  negative agent return value or a missing manifest is reported as failure and
  `obsidian-payload.log` is surfaced. Ctrl+C is reported as cancelled (CLI exit
  code 130, not 2).
- **Workspace "Open dump" never executes a dumped `.exe`.** Managed assemblies
  open in the Inspector, other files are revealed in Explorer, and the button
  tooltip says so.
- **Managed-injection history** no longer offers an Unload action that cannot
  work, and the injection warning now points at the MANAGED ASSEMBLY card.
- **Runtime label of self-contained .NET apps** is normalised — the target list
  and CLI no longer show raw `.NET 10,0,1426,52326 @Commit: …` strings.
- **Overwrite confirmation** honours a custom file-name template instead of
  silently replacing files that the built-in prediction did not see.
- **Agent housekeeping:** deep-dump and managed-injection work directories are
  removed after the run (including stale folders left by earlier versions), and
  manifest file paths are validated to stay inside the output folder.
- **CLI:** `info`/`modules` columns are separated (address and size no longer
  run together), `--page` help lists `memory`, and hex patterns are validated
  strictly (exactly two hex digits per byte, `??` wildcard) in the CLI and the
  hex viewer.
- **Inspector resource images** are decoded with dimension and byte-size caps, so
  a decompression bomb cannot freeze the application.
## [1.4.0] — 2026-11-09

Hard-dump parity with ExtremeDumper: hidden assembly carving, anti-dump
metadata repair and managed assembly injection.

### Added

- **CARVE tab on the Memory page.** Walks committed readable memory for MZ
  images, converts mapped or file layouts, validates every candidate with
  dnlib, deduplicates by SHA-256 and saves the managed assemblies into
  `<output>\carved`. This reaches manually mapped modules that never appear in
  the loader list. Framework assemblies are skipped unless requested.
- **Metadata repair from the live CLR.** When a managed dump fails metadata
  validation, `MetadataRepairService` attaches with ClrMD, reads
  `ClrModule.MetadataAddress`/`MetadataLength`, repairs a zeroed COR20 directory
  and a wiped BSJB root, and writes the patched assembly back. Controlled by
  Settings → Dumping → *Repair metadata from the live CLR* (default on).
- **Managed assembly injection** on the Injection page. Loads a .NET Framework
  4.x x64 assembly through the existing native CLR host and invokes a
  `static int Method(string)` entry point with an optional argument. Results
  include the CLR HRESULT, method return value, work directory and history.
- Reusable `AssemblyCarver`, `MetadataRepairService` and
  `ManagedInjectionService` in the engine.

### Changed

- Dumps that fail metadata validation now attempt the CLR repair before they
  are reported as packed/metadata-encrypted; successful repairs appear in the
  dump warnings and activity log.
- Injection page gained the MANAGED ASSEMBLY card; the native warning explains
  when to use it.
- Fixed text boxes in the managed card that were invisible with a fixed height;
  field content and automation values now render correctly.

### Verified

- Page walk over the .NET 10 test target: 318 MZ candidates, ~100 MB scanned,
  1–2 valid assemblies saved in under 1 s; a file-layout assembly written
  into a private allocation and absent from the module list was recovered.
- A dumped test assembly with its COR20 directory and BSJB signature zeroed was
  repaired from the live runtime and reopened by dnlib.
- Managed injection into the net48 target returned 6 and produced the full
  dumped-assembly manifest.

## [1.3.0] — 2026-11-06

Memory and binary inspection release. Everything below is read-only: OBSIDIAN
never writes to the target from these views.

### Added

- **Memory page** (Ctrl+5) with three views:
  - **MAP** — live `VirtualQueryEx` address space with base, size, protection,
    type, owning module and flags. Committed regions are shown by default;
    Modules / Executable / Suspicious / Reserved filters and free-text search
    narrow the list. The summary reports committed bytes, commit/reserve counts
    and suspicious regions. Selected regions can be dumped (up to 256 MB), copied
    or opened in the hex viewer (first 4 MB).
  - **SCAN** — cancellable, bounded pattern scanner over committed readable
    regions (512 MB default, 2,000 hits). Text, UTF-16 and hex patterns, `??`
    wildcards, case-insensitive ASCII, executable-only mode, live progress and
    hit context. Double-click a hit to inspect it in hex.
  - **RUNTIME** — ClrMD snapshot with runtime version, threads, modules, object
    count, heap size and walkability, plus the top heap types by count, size and
    share. A 10 s budget keeps the UI responsive; missing DAC / runtime support
    becomes an explicit message instead of a crash.
- **Inspector PE tab** — machine, subsystem, timestamp, characteristics, image
  sizes, section table with Shannon entropy and flags, data directories and CLR
  metadata, parsed with bounds checks.
- **Inspector Resources tab** — list of embedded/linked resources with text,
  image and hex previews, save and copy.
- **Inspector HEX tab** and a themed, resizable hex window for memory regions and
  scan hits: text or hex search with wildcards, go-to-offset, offset/hex/ASCII
  columns, up to 8 MB displayed.
- Reusable `HexView`, `MemoryViewModel`, `MemoryMapReader`, `MemoryScanner`,
  `PeHeaderReader`, `ResourceExtractor` and `RuntimeSnapshotService`.
- `Microsoft.Diagnostics.Runtime` 4.2 for managed heap snapshots.

### Changed

- Keyboard page switching is now Ctrl+1…7 (Targets → Workspace →
  Inspector → Injection → Memory → Activity → Settings).
- Tab bodies inherit the page text colour instead of the selected tab label's
  accent, so body copy is no longer tinted with the accent.
- Dark theme secondary text was brightened (`Dim` `#7A8492`) and section labels
  use `Muted`, fixing the low-contrast `APPEARANCE` / `DUMPING` / `OUTPUT
  DIRECTORY` headings reported on the settings page.
- MAP hides reserved ranges by default and totals only committed memory, so the
  landing view is the process's real footprint instead of guard/reserve noise.

### Fixed

- **Tab content was missing from the accessibility/UI-automation tree.** The
  custom `TabControl` template now names its content host
  `PART_SelectedContentHost`; screen readers and automation can reach every
  page and dialog again.
- **PE/resource body text used the tab accent colour.** The content presenter
  now sets an explicit inherited `Foreground` from the theme.
- PE parsing is bounds-checked for truncated files; resource previews cap large
  resources; ClrMD failures degrade to an explanatory state.

## [1.2.1] — 2026-11-06

Interface fixes reported on the redesigned shell.

### Fixed

- **Maximize button did nothing.** The Snap Layouts hit test made Windows treat
  the caption button as non-client, which swallowed the press before either the
  system or the WPF button could act. The window now handles the non-client
  press/up itself, toggles exactly once, restores correctly, shows a hover
  surface again, and maximises to the work area with no inset frame.
- **Context menus were white with unreadable text.** The menu surface, items,
  hover, separators and disabled state now use the theme (`Elevated` surface,
  hairline border, accent-tinted hover) in both light and dark mode.
- **Tooltips stayed dark in the light theme.** The light palette now defines a
  light tooltip surface and the global `TextBlock` foreground override was
  removed, so text inherits from its actual container (tooltip, menu, button).
  Tooltips wrap long paths and use the theme's shadow.
- **Accent presets only recoloured the primary button.** Focus rings, input
  focus, check marks, text/code selection, checked chips, the selected
  navigation row, its count badge, the active tab label and the detail-panel
  icons now follow the chosen accent. Accent presets were retuned for contrast
  and the primary-button text switches between white and graphite based on the
  accent's luminance.

### Changed

- **Responsive layout pass** at the 1180×740 minimum: Workspace and Activity
  toolbars use two stable rows instead of ad-hoc wrapping, detail panels shrink
  fluidly (280–360 px), table columns were tightened, and long runtime/path
  labels truncate with tooltips instead of overflowing.
- Window caption buttons expose accessible names and the maximize/restore
  state is announced.
- Inactive/disabled menu items are dimmed consistently; `Separator` inside menus
  is themed.

## [1.2.0] — 2026-11-06

The "instrument" release: a complete interface redesign. The engine, the view
models, the bindings, the commands and the settings schema are unchanged — only
the visual layer was rebuilt, around a documented design system.

### Added

- **`DESIGN_SYSTEM.md`** — the visual contract for OBSIDIAN (palette, type scale,
  spacing grid, component metrics, radii, elevation, icons, buttons, inputs,
  navigation, tables, log viewer, code viewer, states, motion, responsive rules,
  copy tone, accessibility and implementation rules).
- **New shell**: 40px title bar with a compact target chip, 224px sidebar with
  an active-row accent marker and a session panel, a slimmer command bar, a
  26px monospace status bar, and toasts anchored bottom-right.
- **Shared style tokens**: `FieldLabel` / `FieldValue` / `FieldValueMono`,
  `StatValue`, `InsetWell`, `ToolbarSeparator`, `KeyCap`, `DangerButton`, and a
  single `ObsidianFocusVisual` focus ring used by every interactive control.
- **Automation names** on navigation rows, so UI verification and assistive
  technology can address pages by name instead of screen coordinates.
- **New brand mark and icon**: a graphite hexagon with an ember core, replacing
  the blue/violet gradient hexagon in `Assets/obsidian-mark.png` and the
  multi-size `Assets/obsidian.ico` (16–256px).

### Changed

- **Palette replaced.** Graphite surfaces (`#0B0C0E` … `#1F242B`), hairline
  borders, obsidian red accent (`#D0453C` dark / `#B5372E` light) and a muted,
  desaturated semantic set. The violet/cyan gradient identity, ambient glows,
  grid backdrop and gradient hero cards are gone.
- **Every page rebuilt** on the new system: Targets (metric strip, sortable
  table, 340px inspector), Workspace (flat target summary + metric strip),
  Inspector (compact header, identity definition list, references preview,
  restyled explorer/strings/references/protections tabs), Injection (library /
  technique / operation cards, result and history), Activity (tabular log with
  level colours) and Settings (row-based sections with controls on the right).
- **Geometry**: 2–4px radii everywhere (was 10–18px), 1px structure, no shadows
  except toasts/popups, dense 30px controls and 26–38px table rows.
- **Typography**: 18px page titles (was 23–27px), 12.5px body, 9.5px uppercase
  section labels and table headers, Cascadia Mono for every technical value.
- **Filter chips** now use a neutral selected state; red is reserved for the
  primary action, active navigation, tab underline, focus and destructive
  affordances.
- **Accent presets** renamed and retuned: Ember (`Red`, new default), Steel
  (`Cyan`), Jade (`Green`), Amber, Iris (`Violet`).
- **`docs/THEME-CONTRACT.md`** rewritten for the new token set (including
  `AccentHover`, `AccentPressed`, `FocusRing`, `SurfaceHeader`).
- `tools/ui-capture.ps1` navigates by accessible name and only falls back to
  coordinates; `DensityService` now mirrors the new radius/spacing tokens.

### Fixed

- Light theme: table headers and section labels were below comfortable contrast;
  the light `Dim` tone was darkened to `#6F7782`.
- Empty-state glyphs were invisible on light surfaces; they now use the `Dim`
  token in both themes.

## [1.1.0] — 2026-10-28

The "pro edition" release: a full-code audit of the 1.0.0 tree, a working DLL
injection stack, a runtime theme system, a professional settings panel, and a
much wider operator workflow. See `docs/AUDIT.md` for the consolidated audit.

### Added

- **DLL injection page** (`Injection` in the sidebar, `Ctrl+4`): pick a native
  DLL, get a live architecture/compatibility verdict, choose a technique —
  `CreateRemoteThread`, `NtCreateThreadEx` or `QueueUserAPC` — inject it,
  inspect the remote module base / Win32 error / NTSTATUS / duration, then unload
  it again. Confirmation dialog, per-session history, copyable report, engine log
  and toast integration. Verified end-to-end through the real UI on x64 and WOW64
  targets (`tools/verify-injection-ui.ps1`).
- **Engine injection API** (`Obsidian.Dumper.Core.Injection.DllInjector`):
  architecture validation before any allocation, WOW64 support through the real
  32-bit `LoadLibraryW` address, `NtCreateThreadEx` and `QueueUserAPC` for x64
  targets, module unload, honest result objects (`InjectionResult`), and
  `ProcessControl` (suspend / resume / terminate / suspended probe). The managed
  deep-dump agent now uses the same injector instead of its own copy.
- **CLI**: `inject`, `unload`, `modules` commands, `--json` output for
  `list/info/modules/dump/deep/inspect/inject/unload`, `--on-conflict
  fail|rename|overwrite`, `--filter`-style help per command, documented exit codes
  (0 ok / 1 usage / 2 failed / 130 cancelled), Ctrl+C cancellation.
- **Runtime theme system**: dark / light / follow-Windows themes, five accent
  presets (Violet, Cyan, Green, Amber, Red), live switching without restart,
  DWM immersive chrome that follows the effective theme, `--theme` startup
  override, palette in `Themes/Palette.Dark.xaml` + `Palette.Light.xaml` behind a
  frozen token contract (`docs/THEME-CONTRACT.md`).
- **Professional settings panel** (schema v2, ~40 persisted + validated options):
  Appearance (theme, accent, density, animations, engine block), Dumping (policy,
  overwrite mode, parallel threads, strip bound imports, auto-open, deep-dump work
  dir), Processes (auto-refresh interval, lazy command lines, confirmations,
  integrity display), Injection (default method, confirmation, last folder,
  auto-unload on detach), Activity (max entries, timestamps, auto-scroll, minimum
  level), Diagnostics (copy report, paths, engine versions, legal notice, reset).
  Forward-compatible load, clamped values, v1→v2 migration, reflection-based reset.
- **Pro workflow**: process context menu (attach, attach + dump, copy PID/path/
  command line, open folder, suspend, resume, terminate with confirmation),
  sortable targets columns (working set, started) + auto-refresh, CSV/JSON export
  of targets and modules, per-module context menu and dump summary, Inspector save
  to `.cs`, Markdown/JSON report export, clipboard actions, in-code highlight
  search, expand/collapse, recent-capture management, Activity export/copy/clear/
  follow with minimum-level filters.
- **Verification**: `tools/verify-injection-ui.ps1` (7-check E2E UI test),
  `tools/ui-capture.ps1` (dark + light screenshots, exclusive `artifacts/.gui-lock`),
  `tools/build.ps1` (serialised build/test helper).
- **Audit documentation**: `docs/AUDIT.md` plus four area reports in
  `docs/audit/` (engine, inspection/CLI, UI shell, UI pages) with per-finding
  severity, fix and verification notes.

### Fixed

- **180 defects** found by the four audits (see `docs/AUDIT.md`), including:
  - Engine: PE `Characteristics` read at the wrong offset (every DLL was
    misclassified), a P/Invoke for a non-existent ntdll export (WOW64 command
    lines always empty), cross-bitness `QueueUserAPC` destabilising targets,
    unsafe `ReadInto` range handling, integrity-level SID parsing, native-state
    loss on module rescan, duplicate module-name crash, PE rebuild overflow
    hardening, payload image-copy crash on 0x2000-aligned images, `DllMain`
    thread-start failure handling.
  - Inspection: metadata strings collected after the snapshot (never surfaced),
    stale decompiler cache after re-dump, IL formatter locals/EH/operand bugs,
    protection heuristic false positives, mode of file opening.
  - UI shell: `async void` command crashes, toast timer leaks after shutdown,
    settings save/notify/reset bugs, startup race on deferred attach, Windows 11
    maximise/Snap Layouts, progress bar never filling, duplicated version strings.
  - UI pages: 19-line mojibake corruption in `WorkspaceViewModel`, "no target"
    empty state that could never appear, selection loss on every refresh, filter
    chips unchecking themselves, stale IL race in the Inspector, missing
    virtualization, invisible errors/loading states, broken empty states, plus
    dozens of copy/typo fixes.
- Settings are now clamped and migrated; a corrupt or hand-edited settings file
  can no longer break a dump or show misleading values.

### Changed

- Version 1.1.0 everywhere; version strings come from the assembly metadata
  (`Services/AppInfo.cs`), not from literals.
- `Themes/Theme.xaml` is styles-only with `DynamicResource` palette references;
  pages, code view and converters all follow the active theme.
- `DumpOptions` gained `StripBoundImports`, `OverwritePolicy`
  (`Fail`/`Rename`/`Overwrite`, race-free) and `WorkDirectory`; the legacy
  `Overwrite` boolean maps onto the policy.
- `TargetSession.ReloadModules` preserves dump state for native modules too and
  tolerates duplicate module names.
- CLI help, errors, exit codes and JSON are consistent across every command.
- Docs: README, `docs/ARCHITECTURE.md`, `docs/THEME-CONTRACT.md`,
  `docs/SETTINGS-CONTRACT.md`, `docs/AUDIT.md`, launchers and `publish.ps1`
  updated. `publish.ps1 -SelfContained` no longer overwrites the
  framework-dependent publish folder; `-Target native` in the build helper works
  without `pwsh`.
- Test harness grew from 41 to **213 deterministic checks** (inspector, IL,
  EH/switch, decompiler, protections, malformed inputs, CLI surface, live
  injection/process control, live dump, agent assets), with `--filter` and no
  machine-specific paths.

## [1.0.0] — 2026-10-26

First public tree: live process inventory, out-of-process memory-image
reconstruction, optional managed deep-dump agent, ILSpy-powered inspector,
custom dark WPF shell, headless CLI, verification harness.
