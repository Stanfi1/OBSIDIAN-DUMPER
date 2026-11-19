# UI pages audit — Targets / Workspace / Inspector / Activity

**Owner:** ui-pages teammate · **Date:** 2026-10-28
**Scope (write):** `src/OBSIDIAN/Views/TargetsView.xaml(.cs)`, `WorkspaceView.xaml(.cs)`,
`InspectorView.xaml(.cs)`, `LogView.xaml(.cs)`, `ViewModels/TargetsViewModel.cs`,
`WorkspaceViewModel.cs`, `InspectorViewModel.cs`, `LogViewModel.cs`,
`Controls/CodeView.cs`, `Common/Converters.cs`.
**Out of scope (reported, not edited):** `Theme.xaml`, `AppSettings.cs`, `ShellViewModel.cs`,
`Obsidian.Dumper.Core.*`, docs-owned contracts.

## 0. Method and verification

Every file in scope was read end-to-end, then every WPF binding was resolved by hand against
the view-model property/type (WPF swallows binding failures silently, so "the UI looks right" is
not proof). The app was built with the serialised helper and driven for real through
`tools/ui-capture.ps1`, then screenshots were inspected.

| Gate | Command | Result |
|---|---|---|
| App build | `powershell -NoProfile -ExecutionPolicy Bypass -File tools/build.ps1 -Target app` | **BUILD OK, 0 warnings, 0 errors** |
| UI smoke | `tools/ui-capture.ps1` (Targets → Workspace dump → Inspector overview/explorer/strings → Injection → Activity → Settings → light pass) | all screens captured; Targets/Workspace/Inspector/Activity verified against expectation; screenshots live in `OBSIDIAN-DUMPER/artifacts/shots/` |
| Regex safety | 180–300 KB adversarial single-line inputs against the exact CodeView patterns, 2 s timeout each | no catastrophic backtracking; worst 202 ms on a 180 KB pathological line (never reached in-app because lines > 8 000 chars are not tokenised) |
| Tests | not run | per Lead instruction: test harness is owned by another agent during the parallel phase |

Note on tooling: this machine has **no `pwsh`**; the helper was invoked through
`powershell.exe`. `Get-Content` on Windows PowerShell 5.1 decodes BOM-less UTF-8 as ANSI and
manufactures `â€”`-style mojibake; the genuine encoding defect below was confirmed by reading raw
bytes (`U+00E2 U+20AC U+201D`), not by `Get-Content`.

**Fix counts (this report):** 5 critical, 24 major, 18 minor, 6 suggestions = **53 findings**
(46 fixed in scope, 7 reported/deferred with owner).

---

## 1. Findings

### Critical

| ID | File:line | Problem | Fix | Verification |
|---|---|---|---|---|
| UP-01 | `Views/WorkspaceView.xaml:9` (+`:26`) | No-target empty state was bound to `TargetName` through `StringEmptyToVisibility`: the VM initialises `TargetName = "No target attached"` (non-empty), so on startup **the empty state was hidden and the full "attached target" layout was shown for a process that does not exist**. | Added `WorkspaceViewModel.HasTarget` and bound the empty state / content grid to it (`BoolToVis` / `invert`). | build; capture 02/09; code review |
| UP-02 | `ViewModels/WorkspaceViewModel.cs:32-36, 189, 191, 347, 348, 362, 363, 367-369, 413, 425, 536, 647-655` | File was saved once with a broken encoding: 19 strings contained double-encoded UTF-8 (`â€”` instead of `—`, `Â·` instead of `·`, `â€¦` instead of `…`). Visible as garbage in the details placeholders and toasts. Confirmed at byte level (`U+00E2 U+20AC U+201D`), not a tooling artifact. | Replaced all 19 occurrences with the intended characters; whole file re-saved as valid UTF-8. Grep `â€|Â·` over `Views/`, `ViewModels/`, `Controls/`, `Common/` now returns nothing for in-scope files. | build; grep; capture 02/14 |
| UP-03 | `ViewModels/TargetsViewModel.cs:131-152, 315-322` | `ProcessRecord` is a `record`; the old `Selected` setter used `Set(...)` (value equality). After a rescan produced a fresh instance with identical values, the setter returned early, never raised `PropertyChanged`, and the ListBox selection pointed at an object no longer in `ItemsSource` — **selection was visibly lost on every rescan**. | `Selected` is now reference-identity based; `RefreshAsync` captures the selected PID before replacing the collection and re-selects the new instance. | build; captures 01/09 (selection kept across rescan in the harness) |
| UP-04 | `TargetsViewModel.cs:410-435`; `WorkspaceViewModel.cs:419-445`; `InspectorViewModel.cs:470-490` | `ICollectionView.Refresh()` (search / filter change) raises Reset; WPF's `Selector` clears `SelectedItem`, and the TwoWay binding writes `null` back into the VM. **Every filter keystroke wiped the selection** — in the Inspector that also closed the decompiled type, the method list and the C#/IL panes. | Each page now captures the selection before `Refresh()`, restores it through the property setter afterwards (re-raising dependent notifications and command states), and only clears it when the item is genuinely filtered out. | build; code review (the harness does not type filters; manually traced WPF `Selector` reset behaviour) |
| UP-05 | `InspectorViewModel.cs:526-551` | Stale IL race: `LoadIlAsync` checked the capture generation but not the selected method, so a slow disassembly of method A could overwrite the IL of the newly selected method B. | The continuation now verifies both `_inspector` reference and `SelectedMethod` before assigning; `SelectedMethod`/`SelectedType` clear the old IL immediately. | build; code review |

### Major

