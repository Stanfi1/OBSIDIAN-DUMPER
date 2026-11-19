# Settings contract (AppSettings schema v2)

> **Status: implemented in 1.1.0.** `Services/AppSettings` matches this document
> (schema v2, forward-compatible JSON, clamped values, v1→v2 migration, reflection
> based reset). Keep this file and the real model in sync when adding settings.

`src/OBSIDIAN/Services/AppSettings.cs` (ui-core owns it) is the single source of
truth for persisted operator settings. This file freezes the **public property
names and types** so that the shell, the page view models, the injection page and
the final verification script agree without inventing near-duplicates such as
`LastDllDirectory` vs `InjectionDllDirectory`.

Rules:

- Property names are PascalCase and frozen. Add new properties at the end of the
  matching section; never rename one that is already listed here.
- Every property must be persisted by `SettingsStore.Save()` (System.Text.Json,
  default camelCase is not used — the file keeps the CLR names) and validated /
  clamped on load (corrupt or out-of-range values fall back to the default).
- Every property must have a real consumer. A setting that nothing reads is a
  defect; the consumer is listed below.
- Defaults are chosen so a fresh `settings.json` behaves exactly like today.
- `SettingsPath` stays `[JsonIgnore]`.
- Schema changes bump `SchemaVersion` (int, currently `1` when absent).

## Appearance

| Property | Type | Default | Consumer |
|---|---|---|---|
| `Theme` | `Obsidian.Services.AppTheme { System, Dark, Light }` | `System` | `ThemeService.Apply`, App startup || `AccentColor` | `Obsidian.Services.AccentPreset { Violet, Cyan, Green, Amber, Red }` | `Violet` | `ThemeService.Apply` accent overrides |
| `Density` | `Obsidian.Services.UiDensity { Comfortable, Compact }` | `Comfortable` | shell root resources (row height / card padding) |
| `Animations` | `bool` | `true` | page transitions, toast fades (shell) |
| `ShowEngineVersions` | `bool` | `true` | sidebar engine block visibility |

## Dumping

| Property | Type | Default | Consumer |
|---|---|---|---|
| `OutputDirectory` | `string` | `%Documents%\OBSIDIAN\dumps` | `AppSettings.EffectiveOutputDirectory`, `ToDumpOptions` |
| `FileNameTemplate` | `string` | `"{name}"` | `ToDumpOptions` |
| `RebuildPe` | `bool` | `true` | `ToDumpOptions.RebuildPe` |
| `StripDebugDirectory` | `bool` | `true` | `ToDumpOptions.StripDebugDirectory` |
| `StripBoundImports` | `bool` | `true` | `ToDumpOptions.StripBoundImports` |
| `InvalidateStrongName` | `bool` | `true` | `ToDumpOptions.InvalidateStrongName` |
| `VerifyMetadata` | `bool` | `true` | `ToDumpOptions.VerifyWithMetadata` |
| `HideFrameworkModules` | `bool` | `false` | `ToDumpOptions.IncludeFrameworkModules`, Workspace page |
| `DumpNativeModules` | `bool` | `false` | `ToDumpOptions.DumpNativeModules` |
| `RawImage` | `bool` | `false` | `ToDumpOptions.RawImage` |
| `OverwritePolicy` | `DumpOverwritePolicy { Fail, Rename, Overwrite }` | `Rename` | `ToDumpOptions.OverwritePolicy` (engine) |
| `ConfirmOverwrite` | `bool` | `false` | Workspace page overwrite confirmation |
| `AutoOpenOutputFolder` | `bool` | `false` | shell after a successful dump batch |
| `ParallelDumpThreads` | `int` (1..8) | `clamp(ProcessorCount/2, 1, 4)` | `ToDumpOptions.MaxDegreeOfParallelism` |
| `DeepDumpWorkDirectory` | `string` (empty = auto) | `""` | `DeepDumper` work-directory candidate override |

## Processes

| Property | Type | Default | Consumer |
|---|---|---|---|
| `ShowInaccessibleProcesses` | `bool` | `true` | Targets page filter |
| `LazyCommandLine` | `bool` | `true` | Targets page (load command line only for the selection) |
| `AutoRefreshIntervalMs` | `int` (0 = off, else 2000/5000/10000/30000) | `0` | Targets page refresh timer |
| `RememberFilters` | `bool` | `true` | Targets/Workspace page filters persisted across sessions |
| `ConfirmDestructiveActions` | `bool` | `true` | Targets suspend/resume/terminate |
| `HighlightElevated` | `bool` | `true` | Targets page badge rendering |
| `ShowIntegrity` | `bool` | `true` | Targets page integrity column |

## Injection

| Property | Type | Default | Consumer |
|---|---|---|---|
| `ConfirmInjection` | `bool` | `true` | Injection page confirmation dialog |
| `DefaultInjectionMethod` | `Obsidian.Dumper.Core.Injection.InjectionMethod` | `CreateRemoteThread` | Injection page initial selection |
| `InjectionDllDirectory` | `string` | `""` | Injection page file dialog initial directory |
| `AutoUnloadOnDetach` | `bool` | `false` | shell detach path (unload last injected modules) |

## Activity

| Property | Type | Default | Consumer |
|---|---|---|---|
| `MaxLogEntries` | `int` (500..50000) | `2000` | `LogBus` capacity, Log page trimming |
| `LogShowTimestamps` | `bool` | `true` | Log page row template |
| `LogAutoScroll` | `bool` | `true` | Log page |
| `LogMinLevel` | `Obsidian.Dumper.Core.Diagnostics.LogLevel` | `Trace` | Log page initial filter |

## Diagnostics

| Property | Type | Default | Consumer |
|---|---|---|---|
| `SchemaVersion` | `int` | `2` | `SettingsStore.Load` migration/validation |

| `LastTargetsFilter` | `string?` | `null` | Targets page restores the filter chip (only when `RememberFilters`) |
| `LastTargetsSearch` | `string?` | `null` | Targets page restores the search text (only when `RememberFilters`) |
| `LastWorkspaceSearch` | `string?` | `null` | Workspace page restores the search text (only when `RememberFilters`) |
| `LastSelectedPid` | `int` | `0` | Targets page restores the selection (only when `RememberFilters`) |

## Serialisation notes

- `SettingsPath` is `[JsonIgnore]`, everything else round-trips.
- Enums are written as names (`JsonStringEnumConverter` on the options).
- `SetProperty`-style change notification is not needed on `AppSettings`; the
  view model raises and then calls `SettingsStore.Save()`.
- `SettingsStore.Save()` must fire `Changed` on success **and** log (not throw)
  on failure; `Save()` on an empty path must be a no-op without exceptions.
