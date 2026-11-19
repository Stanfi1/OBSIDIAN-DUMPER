# UI shell / settings / app infrastructure — audit report

Area: `src/OBSIDIAN/App.xaml(.cs)`, `ShellWindow.xaml(.cs)`, `Themes/`, `Services/`, `Common/ObservableObject.cs`, `ViewModels/ShellViewModel.cs`, `ViewModels/SettingsViewModel.cs`, `ViewModels/InjectViewModel.cs` (nav wiring only), `Views/SettingsView.xaml(.cs)`, `OBSIDIAN.csproj`.
Owner: ui-core. Baseline: 1.0.0 tree (version bump to 1.1.0 landed during the audit).

Verification: `powershell -NoProfile -ExecutionPolicy Bypass -File tools/build.ps1 -Target app` → **BUILD OK** for every file in this area (the only warnings in the build are pre-existing `CodeView.cs` CS8602 from the pages area, fixed by ui-pages). Runtime checks were done with startup-argument smoke runs (`--attach`, `--page`, `--theme`, deferred attach) and UI screenshots are scheduled through `tools/ui-capture.ps1` after the theme work (task-7).

Severity scale: critical / major / minor / suggestion.

## Findings and fixes

| id | sev | file:line | problem | fix | verification |
|---|---|---|---|---|---|
| SH-01 | critical | `Common/ObservableObject.cs:121` | `AsyncRelayCommand.Execute` was `async void` with no catch: any unobserved handler fault became an unhandled dispatcher exception (crash dialog / lost command). | Caught `OperationCanceledException` as a normal outcome, routed every other exception to new `AsyncRelayCommand.ExceptionHandler`; the app installs a handler that logs to Activity and shows a toast. Installed in `App.OnStartup`. | Build; commands exercised via startup (`--dump`, attach flows) without dispatcher faults. |
| SH-02 | major | `ViewModels/ShellViewModel.cs:453` (original 382–391) | Toast expiry used `Task.Delay(6s).ContinueWith(dispatcher.BeginInvoke)`. Timers survived window close (posting to a shut-down dispatcher), were never cancelled when a toast was dismissed manually, and removing a toast twice raced the queue. | One `DispatcherTimer` per toast, tracked in a dictionary; `DismissToast` stops the timer; `ClearToasts`/`Shutdown` stop all; queue capped at 4. Toast removal is idempotent. | Build; toast/dismiss path reviewed; shutdown path covered by `OnExit`. |
| SH-03 | major | `ViewModels/ShellViewModel.cs:326`, `403` | No `Confirm` helper for destructive actions (needed by Targets/Injection pages). | Added `bool Confirm(string title, string message, string confirmLabel = "Continue", bool destructive = false)` (owner=`MainWindow`, Warning vs Question icon, default Cancel) and logs the decision at Info level. | Builds against Lead's `InjectView`/`InjectViewModel` integration. |
| SH-04 | major | `ViewModels/ShellViewModel.cs:125` | Navigation was addressed by magic index (`Navigation[1].Badge`), so inserting a page (Injection) silently wrote the badge onto the wrong item. | Nav items are built once and kept in typed fields (`_workspaceNav`, `_injectNav`); badges/shortcuts use the fields/keys. Six entries now (Injection at index 3, Ctrl+1..6). | Build; `Ctrl+6`/sidebar order matches Lead's agreed layout. |
| SH-05 | major | `ViewModels/ShellViewModel.cs:120` | The new Injection page was unreachable: no `InjectPage`, no navigation entry, no `DataTemplate`, no shortcut. | Added `InjectPage = new InjectViewModel(this)`, nav entry `("inject", "Injection", "Load a library into the target", \uE896)`, `DataTemplate` in `App.xaml` and Ctrl+1..6 shortcut handling in `ShellWindow`. | Build; navigation path exercised in smoke run; full page render verified after task-7 tokens. |
| SH-06 | major | app wiring (`ShellViewModel.cs:374`, `420`) / `ViewModels/InjectViewModel.cs:31` | `InjectViewModel.HasTarget` never refreshed after attach/detach (page would stay disabled). | `InjectPage.Refresh()` called after `Workspace.Load`/`Inspector.SeedFromSession` in `AttachAsync` and in `Detach`; `GoToInjectionCommand` added. | Build; property raises on shell events. |
| SH-07 | major | `ViewModels/ShellViewModel.cs:533` | No shutdown hook: 1 s clock `DispatcherTimer` and toasts kept running after the window closed, target session was never disposed on exit, final settings were not flushed. | Added `Shutdown()` (stop clock, clear toasts, dispose session, persist settings) and `App.OnExit` override calls it. | Build; app closes cleanly (smoke run). |
| SH-08 | major | `Services/AppSettings.cs:163` (original 103–118) | `SettingsStore.Save`: `Changed?.Invoke()` was mis-indented inside `try`; it was skipped when `SettingsPath` was empty or the write threw, and a path without a directory threw through `CreateDirectory("")`. Failures were swallowed. | `TrySave(out error)` returns success, always raises `Changed` from `finally`, raises `SaveFailed` for the shell toast, picks a writable path when none is set, and never throws. `Save()` kept as wrapper. | Build; failure path covered by empty-path logic review; shell surfaces `SaveFailed`. |
| SH-09 | major | `Services/AppSettings.cs:63` | `ResetDefaults` in the view model copied only 9 of 11 settings by hand; `ConfirmOverwrite` (and every future property) was left untouched. | Reflection-based `AppSettings.CopyFrom` + `ResetToDefaults` (excludes `SettingsPath`) so resets always cover every persisted property; reset now asks for confirmation and re-applies external consumers. | Build; UI reset command reviewed. |
| SH-10 | major | `Views/SettingsView.xaml:28,39` | Output directory / filename template used `UpdateSourceTrigger=LostFocus`; typing and then pressing F5/Enter left the old value persisted. | `UpdateSourceTrigger=PropertyChanged, Delay=400` + token tooltip; save failures now surface as a toast via `SaveFailed`. | Build; binding reviewed against setters. |
| SH-11 | major | `App.xaml.cs:294` (original `StartupOptions.Parse` 146–177) | Startup args were silently ignored on unknown option, missing value, invalid pid, unknown `--page`; no `--arg=value` form, no `--help`, no `--theme` override. | Rewritten parser: warnings collected (logged to Activity), `--key=value` accepted, `--help` usage dialog, `--theme <dark\|light\|system>` validated and stored for `ThemeService`, `--page` validated against the live navigation keys, `--dump` value warning. | Build; startup smoke with `--attach/--page/--theme` produced the expected log lines. |
| SH-12 | major | `App.xaml.cs:96,108,150` (original fixed 600/1200 ms delays) | Deferred `--attach`/`--select` raced the background process scan (fixed delays); a slow scan attached to the wrong/absent record or nothing. | `FindProcessAsync` polls the live list for up to 5 s before the direct `Process.GetProcessById` fallback; warns when the pid disappears. | Build; startup attach smoke test. |
| SH-13 | major | `ShellWindow.xaml.cs:59,86` (original 58–72) | Maximise handling set `MaxHeight/MaxWidth = SystemParameters.MaximizedPrimaryScreenHeight/Width` — wrong monitor, wrong DPI, and a `WindowStyle=None` window still covered the taskbar. Maximise button never switched to the restore glyph. Snap Layouts were unsupported (custom chrome). | `WM_GETMINMAXINFO` hook clamps the maximised rectangle to the *current monitor work area*; `StateChanged` flips glyph/tooltip (`E922`⇄`E923`) and keeps the 6 px inset; `WM_NCHITTEST` returns `HTMAXBUTTON` over the maximise button on Windows 11 so the OS Snap Layouts flyout and native maximise/restore work. | Build; maximised bounds / flyout verified with a short live run (see task-7 screenshots). |
| SH-14 | major | `Themes/Theme.xaml:675` (reported by ui-pages) | `ObsidianProgress` template lacked `PART_Track`, so `ProgressBar` never sized `PART_Indicator`: dump progress bars appeared stuck. | Added `PART_Track` + kept `PART_Indicator`; deprecated colours untouched (theme conversion follows in task-7). | Build; template structure checked against WPF contract. |
| SH-15 | major | `ViewModels/ShellViewModel.cs:171` + `ViewModels/SettingsViewModel.cs` + `ShellWindow.xaml:179` (original hardcoded `v1.0.0`, `1.0.0 initialised`, `4.4`, `9.1`, `.NET 10`, `1.0.0` About) | Product/engine versions were duplicated in five places; the 1.1.0 bump would have left the shell lying. | New `Services/AppInfo.cs` reads assembly metadata (`AssemblyInformationalVersion`, dependency assembly versions, `RuntimeInformation`) and is the only source; shell log line, status bar, sidebar engine block and About card bind to it. `app.manifest` identity bumped to 1.1.0.0. | Build; `grep "1.0.0"` over source tree clean (bin/obj excluded). |
| SH-16 | minor | `Common/ObservableObject.cs:81,147` | `RelayCommand.RaiseCanExecuteChanged` always marshalled via `Application.Current?.Dispatcher`, so it silently dropped when no application existed (designer/tests) and always deferred even when already on the UI thread. | Shared `UiDispatch.Run`: runs inline without an application or on the UI thread, otherwise `BeginInvoke(DataBind)`, and skips a shut-down dispatcher. Both command types use it. | Build; command invalidation paths reviewed. |
| SH-17 | minor | `ViewModels/ShellViewModel.cs:326` (original 288–344) | `AttachAsync` returned silently when busy, left the "Attaching to…" status on failure, and disposed the previous session without protection (a failed dispose crashed the attach path). | Busy attaches now toast; status is reset on every failure; previous session dispose is guarded and logged; badge/Inject refresh after state change. | Build; code review. |
| SH-18 | minor | `ViewModels/ShellViewModel.cs:403` (original 346–369) | `Detach` could throw from `Dispose`, left progress visible and did not refresh the Injection page. | Guarded dispose, `HideProgress`, `InjectPage.Refresh()`, badge/uptime reset, navigate to Targets. | Build; code review. |
| SH-19 | minor | `ViewModels/ShellViewModel.cs` (original `_attachedAt` after ctor) | `_attachedAt` sat in the middle of the class after the constructor; uptime formatting wrapped after 24 h (`hh`), started at process start before any attach. | Field moved next to the other session state (never initialized to "now"); uptime is `—` without a target and formatted from `TotalHours`; clock tick clamps negative deltas. | Build; `--attach` smoke run showed `00:00:0x`. |
| SH-20 | minor | `ViewModels/ShellViewModel.cs` (original RefreshCommand 125–131) | F5/refresh silently did nothing on Inspector/Injection/Activity/Settings. | Type-switch dispatch: Targets rescan, Workspace rescan, Activity view refresh, Settings refresh; Injection intentionally no-op (no data to reload). | Build; code review. |
| SH-21 | minor | `Services/AppSettings.cs:117` (original 52–81) | `EffectiveOutputDirectory` silently re-pointed `OutputDirectory` when the configured folder was not writable: dumps went elsewhere while Settings kept showing the old path. | Added `OutputDirectoryFallback` event; `SettingsStore` subscribes and raises `Changed` (on the UI thread), so Settings refreshes when the fallback kicks in. The persisted preference is deliberately not overwritten. | Build; event wiring reviewed. |
| SH-22 | minor | `Services/AppSettings.cs:218,239` (original 122–145) | Settings load swallowed corrupt-file errors and JSON options were too strict for forward compatibility (no comments/trailing commas/enums). | `LoadError` exposed and logged to Activity; options accept comments/trailing commas/case-insensitive names and a `JsonStringEnumConverter` (needed by schema v2). `Normalise()` repairs empty output/template on load. | Build; load path reviewed. |
| SH-23 | minor | `ViewModels/SettingsViewModel.cs:190–253` (original 151–180) | Browse/open-output/open-log-folder commands could throw (dialog, `Process.Start`, read-only path) into the command wrapper; settings page did not refresh when the store changed elsewhere. | All three commands wrapped with honest toasts; view model subscribes to `SettingsStore.Changed` and refreshes; failed saves toast once through `SaveFailed`. | Build; command review. |
| SH-24 | minor | `ShellWindow.xaml.cs:149` (original 38–56) | Page transition could be re-triggered while a previous animation clock was still active; disabling animations did not clear a running clock. | `AnimatePage` clears opacity/Y animations (`BeginAnimation(..., null)`) before starting; animation-less path resets transform+opacity. | Build; navigation smoke run. |
| SH-25 | minor | `ShellWindow.xaml.cs:217` (original 86–92) | Ctrl shortcut range was `D1..D5`; the sixth sidebar entry (Injection) could not be reached; Escape did not dismiss toasts; the toast X button removed the item but left its timer running. | Ctrl+1..6 map to sidebar order; Escape clears toasts; dismiss goes through `ShellViewModel.DismissToast` (stops the timer); dead empty `Button_Click` handler removed. | Build; shortcut mapping reviewed. |
| SH-26 | minor | `ShellWindow.xaml:5` (original `Title="OBSIDIAN"`) | Window title never reflected the attached target. | Bound to `ShellViewModel.WindowTitle` (`OBSIDIAN Dumper — name.exe (pid n)`). | Build; binding reviewed. |
| SH-27 | minor | `Views/SettingsView.xaml:138` (original literal `#2A2144`) | Hardcoded divider colour duplicated the card border token; would not follow the light theme. | Replaced with the existing `Divider` style. | Build; XAML reviewed (theme-wide literal removal is task-7). |
| SH-28 | minor | `Views/SettingsView.xaml:200` | Shortcut help still advertised `Ctrl+1…5`. | Updated to `Ctrl+1…6`. | Build; UI copy proof-read. |
| SH-29 | minor | `App.xaml.cs:79,235` (original 17–136) | `OnExit` did no cleanup; crash log was only written next to the executable (often read-only), so diagnostic evidence was lost; command faults were only visible as a dialog. | `OnExit` disposes the shell; crash log falls back to `%TEMP%`; command exceptions logged + toasted; DWM theme helper parameterised (`dark`) ready for runtime theme switching (task-7 applies it on `ThemeChanged`). | Build; code review. |
| SH-30 | suggestion | `Services/AppSettings.cs:46` | No repair path for `null`/whitespace persisted values. | `Normalise()` on load keeps the app usable with a hand-edited file. | Build. |
| SH-31 | suggestion | `OBSIDIAN.csproj` | No defects found in project/targets; the agent payload staging targets are correct. `app.manifest` carried 1.0.0.0 identity. | Manifest identity bumped with the product version; no csproj change required. | Full solution/app builds. |