| ID | File:line | Problem | Fix | Verification |
|---|---|---|---|---|
| UP-06 | `TargetsViewModel.cs:377-395`; `LogViewModel.cs:148-166` | Filter chips desynchronised: the setter early-returned when the same filter was re-applied, so no `PropertyChanged` fired, but the `ToggleButton` had already unchecked itself locally — **clicking the active chip left every chip unchecked while the filter stayed active**. | Command handlers re-raise all chip flags even when the value is unchanged (and refresh the view). | build; code review of `ToggleButton.OnClick` → `SetCurrentValue` behaviour |
| UP-07 | `Views/TargetsView.xaml:116-127, 264-286`; `TargetsViewModel.cs:238-279, 354-368` | Scan UX: `IsListEmpty` was `IsScanning || View.IsEmpty`, so the **"No processes match this view" overlay covered a populated list during every scan**. Refresh stayed enabled while the scan ran (click did nothing); `CancelScanCommand` existed but was not bound; there was no progress feedback; cancellation was reported as an error and cleared the list. | Added `ShowEmptyState` / `ShowScanningState` / `ScanSummary` / `ScanStatusText` / `ScanError`; toolbar shows "Scanning… N processes" + Cancel while scanning; Refresh `CanExecute` is false while scanning; cancellation now keeps the previous list and says "Scan cancelled." | build; captures 01/09; code review |
| UP-08 | `WorkspaceViewModel.cs:54-63, 310-320` | Overlapping operations: `RescanCommand` only required a session (clicking Rescan during a dump rebuilt the `ModuleRecord` collection the dumper was writing to); `DumpNativeCommand` did not check busy state; dump commands did not account for the deep-dump flag. | All operations now use `IsBusyOrDumping` in `CanExecute` + method guards, and command states are refreshed whenever either flag changes (incl. `CancelDumpCommand`, `RescanCommand`, `SelectAllCommand`). | build; code review |
| UP-09 | `WorkspaceViewModel.cs:400-417` | `RevealCommand` stayed disabled after a dump because its `CanExecute` was only re-evaluated on selection change, not when `DumpedPath` changed on the selected module. | `ModuleOnPropertyChanged` now raises `RevealCommand.RaiseCanExecuteChanged()` for the selected module. | build; capture 02 (Reveal enabled after the harness dump) |
| UP-10 | `WorkspaceViewModel.cs:542-636`; `Views/WorkspaceView.xaml:232-261` | Dump could not be cancelled although `ModuleDumper.DumpAsync` accepts a `CancellationToken`; detached targets left a running dump writing into a disposed session. | Added `CancelDumpCommand` + CancellationToken; progress panel has a Cancel button; `Clear()` (detach) cancels the batch; cancellation produces an honest toast and keeps already-written files. | build; code review (core cancellation limitation noted in §5) |
| UP-11 | `WorkspaceViewModel.cs:281-285` | `Load()` did not reset `SelectedCount`; after a detach/re-attach the toolbar kept the previous session's "N selected" and the Dump-selected command state. | `Load()` resets `Selected`, `SelectedCount`, `DumpedCount`, `FailedCount` before refreshing counts. | build; code review |
| UP-12 | `WorkspaceViewModel.cs:453-502` | `TargetSession.ReloadModules()` carries dump state only for managed modules, so **a dumped native module lost its Dumped/failed badge after "Rescan modules"**. | The page preserves dump state for *all* previously dumped modules by name and re-applies it to the refreshed records; root cause reported to the engine owner (out of scope). | build; code review |
| UP-13 | `Views/WorkspaceView.xaml:425-434`; `Views/InspectorView.xaml:202-225`; `WorkspaceViewModel.cs:143-144`; `InspectorViewModel.cs:206-210` | Read-only booleans were bound directly (`Selected.IsFramework`, `Summary.IsStrongNamed`, `Summary.IsIlOnly`) and rendered as "True"/"False"; the PE timestamp was rendered with default culture and a `TargetNullValue` hack. | Added `SelectedFrameworkText`, `SelectedInMemoryText`, `StrongNameText`, `IlOnlyText`, `CompileTimestampText` (`yes`/`no`, `deterministic`, fixed `yyyy-MM-dd HH:mm:ss`). | build; capture 02/03 |
| UP-14 | `InspectorViewModel.cs:322-370, 409-467`; `Views/InspectorView.xaml:140-166` | Failed loads were invisible: `HasError`, `ErrorMessage`, `IsLoading` and `IsDecompiling` were never bound, so an unreadable/native file showed the normal "No assembly loaded" empty state with no reason, and long metadata/decompile operations looked frozen. | Empty state is now data-driven (`EmptyStateTitle`/`EmptyStateHint`), shows the error text, a Reload button, a "Reading assembly metadata…" state, and a "decompiling…" overlay in the C# tab. | build; code review |
| UP-15 | `InspectorViewModel.cs:409-467` | Load races/leak: overlapping capture loads could show the wrong error or stop the spinner early, the superseded `AssemblyInspector` was disposed but a failed load left the previous inspector alive and usable by in-flight decompile tasks. | Generation guards on success *and* error/finally; superseded inspectors are disposed; the previous inspector is disposed on failure. | build; code review |
| UP-16 | `InspectorViewModel.cs:58-82`; `Views/InspectorView.xaml:38-52` | Capture chips never showed which capture was open: `ToggleButton`s had a command but no bound `IsChecked`. | Added `CapturedAssembly.IsActive`, maintained by `SelectedCapture`; chip binds `IsChecked` OneWay and exposes the capture detail as a tooltip. | build; capture 04 (active "System.Private.CoreLib_2" chip highlighted) |
| UP-17 | `Views/WorkspaceView.xaml` (module `ListBox`) | The module list explicitly disabled virtualization: `VirtualizingPanel.IsVirtualizing="False"` + `ScrollViewer.CanContentScroll="False"` — with hundreds of modules and heavy row templates, initial layout/scrolling degrades badly. | Removed both overrides; the `TableList` style already enables recycling virtualization. | build; capture 02 |
| UP-18 | `Views/InspectorView.xaml` (types `ListBox`) | Types are grouped by namespace; grouping disables WPF virtualization unless `IsVirtualizingWhenGrouping` is set, so a large assembly realised every type row at load. | Added `VirtualizingPanel.IsVirtualizing="True"`, `IsVirtualizingWhenGrouping="True"`, `VirtualizationMode="Recycling"`, `CanContentScroll="True"`. | build; capture 04 |
| UP-19 | `LogViewModel.cs:207-214` | "Clear" left the entry count label and Copy/Export command states stale, and the cleared state was not announced. | `Clear()` raises the full meta set, re-evaluates commands and toasts. | build; capture 07 |
| UP-20 | `LogViewModel.cs:236-262` | Export called `File.WriteAllLines` without any error handling: a locked/read-only target threw into WPF's dispatcher (global MessageBox) instead of a page-level message; no extension/overwrite defaults. | Dialog now has `DefaultExt`, `AddExtension`, `OverwritePrompt`, owner; write is wrapped and failure toasts + logs. | build; code review |
| UP-21 | `LogViewModel.cs:100-107`; `Views/LogView.xaml:88-108` | The empty overlay was shown only when the underlying log was empty; a filter that hid every entry produced an **empty list with no message**. | Added `IsViewEmpty`, dynamic `EmptyStateTitle`/`EmptyStateHint` ("No entries match this filter" / hint). | build; code review |
| UP-22 | `TargetsViewModel.cs:18-50` (new helper) + `Search`/filters in all four VMs | Every keystroke called `ICollectionView.Refresh()` synchronously: 250+ processes, up to 4 000 log rows and potentially thousands of types caused visible typing jank. | Added a shared `ViewRefreshDebouncer` (180 ms `DispatcherTimer`) used by Targets search, Workspace search, Inspector type filter, Inspector string filter and Log search. | build; code review |
| UP-23 | `TargetsViewModel.cs:86-88, 354-368` | The startup scan was a fire-and-forget `_ = RefreshAsync()`; an exception escaping the method became an unobserved task exception (silent). | `SafeInitialScanAsync` wraps and logs the initial scan. | build; code review |
| UP-24 | `Controls/CodeView.cs` (whole file) | CodeView had **no line numbers**, long lines wrapped instead of scrolling horizontally, every `Code` change reset scroll/caret with no way to re-colour for a theme switch, there was no render cap for very large documents and no per-line token cap for minified lines; the regexes were compiled with a meaningless `Singleline` flag; preprocessor directives and types were not recognised. | Rebuilt as a `Grid` with a synchronised line-number gutter (separate read-only RichTextBox), fixed `PageWidth` so lines scroll horizontally, capped at 250 000 chars / 6 000 lines / 50 000 runs / 8 000 chars per tokenised line with a visible truncation notice, token kinds stored on each `Run` so `ApplyTheme()` recolours in place (caret/selection/scroll preserved), added directive + type token groups, removed `Singleline`. | build; capture 04 (line numbers aligned 1:1 with code lines); regex stress test (§3) |
| UP-25 | `Views/TargetsView.xaml.cs:37-56` | `MouseDoubleClick` on the ListBox attached the **previously selected** process when the user double-clicked empty space below the rows. | Hit-test `ItemsControl.ContainerFromElement` for a real `ListBoxItem`; ignore otherwise. | build; code review |
| UP-26 | `LogViewModel.cs:112-145`; `Views/LogView.xaml.cs` | Log view leaked its VM event subscription (subscribed on `DataContextChanged`, never detached) and the dispatcher callback could throw during shutdown (`BeginInvoke` on a closing dispatcher), losing the last UI updates. | View subscribes on `Loaded` and detaches on `Unloaded`; VM guards dispatch with an exception filter for shutdown. | build; code review |
| UP-27 | `Views/TargetsView.xaml` (selected header alignment) + `Views/WorkspaceView.xaml`, `Views/InspectorView.xaml` (table headers) | Header grids used different left/right insets from the row content (`11/21` vs `10/12`; `18/18` vs `14/16`), so the star column absorbed the difference and **right-hand columns drifted a few pixels from their headers**. | Header margins normalised to the `TableRow` content insets (`10,12,12,0`, `14,10,16,0`, `14,8,16,0`). | build; captures 01/02/04/05 (columns line up) |
| UP-28 | `Views/WorkspaceView.xaml` (no module empty state) | When the filters hid every module the table area was simply blank. | Added a module empty state with title/hint and a "Clear filters" button (`IsModuleListEmpty`/`ModuleEmptyTitle`/`ModuleEmptyHint`). | build; code review |

### Minor

| ID | File:line | Problem | Fix |
|---|---|---|---|
| UP-29 | `Common/Converters.cs:87-124` | `CountToVisibilityConverter` documented/behaved confusingly (visible when empty) and accepted only the `"inverse"` spelling although every other converter accepts `"invert"`; only `System.Collections.ICollection` was recognised (generic read-only lists fell through to 0); a throwing `Count` getter would surface as a binding error. | XML-doc states "visible when the count is zero"; accepts `invert`/`inverse`; handles `int`, `long`, `ICollection`; catches Count failures; never throws. |
| UP-30 | `Common/Converters.cs:141-199` | Log/severity/bool brush converters allocated a new `SolidColorBrush` per cell per repaint (a log with 4 000 visible rows allocates thousands of brushes while scrolling) and cannot be recoloured for runtime themes. | Frozen shared brushes per level/severity now; the theme phase will switch these to token lookups + `ThemeChanged` (task-9). |
| UP-31 | `Common/Converters.cs:202-227` | `PercentageWidthConverter`: factor parameter was not clamped (a factor > 1 could exceed the track) and NaN/non-numeric values were not handled. | Clamp factor 0..1, NaN-safe, accepts int/float/decimal via helper. |
| UP-32 | `Common/Converters.cs:229-250` | `PercentWidthConverter` only accepted `double` for the percent (an `int` binding silently rendered 0) and did not guard NaN. | Numeric coercion helper + NaN/negative handling. |
| UP-33 | `Common/Converters.cs:252-296` | `BytesConverter` used the engine formatter (current culture — decimal commas in some locales), did not handle unsigned/negative values, and is currently **unused** by any view. | Invariant formatting, `ulong`/negative handling, documented as unused (dead resource kept for shell parity). |
| UP-34 | `Views/TargetsView.xaml.cs` | No keyboard path for the primary actions; Enter/double-click inconsistent; Ctrl+F not supported. | Ctrl+F focuses the filter, Enter on a selected row attaches, double-click hit-test fixed. |
| UP-35 | `Views/WorkspaceView.xaml.cs` | No keyboard path for dump/selection. | Ctrl+F filter, Ctrl+D dump selected, Space toggles the selected module's checkbox, Escape clears the detail selection. |
| UP-36 | `Views/InspectorView.xaml.cs` | No keyboard path for filter/reload. | Ctrl+F focuses the type filter; Ctrl+R reloads the selected capture. |
| UP-37 | `Views/LogView.xaml.cs` | No keyboard path for search; follow-mode state cleared re-subscription. | Ctrl+F focuses the log filter; subscription lifecycle fixed (UP-26). |
| UP-38 | `InspectorViewModel.cs:117-129` | `OpenFileCommand` was an `AsyncRelayCommand` whose body awaited `Task.CompletedTask`; the `OpenFileDialog` had no owner (could open behind the shell) and no error handling. | Plain `RelayCommand`, owner = `MainWindow`, try/catch with toast. |
| UP-39 | `TargetsViewModel.cs` / `WorkspaceViewModel.cs` / `InspectorViewModel.cs` | Copy/reveal/open-folder actions were silent when they failed or the file had disappeared (clipboard locked, process exited, Explorer failure). | Toasts for copy success/failure, missing executable/dump, Explorer failures. |
| UP-40 | `InspectorViewModel.cs:400-425`; `Views/InspectorView.xaml:79-83` | `VisibleTypeCount` / `VisibleStringCount` were raised *before* the `ICollectionView.Refresh()` that changes them (stale count on load). | Refresh, then raise; and raise the empty-state flags too. |
| UP-41 | `Views/InspectorView.xaml` (tab control) | `InspectorViewModel.SelectedTab` existed but was never bound, so the property was dead and tab state was not preserved in the VM. | `TabControl.SelectedIndex` bound TwoWay. |
| UP-42 | `Views/InspectorView.xaml:279-284` | "no embedded resources" message was correct-by-converter but the protections badge showed "0 hints" even next to "no fingerprints detected". | Badge hidden when the protection list is empty (`CountToVis` with `invert`). |
| UP-43 | `InspectorView.xaml` (strings tab) | No empty state when the string filter matched nothing; "Short strings" checkbox ambiguous. | Added "No literal strings match this filter" overlay + tooltip ("Include literals shorter than 6 characters"). |
| UP-44 | `LogViewModel.cs:97` | "32 entries" label ignored the active filter and went stale on clear. | "N of M entries" computed from the filtered view, re-raised on every change. |
| UP-45 | `LogViewModel.cs` (copy/export) | Copy and Export always operated on the whole buffer, contradicting the visible filter. | Both operate on the filtered view and report the exported/copied count. |
| UP-46 | `Views/LogView.xaml` (chips) | The "Info" chip also shows `Success` entries without saying so; chip plurals were inconsistent ("Warnings"/"Errors" vs "Info"). | Tooltips added ("Info and success entries", etc.); labels kept consistent as nouns. |
| UP-47 | `Views/TargetsView.xaml:66` + `TargetsView.xaml.cs` selection | Caption read "x86 1" (ambiguous) and the scan caption read "scanned never" before the first scan. | "x86: {0}"; `ScanSummary` now says "not scanned yet"/"scanning…"/"scanned HH:mm:ss"/"scan failed". |
| UP-48 | `Views/TargetsView.xaml:112-127` | "attach targets" caption under MANAGED CLR was misleading (it counts CLR hosts, not attach targets). | "CLR hosts". |