Deferred (correctly owned by the next tasks, tracked so nothing is lost):

* `SH-D1` (task-7, major): FIXED — see "Runtime theme system (task-7)" below. Literal colours removed from `Theme.xaml`/`ShellWindow.xaml`/`SettingsView.xaml`; palettes split; `AppTheme`/`ThemeService`; DWM follows the effective theme; Appearance section added.
* `SH-D2` (task-8, major): settings schema v2 (`docs/SETTINGS-CONTRACT.md`) — theme/accent, density, dumping/overwrite policy/parallel threads/auto-open, processes, injection, activity, diagnostics; wire every consumer. Engine support for `StripBoundImports`, `OverwritePolicy` and `DumpOptions.WorkDirectory` requested; first two already landed.
* `SH-D3` (ui-pages, reported): `LogViewModel` trims at a hardcoded 4000 entries and `LogBus` capacity is fixed in the shell ctor — task-8 exposes `MaxLogEntries`; the Log page must consume it (request sent to Lead/ui-pages).
* `SH-D4` (ui-pages, reported): mojibake `â€”` literals in `ViewModels/WorkspaceViewModel.cs` (encoding damage in source, files are valid UTF-8) — ui-pages owns that file.

## Verified clean

* `App.xaml`: converter registrations match `Obsidian.Converters` types; every `DataTemplate` (Targets/Workspace/Inspector/Inject/Activity/Settings) has a matching view class; merged dictionary order is `palette → Theme.xaml` after task-7.
* `ShellWindow.xaml` bindings against `ShellViewModel`: `WindowTitle`, `HasTarget`, `TargetName/Pid/Arch/Runtime`, `Navigation/SelectedNav`, `PageTitle/Subtitle`, `RefreshCommand`, `Workspace.OpenOutputCommand`, `OpenActivityCommand`, `OpenSettingsCommand`, `DetachCommand`, `StatusText`, `ProgressMax/Value/IsProgressVisible`, `SessionUptime`, `TargetPid`, `ClockText`, `VersionText`, `Toasts`, `Toast.Title/Message/Glyph/Accent`, `Workspace.TotalCount/DumpedCount`, `DnlibVersion/DecompilerVersion/RuntimeLabel`.
* `SettingsView.xaml` bindings against `SettingsViewModel`: `OutputDirectory`, `FileNameTemplate`, `VerifyMetadata`, `InvalidateStrongName`, `StripDebugDirectory`, `HideFrameworkModules`, `DumpNativeModules`, `RawImage`, `Animations`, `VersionText`, `EngineText`, `SettingsPath`, `BrowseOutputCommand`, `OpenOutputCommand`, `ResetCommand`, `OpenLogsCommand`.
* `ObservableObject.Set/Raise`: change detection, `[CallerMemberName]`, multi-name overload; no thread-affinity violations outside the documented dispatcher helpers.
* `SettingsStore` candidates/probe logic (Documents → LocalAppData → app dir) and `Clone()`; settings are never allowed to break a dump.
* `ShellWindow` chrome: WindowChrome caption height, drag/resize, double-click title bar (WPF caption), min/max/close routing, `RootBorder` inset.
* `App` startup order: exception handlers installed before any window; options parsed before shell construction; deferred work (`--attach`, `--select`, `--open`) never blocks the UI thread.
* `Themes/Theme.xaml`: style keys referenced by `App.xaml`, `ShellWindow.xaml`, `SettingsView.xaml` and the pages all exist; no dangling `StaticResource` in shell-owned files (palette-token conversion pending task-7).
* No `1.0.0` strings left in shell/settings UI copy (all version strings flow from `AppInfo`).