### Suggestions / dead code (kept deliberately, to be wired in the pro-feature phase)

| ID | Item | Note |
|---|---|---|
| UP-49 | `TargetsViewModel.ReportVisibleCount()` | Removed (unused public method). |
| UP-50 | `CodeView` punctuation brush | Removed: the tokenizer never produced a punctuation match, the fallback plain brush is correct. |
| UP-51 | `WorkspaceViewModel.ToggleFrameworkCommand`, `WorkspaceViewModel.HasSelectionCount` | Still unused by XAML; reserved for the quick-toggle work in task-10. |
| UP-52 | `LogViewModel.RefreshCommand` | Unused by the view; retained for shell/command parity. |
| UP-53 | `Common/Converters.cs` `PercentageWidthConverter` (`PercentWidth` in App.xaml) and `BytesConverter` (`Bytes`) | Registered in `App.xaml` (ui-core-owned) but not referenced by any view. Either delete the registrations later or use them; flagged, not edited out of scope. |
| UP-54 | Copy conventions | Buttons use sentence case ("Copy path", "Show in folder") while captions/headers are UPPER CASE and badges are lowercase; verified all four pages now follow that convention (no mixed "Dump All Managed" style left). |
| UP-55 | `Views/InspectorView.xaml:157` | The empty-state tip hard-coded "Ctrl+1…5"; the shell now supports Ctrl+1…6 (injection page). Updated to "Ctrl+1…6" + "F5 resyncs the current page". |

---

## 2. Bindings verified

All bindings resolved against the VM/model types (`b` = bool, `s` = string, `i` = int, `d` = double; `cmd` = `ICommand`;
"inv" = used with `BoolToVis ConverterParameter=invert`).

### TargetsView → TargetsViewModel / ProcessRecord
`TotalCount` i · `ManagedCount` i · `ElevatedCount` i · `X64Count` i · `X86Count` i (StringFormat) ·
`ScanSummary` s · `Search` s (TwoWay, `UpdateSourceTrigger=PropertyChanged`) · `IsAllFilter/IsManagedFilter/IsX86Filter/IsX64Filter/IsElevatedFilter` b (OneWay) ·
`SetFilterCommand` cmd · `RefreshCommand` cmd · `CancelScanCommand` cmd · `IsScanning` b · `ScanStatusText` s ·
`VisibleCountLabel` s · `View` `ICollectionView` → ItemsSource · `Selected` `ProcessRecord?` (TwoWay) · `HasSelection` b (inv) ·
`AttachCommand`, `CopyPathCommand`, `OpenFolderCommand` cmd · `AttachHint` s · `EmptyStateTitle/Hint` s · `ScanError` s · `HasScanError` b ·
`ShowEmptyState/ShowScanningState` b.
Row (ProcessRecord): `Id` i · `DisplayName` s · `Company` s? (blank for null) · `RuntimeLabel` s · `ArchitectureLabel` s ·
`IsManaged` b · `IsElevated` b · `IntegrityLabel` s · `SessionId` i · `PidLabel`/`SizeLabel` via `Selected`:
`Selected.DisplayName/ImagePath/ArchitectureLabel/PidLabel/RuntimeLabel/IntegrityLabel/SessionId/ModuleCount/SizeLabel/Company/FileVersion/StartTime(→SelectedStartText)/CanRead/AccessProblem(→SelectedAccessText)/CommandLine(→SelectedCommandLine)/Clr/Clr.DisplayName/Clr.ClrModulePath`.

### WorkspaceView → WorkspaceViewModel / ModuleRecord
`HasTarget` b · `TargetName/TargetRuntime/TargetPid/TargetArch/TargetIntegrity/TargetPath/TargetCommandLine` s ·
`TotalCount/ManagedCount/FrameworkCount/InMemoryCount/DumpedCount/FailedCount` i · `DumpAllCommand/DeepDumpCommand/RescanCommand` cmd · `DeepDumpHint` s ·
`Search` s (TwoWay/PropertyChanged) · `ShowManagedOnly` b (TwoWay) · `HideFramework` b (TwoWay) · `SelectAllCommand/ClearSelectionCommand/ClearFiltersCommand` cmd ·
`SelectedCount` i · `DumpSelectedCommand/DumpModuleCommand` cmd · `IsDumping` b · `DumpStatusText` s · `DumpProgressText` s · `DumpProgress` d/`DumpMax` d (OneWay) · `CancelDumpCommand` cmd ·
`IsModuleListEmpty` b · `ModuleEmptyTitle/Hint` s · `View` ItemsSource · `Selected` TwoWay · `HasSelection` b (inv) ·
`Selected.Name/KindLabel/AddressLabel/SizeLabel/MetadataLabel` · `SelectedModulePath/SelectedModuleOutput/SelectedModuleNote/SelectedFrameworkText/SelectedInMemoryText` ·
`InspectCommand/RevealCommand` cmd · `GoToTargetsCommand` cmd · `TargetIntegrity`/`TargetPid` StringFormats validated.
Row (ModuleRecord): `Name`, `DisplayPath`, `KindLabel`, `IsManaged` (DataTrigger), `MetadataLabel`, `SizeLabel`, `AddressLabel`, `Status` (enum → DataTriggers "Dumped"/"Failed"/"Dumping"), `StatusLabel`, `IsSelected` (TwoWay CheckBox).

### InspectorView → InspectorViewModel / AssemblySummary / models
`HasCaptures` b · `Captures` `IEnumerable<CapturedAssembly>` · `SelectCaptureCommand` cmd (RelativeSource UserControl DataContext) ·
`IsActive` b (OneWay) · `Detail` s tooltip · `OpenFileCommand/ReloadCommand/CopyIdentityCommand/RevealCommand` cmd · `HasAssembly` b/inv ·
`Summary.Name/Version/Kind/Machine/RuntimeVersion/IsReadyToRun/IsMixedMode/TypeCount/MethodCount/FieldCount/SizeLabel/Sha256/SourcePath` ·
`StrongNameText/IlOnlyText/CompileTimestampText` s · `Summary.IsReadyToRun/IsMixedMode` b (DataTriggers/BoolToVis) · `HasError` b · `EmptyStateTitle/Hint` s ·
`SelectedTab` i (TwoWay) · `TypeFilter` s (TwoWay) · `VisibleTypeCount` i · `TypesView` ItemsSource · `SelectedType` TwoWay ·
row TypeEntry: `Name`, `NamespaceLabel` (grouping), `Namespace`, `FullName` (filter), `IndentPixels`, `KindLabel`, `MethodCount` ·
`SelectedTypeHeader/Summary` s · `Methods` ItemsSource · `SelectedMethod` TwoWay · row MethodEntry `Signature`, `IlSizeLabel` ·
`SourceCode` s (OneWay to CodeView.Code) · `CodeKind` enum literal CSharp/Il · `IsDecompiling` b · `CopySourceCommand/CopyIlCommand` cmd ·
`StringFilter` s (TwoWay) · `VisibleStringCount` i · `ShowShortStrings` b (TwoWay) · `StringsView` ItemsSource · `IsStringsEmpty` b · `IsTypesEmpty` b ·
row StringEntry `Display`, `Kind`, `Length`, `Count` · `Summary.References` (row ReferenceEntry `Name`, `Version`, `Culture`, `Kind`) ·
`Summary.TypeCount` StringFormat · `Summary.Protections` (row ProtectionHint `Name`, `SeverityLabel`, `Severity`→`SeverityBrush`, `Description`, `Evidence`) ·
`Summary.Protections.Count` (CountToVis/invert) · `Summary.Sections` (row PeSectionInfo `Name`, `EntropyPercent`+ActualWidth→`EntropyWidth` MultiBinding, `EntropyLabel`, `FlagsLabel`, `VirtualSize` StringFormat, `SizeLabel`) ·
`Summary.Resources` (row ResourceEntry `Name`, `SizeLabel`) · `Summary.ResourceCount` CountToVis · `DataContext.GoToWorkspaceCommand` (RelativeSource Window → ShellViewModel — valid, ShellWindow DataContext is the shell).