## Runtime theme system (task-7)

Implemented exactly against `docs/THEME-CONTRACT.md` (including the Lead's
`Divider` → `Separator` / `SeparatorStrong` rename, because `Divider` is a style
key; no palette token named `Divider` is defined).

| id | sev | file | problem | fix | verification |
|---|---|---|---|---|---|
| TH-01 | major | `Themes/Palette.Dark.xaml`, `Themes/Palette.Light.xaml` | Palette was baked into `Theme.xaml`; no light mode at all. | Two palette dictionaries with the full contract key set (identical keys incl. `Separator`/`SeparatorStrong`, `*Color` pairs, gradients, glow/shadow effects, code/log/severity tokens; `HeroBorder` kept as an alias of `CardHeaderBorder`). | Build; palette swap exercised by the acceptance run. |
| TH-02 | major | `Themes/Theme.xaml` | Palette definitions lived in the style sheet, so a merged palette would have been shadowed by the literal values; styles used `StaticResource` (no live recolor). | Theme.xaml is styles/fonts only; every palette colour is `{DynamicResource ...}`; zero `#RRGGBB` remains (verified by grep). All palette keys moved out (Lead acceptance grep passes). | Build; runtime recolor observed live. |
| TH-03 | major | `Services/ThemeService.cs` | No runtime theme engine. | `AppTheme`/`AccentPreset` enums + static `ThemeService`: `Initialise`/`Apply` swap merged dictionary index 0, resolve `System` from `HKCU\...\Personalize\AppsUseLightTheme`, re-apply on `SystemEvents.UserPreferenceChanged` (marshalled to the UI thread), apply accent overrides (`Accent`, `AccentSoft`, `AccentTint`, `AccentBorder`, `AccentBorderStrong`, `AccentText`, `AccentGradient`, `AccentGradientH`, `AccentGlow` with mode-aware opacity), and fire `ThemeChanged` on the UI thread after the swap. `GetBrush`/`GetAccentPresetBrush` helpers for code-behind and the settings swatches; `Shutdown` unhooks the OS listener. Note: enum renamed from `ThemeMode` to `AppTheme` because .NET 10 WPF added `System.Windows.ThemeMode` (ambiguous with `using System.Windows`). | Build; live dark/light/accent switching verified. |
| TH-04 | major | `App.xaml`, `App.xaml.cs` | No palette merge; theme never loaded before the window; DWM dark mode hardcoded; no `--theme` effect. | App.xaml merges palette (index 0) + styles; `OnStartup` calls `ThemeService.Initialise` from persisted settings (honouring `--theme` as a non-persisted override with an "override for this run only" hint in the Appearance section) **before** the window exists; DWM immersive dark mode follows `ThemeService.IsDark` and is re-applied on `ThemeChanged`; `OnExit` unhooks. | Build; screenshot `01-persisted-light-cyan` starts light; dark run shows dark DWM chrome. |
| TH-05 | major | `Services/AppSettings.cs`, `ViewModels/SettingsViewModel.cs`, `Views/SettingsView.xaml` | Theme/accent were not settings; no Appearance UI. | Persisted `Theme` (`AppTheme`, default System) and `AccentColor` (`AccentPreset`, default Violet), clamped on load; SettingsViewModel exposes segmented System/Dark/Light + accent swatch list + summary, applies instantly and persists; SettingsView has the APPEARANCE card (theme chips, accent swatches, page-transition toggle, live summary). | Build; UIAutomation test toggled dark + green accent, `settings.json` showed `"Theme":"Dark"`/`"AccentColor":"Green"`, restart rendered dark+green. |
| TH-06 | major | `ShellWindow.xaml`, `ViewModels/ShellViewModel.cs` | Window background, ambient glows, title bar, sidebar, command bar, status bar and toasts carried literal colours; toast accent was a hardcoded hex string that could not follow the theme. | Every literal replaced with contract tokens; `Toast.Accent` is now a resolved theme `Brush` (from `AccentToken` mapping to Green/Amber/Red/Cyan) refreshed on `ThemeChanged`. | Build; both acceptance screenshots show correctly recoloured chrome/toasts surface. |