### LogView → LogViewModel / LogEntry
`IsAllLevel/IsInfoLevel/IsWarningLevel/IsErrorLevel` b (OneWay) · `SetLevelCommand` cmd · `Search` s (TwoWay) · `AutoScroll` b (TwoWay) ·
`CountLabel` s · `CopyCommand/ExportCommand/ClearCommand` cmd · `View` ItemsSource · `IsViewEmpty` b · `EmptyStateTitle/Hint` s ·
row LogEntry `TimeLabel`, `LevelLabel`, `Level`→`LevelBrush`, `Scope`, `Message` (also tooltip).

**Binding conventions checked:** every `TwoWay` target is a writable property (no read-only `IsXxx` setter); `ItemsSource` bindings point at `ICollectionView` or `ObservableCollection`; `SelectedItem` bindings are TwoWay-capable; all `CommandParameter="…"` literals match the `string` branch in the command handlers; every `StaticResource` key used by the four views resolves in `Themes/Theme.xaml` or `App.xaml` (verified by reflection over the merged resources: `Card*`, `LabelCaps`, `Table*`, `FilterChip`, `Badge*`, `IconButton`, `Obsidian*`, converters).

**No silent binding paths from removed members remain:** grep for `IsListEmpty`, `LastScan` in the four views returns nothing after the rename to `ShowEmptyState`/`ScanSummary`.

---

## 3. CodeView verification detail

| Check | Result |
|---|---|
| Regex compilation | `RegexOptions.Compiled`, `CultureInvariant`, `IgnorePatternWhitespace`; `Singleline` removed (it was meaningless for line-tokenised input). All patterns compile in the app build. |
| Catastrophic backtracking | No nested overlapping quantifiers. Stress test with 2 s/user timeout: unterminated verbatim string (`@""""…` 120 KB) 31 ms; unterminated normal string 139 ms; `/*` storm 43 ms; 180 KB identifier run 3 ms; 300 KB number run 5 ms; mixed quotes/comments 194 ms. Worst case 202 ms only on adversarial single-line input that the app never tokenises (lines > 8 000 chars become one plain run). |
| Token coverage | C#: comment, preprocessor directive (new), string (verbatim/escaped/char), keyword (incl. `nint`/`dynamic`/`scoped`/`with`/`file`…), type (`PascalCase` not followed by `(`), number (hex/binary-underscore/suffixes). IL: comment, `.directive`, string, `IL_xxxx` label, type (`[asm]Ns.Type`, `System.*`), opcode keyword, number. |
| Caret / selection / scroll on re-tokenise | Render is skipped when the text is unchanged; theme re-colouring walks existing `Run.Tag` token kinds instead of rebuilding the document, so scroll/caret/selection survive `ApplyTheme()`. |
| Huge text | 250 000 character cap, 6 000 line cap, 50 000 run cap, 8 000 chars/line token cap; truncation is visible in the document ("… display truncated at … for performance"). A WPF FlowDocument is not virtualised, so a hard cap is the honest fix. |
| Line numbers | Separate read-only gutter with the same font metrics/line height and right-aligned numbers; gutter scroll follows the editor's `ScrollViewer.ScrollChanged` (`SyncGutterScroll`). Screenshot 04: numbers 1..9 align 1:1 with the rendered code lines. |
| Horizontal scrolling | `FlowDocument.PageWidth` is computed from the longest line × measured monospace char width, so long lines scroll instead of wrapping; vertical/horizontal scrollbars `Auto`. |
| Read-only behaviour | `IsReadOnly=true`, caret hidden, `IsDocumentEnabled=false`, editing commands are no-ops; text selection + Ctrl+C still work; tab headers now expose explicit Copy buttons wired to `CopySourceCommand`/`CopyIlCommand`. |
| Theme readiness | Token-kind tags + `ApplyTheme()` + `TryFindResource("CodeText"…,"CodeSelection")` hooks are in place; fallback palette activates until the runtime theme tokens exist (task-9). `CodeLabel` has no contract token — it reuses `CodeType` as fallback; flag for task-9. |

---

## 4. UI strings proof-read

Empty-state / placeholder copy (all four pages) was reviewed for typos, capitalisation and truthfulness:

| Page | Strings reviewed / changed |
|---|---|
| Targets | "PROCESSES / MANAGED CLR / ELEVATED / 64-BIT" (kept, UPPER-case caption convention); "attach targets" → **"CLR hosts"**; "scanned {0}" → **"not scanned yet" / "scanning…" / "scanned HH:mm:ss" / "scan failed · last …"**; "x86 {0}" → **"x86: {0}"**; "No processes match this view" / "Adjust the filters or rescan the machine." (kept, now only shown when the scan is not running); **"Scanning the machine…"** (new); "Select a process", "Double-click a row to attach and map its managed modules.", "Attach & map modules", "Copy path", "Show in folder", "Rescan", filter chip tooltips, "Command line", metric labels (Runtime, Integrity, Session, Modules, Working set, Started, Company, Version) — checked against `ProcessRecord` semantics; no typos found. |
| Workspace | "ATTACHED TARGET", "live", "Dump all managed", "Deep dump (agent)", "Rescan modules", "Find a module…", "Managed only", "Hide framework", "Select all", "Clear", **"Clear filters"** (new), "Dump selected", "MODULE / KIND / CLR / SIZE / BASE / STATUS", "No target attached", "Pick a CLR host from the target list to map its modules, dump assemblies and inspect metadata.", "Open target list", "Select a module", "Dump it to disk, then open it in the inspector for C#, IL and metadata.", row labels (Base address, Mapped size, Metadata, Framework, In memory only, Output, Notes), "Dump this module", "Inspect", "Reveal file", **progress cancel** + **"No modules match these filters"** (new). "Deep dump" explanation tooltip checked for accuracy. |
| Inspector | "CAPTURED", "Open assembly…", "OVERVIEW / EXPLORER / STRINGS / REFERENCES / PROTECTIONS", "IDENTITY / METRICS / RESOURCES / SECTIONS · ENTROPY / PROTECTIONS", "Full name", "Target framework", "Entry point", "Strong name", "IL only", "MVID", "Compiled", "Company", resource/protection empty messages, "no embedded resources", "no protector fingerprints detected", "No protector fingerprints detected — the module looks conventionally compiled.", "Search types…", "Search literals…", "Short strings", "Select a type to decompile", "Select a method to disassemble", **"decompiling…"**, **"Reading assembly metadata…"**, **"This assembly could not be read"**, **"No types match this filter"**, **"No literal strings match this filter"**, "Copy", "Reload", "Go to workspace", empty-state tip. Fixed: `True`/`False` rows → yes/no (UP-13); misleading tip "F5 rescans" → **"F5 resyncs the current page"** and Ctrl+1…6; byte/date formatting clarified ("deterministic"). |
| Activity | "All / Info / Warnings / Errors", "Search the log…", "Follow", "Copy", "Export", "Clear", "N of M entries", "No activity yet", **"No entries match this filter"**, "Clear the visible entries and the engine log buffer", "Copy the entries currently visible…", "Save the entries currently visible…" (tooltips now state the filter semantics). No typos found (`cancelled`, `column`, `literal`, `integrity` etc. spelled correctly). |
| Cross-page | Sentence case for buttons, UPPER CASE for section captions, lowercase for badges/captions — verified consistent; ellipsis character (`…`) used for menus/dialogs; no double spaces; "pid" lowercase everywhere; "CLR" vs ".NET" wording consistent with the shell (`.NET x` for CoreCLR, `.NET Framework x` for Framework). |

---

## 5. Reported out of scope / remaining risks

| Item | Owner | Detail |
|---|---|---|
| `ObsidianProgress` progress bar never fills | ui-core (`Themes/Theme.xaml`) | Template defines `PART_Indicator` but no `PART_Track`; WPF computes the indicator length from the track, so the shell status-bar progress and the Workspace dump progress render as an empty track. Reported to Lead/ui-core. |
| `TargetSession.ReloadModules()` drops native dump state | engine (`Obsidian.Dumper.Core`) | Carries `Status/DumpedPath/Note` only for managed modules. Page-level workaround applied (UP-12); root cause remains. |
| `AssemblySummary.TypeCount` vs `Types` list mismatch | inspector (`Core/Inspection`) | Observed on the sample: header "8 types" while the explorer list shows 9 rows. Likely a nested/compiler-generated counting difference; not a page bug (the page renders both faithfully). |
| Dump cancellation granularity | engine | `ModuleDumper.DumpAsync` aborts between modules; a module already in `DumpOne` runs to completion. The UI reports this honestly ("Modules that were already written are kept on disk"). |
| Log `HasErrors`/unread badge not surfaced in shell | ui-core | The page maintains `HasErrors`/`ErrorCount` (used for future nav badge wiring); no shell consumer exists yet. |
| Theme tokens for pages | ui-pages task-9 | **Resolved in task-9** (see §7): all four views, `CodeView` and the converters now resolve runtime tokens; zero literal `#RRGGBB` remains in the task-9 gate. |
| Interactive-only paths not exercised by the harness | — | Selection-preservation on filter typing, debounce timing, keyboard shortcuts and the no-target Workspace state were verified by code review, not by scripted interaction (the capture harness only clicks nav/tabs). Recommend a short manual pass before release. |

---

## 6. Task-10: pro workflow features (added after the audit)

Implemented on top of the audit fixes; every command is bound and no button is dead (cross-checked XAML `Command="{Binding …}"` against VM command properties).