Acceptance evidence (ui-core's own run, exclusive slot):

* `artifacts/task7/01-persisted-light-cyan.png` — pre-seeded `Theme=Light, AccentColor=Cyan`; window opens light with cyan accent (theme loaded before window creation).
* `artifacts/task7/02-live-dark.png`, `03-live-dark-green.png` — live switch via the real UI (UIAutomation): dark theme, then green accent recolours gradients/glows/nav bar.
* After the clicks `%Documents%\OBSIDIAN\settings.json` contained `"Theme": "Dark"` and `"AccentColor": "Green"`; `04-restarted-dark-green.png` proves persistence across restart.
* `05-window-normal.png`, `06-window-maximised.png` — maximised window rect equals the monitor work area exactly (1920×1032 vs 1920×1032, delta 0; taskbar preserved); maximise button flips to the restore glyph.
* `07-snap-layouts-hover.png` — hovering the restore/maximise button shows the Windows 11 Snap Layouts flyout (WM_NCHITTEST → HTMAXBUTTON works with WindowChrome).
* `--theme light` works end-to-end for the Lead's ui-capture light phase.

Remaining risk: pages (`Views/Targets|Workspace|Inspector|LogView.xaml`) still reference palette colours with `StaticResource`, so they recolour on navigation/restart rather than instantly on a live palette swap; ui-pages converts them in task-9. This does not affect startup in either theme.

## Settings expansion (task-8)

`AppSettings` is now schema v2 (see `docs/SETTINGS-CONTRACT.md`): forward-compatible
JSON load (case-insensitive names, comments, trailing commas, string enums),
`schemaVersion` marker with migration (<1 → 1 → 2; a newer file version is left
alone), and `Normalise()` clamps every numeric/enum/string value so a corrupt or
hand-edited file cannot break a dump. `SettingsView` is a sectioned, scrollable
panel: Appearance / Dumping / Processes / Injection / Activity in the left column,
About / Diagnostics / Legal / Shortcuts on the right. No literal colours, all
labels have hints, combos use the themed `ObsidianComboBox` style.

| setting | default | consumer |
|---|---|---|
| `Theme` | `System` | `ThemeService` + App startup (task-7) |
| `AccentColor` | `Violet` | `ThemeService` accent overrides (task-7) |
| `Density` (`UiDensity`) | `Comfortable` | `DensityService` writes `CardPadding`, `CardCornerRadius`, `CardTightPadding`, `CardTightCornerRadius`, `TableRowMargin`, `TableRowContentMargin`, `PageMargin`, `NavItemPadding`, `CommandBarPadding`, `SettingsSectionSpacing` as DynamicResources consumed by Theme.xaml + shell (live) |
| `Animations` | `true` | `ShellWindow.AnimatePage` page transitions (live) |
| `ShowEngineVersions` | `true` | sidebar ENGINE block visibility (`ShellViewModel.ShowEngineVersions`, live) |
| `OutputDirectory` | `%Documents%\OBSIDIAN\dumps` | `EffectiveOutputDirectory`, `ToDumpOptions` |
| `FileNameTemplate` | `{name}` | `ToDumpOptions` + live preview/validation in Settings |
| `RebuildPe` | `true` | `ToDumpOptions.RebuildPe` |
| `StripDebugDirectory` | `true` | `ToDumpOptions.StripDebugDirectory` |
| `StripBoundImports` | `true` | `ToDumpOptions.StripBoundImports` (engine landed) |
| `InvalidateStrongName` | `true` | `ToDumpOptions.InvalidateStrongName` |
| `VerifyMetadata` | `true` | `ToDumpOptions.VerifyWithMetadata` |
| `HideFrameworkModules` | `false` | `ToDumpOptions.IncludeFrameworkModules` + Workspace.HideFramework (live) |
| `DumpNativeModules` | `false` | `ToDumpOptions.DumpNativeModules` |
| `RawImage` | `false` | `ToDumpOptions.RawImage` |
| `OverwritePolicy` (`DumpOverwritePolicy`) | `Rename` | `ToDumpOptions.OverwritePolicy` (Fail/Overwrite/Rename honoured by ModuleDumper) |
| `ConfirmOverwrite` | `false` | Workspace dump confirmation (ui-pages consumer, requested) |
| `AutoOpenOutputFolder` | `false` | `ShellViewModel.NotifyDumped` opens the output folder after a successful batch |
| `ParallelDumpThreads` | clamp(ProcessorCount/2,1,4) | `ToDumpOptions.MaxDegreeOfParallelism` (clamped 1..8) |
| `DeepDumpWorkDirectory` | `""` = auto | `ToDumpOptions.WorkDirectory` → `DeepDumper` per-run GUID folder |
| `ShowInaccessibleProcesses` | `true` | Targets page filter (ui-pages consumer, requested) |
| `LazyCommandLine` | `true` | Targets page command-line loading (ui-pages, requested) |
| `AutoRefreshIntervalMs` | `0` (off; 2/5/10/30 s) | Targets page refresh timer (ui-pages, requested) |
| `RememberFilters` | `true` | shell restores/saves `LastTargetsFilter`, `LastTargetsSearch`, `LastWorkspaceSearch`, `LastSelectedPid` |
| `ConfirmDestructiveActions` | `true` | Targets suspend/resume/terminate confirmations (ui-pages task-10) |
| `HighlightElevated` | `true` | Targets elevated rendering (ui-pages, requested) |
| `ShowIntegrity` | `true` | Targets integrity column (ui-pages, requested) |
| `ConfirmInjection` | `true` | Injection page confirmation (Lead integration; property exposed) |
| `DefaultInjectionMethod` (`InjectionMethod`) | `CreateRemoteThread` | Injection page initial method (Lead integration) |
| `InjectionDllDirectory` | `""` | Injection page file dialog initial directory (Lead integration) |
| `AutoUnloadOnDetach` | `false` | `ShellViewModel.Detach` calls `InjectPage.UnloadAllOnDetach()` before disposing the session (live) |
| `MaxLogEntries` | `2000` (500..50000) | `LogBus` ring capacity on startup + Activity page trimming (ui-pages consumer, requested) |
| `LogShowTimestamps` | `true` | Activity row timestamps (ui-pages, requested) |
| `LogAutoScroll` | `true` | Activity page initial + live via `LogPage.AutoScroll` |
| `LogMinLevel` (`LogLevel`) | `Trace` | Activity page initial filter via `ShellViewModel.ApplyLogLevelFilter` (Trace→all, Info→info, Warning→warning, Error→error; page filter uses minimum-severity semantics and owns the setting after startup) |
| `SchemaVersion` | `2` | `SettingsStore.Load` migration/validation |

Diagnostics/About card: version + engine versions from `AppInfo`, settings path,
output path, **Copy diagnostics** (version, runtime, theme/accent, paths, attach
state, last 25 log lines to clipboard), open settings folder, reset defaults
(confirmation + every external consumer re-applied), third-party notices (copied
next to the executable via a linked csproj item), legal/authorised-use notice.

Known staged dependencies (reported to ui-pages; since resolved by their pass):
`ShowInaccessibleProcesses`, `LazyCommandLine`, `AutoRefreshIntervalMs`,
`ConfirmDestructiveActions`, `HighlightElevated`, `ShowIntegrity` (Targets page),
`ConfirmOverwrite` (Workspace), `MaxLogEntries` trim, `LogShowTimestamps` and the
minimum-level semantics for `LogMinLevel` (Activity page) were wired in
ui-pages-owned files; all read `_shell.SettingsStore.Current` at the point of use,
so no new API was needed.

Acceptance evidence (ui-core's own run under `artifacts/.gui-lock`):

* Pre-seeded a **v1-shaped** `settings.json` (`Theme=Light`, `AccentColor=Cyan`,
  `ParallelDumpThreads=42`, `MaxLogEntries=999999`, `AutoRefreshIntervalMs=1234`,
  `OverwritePolicy=Fail`, `StripBoundImports=false`) and started `--page settings`:
  the app opened light+cyan, and after any save the file contained
  `SchemaVersion: 2`, `ParallelDumpThreads: 8`, `MaxLogEntries: 50000`,
  `AutoRefreshIntervalMs: 0` while `OverwritePolicy "Fail"` and
  `StripBoundImports false` round-tripped — migration + clamping proven.
* `artifacts/task8/01-settings-light-cyan.png` — sectioned settings page, light
  theme, cyan accent, new APPEARANCE/DIAGNOSTICS cards.
* UIAutomation toggled "Compact density" and "Show engine versions in the
  sidebar": `Density: "Compact"` and `ShowEngineVersions: false` written to
  `settings.json`; `02-settings-compact.png` / `03-settings-compact-no-engine.png`
  show the tighter cards and the hidden sidebar ENGINE block live.
* Restart (`04-settings-restart.png`) kept compact density and the hidden ENGINE
  block — persistence across restart.
* `-Target app` build: BUILD OK, 0 warnings, 0 errors.