| Area | Feature | Notes |
|---|---|---|
| Targets | Context menu: attach / attach & dump all managed / copy PID / copy name / copy image path / copy command line / open file location / suspend / resume / terminate | Right-click selects the row under the cursor first; destructive actions go through `ShellViewModel.Confirm` when `AppSettings.ConfirmDestructiveActions`, and process control uses the engine's `ProcessControl.TrySuspend/TryResume/TryTerminate` (honest access-denied messages). |
| Targets | Auto refresh | `Auto` toggle + interval cycle button; interval points 2/5/10/30 s persisted as `AppSettings.AutoRefreshIntervalMs`; timer pauses while the page is not visible or a scan is running. |
| Targets | Sortable columns | PID, PROCESS, RUNTIME, ARCH, INTEGRITY, WORKING SET, STARTED with ▲/▼ indicator; header buttons use the existing caption typography. WORKING SET and STARTED are new columns; session moved into the details panel. |
| Targets | Export view | CSV and JSON of the filtered/sorted view via SaveFileDialog; CSV is quoted + invariant and includes working set/start time/CLR/command line; JSON is camelCase. |
| Targets | Settings consumers | `ShowInaccessibleProcesses` filter, `LazyCommandLine` (false triggers a 4-way background PEB prefetch instead of per-selection), `HighlightElevated` (amber marker), `ShowIntegrity` (integrity column visibility), `ConfirmDestructiveActions`, `AutoRefreshIntervalMs`. |
| Targets | Details panel | Runtime, integrity, elevated, session, modules, working set, started, company, version, command line + copy, CLR runtime card, access warning; suspend/resume/terminate buttons surfaced as well as in the context menu. |
| Workspace | Dump batch controls | "Dump all managed", new "Dump all modules" (honours the native-modules toggle and warns about skipped native modules), "Dump selected", "Dump selected native modules", Cancel on the progress panel (CancellationToken), progress + per-module status. |
| Workspace | Module context menu | Dump this module / dump selected native / inspect / open dumped file / select dumped file in Explorer / copy module path / copy module name (right-click selects the row). |
| Workspace | Quick toggles | Native modules / Raw image / Verify metadata bound directly to `AppSettings` (persisted); "Hide framework" chip also persists `HideFrameworkModules`; "Dumped only" filter added. |
| Workspace | Dump summary + export | `N dumped · M failed · bytes · ms` summary shown in the toolbar; module list CSV/JSON export; "Output folder" button. |
| Workspace | Overwrite confirmation | When `AppSettings.ConfirmOverwrite` is set and the policy is not Rename, the batch asks via `_shell.Confirm` if the output folder already contains matching files (best-effort prediction; the engine owns exact naming). |
| Inspector | Save / export | Save decompiled C# to `.cs`; export a full Markdown or JSON report (identity, metrics, sections, protections, references, types, strings); all via SaveFileDialog with suggested `Documents\OBSIDIAN\exports` location. |
| Inspector | Clipboard | Copy C# / IL / all visible strings / metadata summary / MVID / strong-name identity. |
| Inspector | Code search | "Highlight in C# and IL" box with debounce drives a new `CodeView.SearchTerm` DP that highlights (case-insensitive) matches in-place and reports the match count; highlight is reapplied on theme change. |
| Inspector | Recent captures | Clear recents button + per-capture context menu (reload, select file in Explorer, copy path, remove); active chip state. |
| Inspector | Type tree | Expand/Collapse all (nested types hidden in the collapsed view); selection preserved across filter/sort; save/copy/report commands alive. |
| Activity | Richer log controls | Copy selected lines (Extended selection) or visible lines; export; clear; Trace chip; Info+/Warn+ use minimum-severity semantics matching `AppSettings.LogMinLevel`; timestamps toggle (persisted) and Follow toggle (persisted); "N new entries" jump button when follow is off; `MaxLogEntries` trimming and live re-trim when lowered in Settings. |

Verification for task-10: app builds 0 warnings/0 errors; `tools/ui-capture.ps1` completed all pages; the pure export helpers were exercised with an out-of-tree net10 harness through reflection — 16/16 checks passed (CSV field count/quoting/15 columns, JSON structure + camelCase, Markdown table escaping + no multi-line literal leakage). One environment caveat: the shared `Documents\OBSIDIAN\settings.json` contained leftover verification values (`OverwritePolicy: Fail`, etc.) during the capture run, so the harness dump batch reported "output file already exists" for every module — that is the configured Fail policy, not a page defect; the UI surfaced the failures correctly.

---

## 7. Task-9: runtime theme adoption

| Surface | Change |
|---|---|
| `Views/TargetsView.xaml` | 26 literal colours replaced with palette tokens (accent/cyan/amber/green/red text tokens, PanelAlt empty-state surfaces, Border/BorderHover/BorderStrong, CardHeaderBorder, InputSurface, ControlSurface, Badge borders); all palette references are `{DynamicResource}`; local `ColumnHeaderButton` style uses DynamicResource for foreground. |
| `Views/WorkspaceView.xaml` | 19 literal colours replaced (PanelAlt/BorderHover/BorderStrong empty state, AccentText/CyanText/GreenText/AmberText/RedText metric numbers, AccentTint/AccentBorder icon tiles, GreenTint/RedTint/AmberTint + Badge borders for KIND/STATUS chips, Surface tokens for rows); DynamicResource throughout. |
| `Views/InspectorView.xaml` | 25 literal colours replaced (PanelAlt empty-state, SurfaceRaisedAlt/SurfaceRaised + BorderSoft for explorer separators, CodeBackground for code panes, ControlSurface entropy tracks, PanelAlt protection cards with Badge border tokens, Cyan/Green/Amber/Red/Accent text tokens for header badges and metrics). |
| `Views/LogView.xaml` | Last literal (`SeparatorStrong` empty icon) tokenised. |
| `Controls/CodeView.cs` | No literal colours. Resolves `CodeBackground`, `CodeText`, `CodeKeyword`, `CodeType`, `CodeString`, `CodeNumber`, `CodeComment`, `CodeDirective`, `CodeSelection`, `CodeLineNumber`; subscribes to `ThemeService.ThemeChanged` on `Loaded` and detaches on `Unloaded`, recolouring runs/line numbers in place (caret/selection/scroll preserved) and re-applying search highlights. Palette has no `CodeLabel` token: IL labels share `CodeType` (documented in code). Fallbacks are `Color.FromRgb` values used only before the palette is available. |
| `Common/Converters.cs` | No literal colours. `LogLevelBrushConverter` → LogInfo/LogSuccess/LogWarning/LogError/LogTrace; `SeverityBrushConverter` → SeverityCritical/SeveritySuspicious/SeverityInfo; `BoolToBrushConverter` → StatusOnline/StatusOffline. A `ThemeBrushes` cache resolves via `ThemeService.GetBrush` and is invalidated on `ThemeChanged`; missing tokens fall back to frozen RGB brushes so converters never throw. |
| Verification | `rg '#[0-9A-Fa-f]{6}'` over the four views + `CodeView.cs` + `Converters.cs` returns **no matches**. App builds 0 warnings/0 errors. Dark and light screenshot passes through `tools/ui-capture.ps1` (see `artifacts/shots`); the light-theme pass is the acceptance gate used by the Lead for final sign-off. |

