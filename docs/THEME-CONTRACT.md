# Theme resource contract

> **Status: implemented in 2.0.0.** The interface redesign replaced the original
> violet/cyan palette with the graphite + obsidian-red identity described in
> [`DESIGN_SYSTEM.md`](../DESIGN_SYSTEM.md). The tokens below exist in
> `Themes/Palette.Dark.xaml` / `Themes/Palette.Light.xaml`, `Theme.xaml` is
> styles-only, every view resolves palette colours dynamically, and
> `Services/ThemeService` switches them live (Settings → Appearance, or
> `--theme dark|light|system` for a single run). Keep this file, the design system
> and both palettes in sync when adding tokens.

This file is the single source of truth for the runtime theme system. It exists so
that the palette (dark/light) and the two UI surfaces (shell + pages) can be
developed in parallel without guessing resource keys.

## Rules

1. `src/OBSIDIAN/Themes/Theme.xaml` holds **styles only**. Every colour it uses
   must be `{DynamicResource <Token>}` — never a literal `#RRGGBB` and never
   `{StaticResource <Token>}` for a palette colour. (Static layout values such as
   radii, paddings, fonts and durations stay as they are.)
2. `src/OBSIDIAN/Themes/Palette.Dark.xaml` and `src/OBSIDIAN/Themes/Palette.Light.xaml`
   define **every token** listed below as a `Color`, a `SolidColorBrush`, a
   `LinearGradientBrush` or a `DropShadowEffect`, as appropriate. Both files must
   define the exact same key set.
3. Views (`ShellWindow.xaml`, `Views/*.xaml`) must not contain literal colours;
   use the tokens below. Code-behind/converters that need a brush resolve
   `Application.Current.TryFindResource("Token")` and listen to
   `ThemeService.ThemeChanged` when the value must be cached (syntax highlighter,
   log/severity converters).
4. Token names are frozen. If a surface genuinely needs a new tone, add the key to
   **both** palettes first, then document it here.
5. OBSIDIAN has no gradient surfaces, glows or glass. `AccentGradient*`,
   `HeroWash`, `VioletGlow`, `CyanGlow` and `AccentGlow` stay defined for
   backwards compatibility, but they are flat or invisible by design.

## Tokens

### Base surfaces

| Token | Dark | Light |
|---|---|---|
| `Bg0` / `Bg0Color` | `#0B0C0E` | `#F4F4F5` |
| `Bg1` / `Bg1Color` | `#0E1013` | `#EDEEF0` |
| `Panel` / `PanelColor` | `#14171B` | `#FFFFFF` |
| `PanelAlt` / `PanelAltColor` | `#171B20` | `#F7F8F9` |
| `Elevated` / `ElevatedColor` | `#1B1F25` | `#FFFFFF` |
| `SurfaceSunken` | `#0A0B0E` | `#ECEDEF` |
| `SurfaceRaised` | `#1A1E24` | `#FFFFFF` |
| `SurfaceRaisedAlt` | `#1F242B` | `#F7F8F9` |
| `SurfaceHeader` | `#121519` | `#F7F8F9` |
| `WindowBar` | `#0B0C0E` | `#FFFFFF` |
| `SidebarSurface` | `#0F1114` | `#F0F1F3` |
| `CommandBarSurface` | `#101215` | `#FAFAFB` |
| `StatusBarSurface` | `#0B0C0E` | `#F0F1F3` |
| `ToastSurface` | `#1A1E24` | `#FFFFFF` |
| `TooltipSurface` | `#22272E` | `#FFFFFF` |
| `CardFill` (solid) | `#14171B` | `#FFFFFF` |
| `CardSheen` (transparent) | `#00000000` | `#00000000` |

### Borders and separators

| Token | Dark | Light |
|---|---|---|
| `Border` | `#262B33` | `#D8DBDF` |
| `BorderSoft` | `#1D2229` | `#E5E7EA` |
| `BorderStrong` | `#39414C` | `#BFC4CB` |
| `BorderHover` | `#333A45` | `#C9CDD3` |
| `Separator` | `#1B2027` | `#E2E4E7` |
| `SeparatorStrong` | `#2C333D` | `#CCD0D5` |
| `GridLine` | `#14181D` | `#EDEEF0` |
| `SelectionBorder` | `#3A2B2B` | `#E3C7C4` |
| `CardHeaderBorder` / `HeroBorder` | `#2A2F37` | `#E2E4E7` |
| `ToastBorder` | `#2E353F` | `#D8DBDF` |

### Text

| Token | Dark | Light |
|---|---|---|
| `Text` | `#E7E9EC` | `#16181C` |
| `TextDim` | `#ADB4BE` | `#3F454E` |
| `Muted` | `#868E99` | `#646B75` |
| `Dim` | `#7A8492` | `#6F7782` |
| `TextOnAccent` | `#FFFFFF` | `#FFFFFF` |
| `WindowTitleText` | `#E7E9EC` | `#16181C` |
| `AccentText` | `#F08A82` | `#A5342C` |
| `CyanText` | `#9CC7D8` | `#2A6A83` |
| `GreenText` | `#8FD6B6` | `#1E7A55` |
| `AmberText` | `#E8C07A` | `#93610F` |
| `RedText` | `#F49A9A` | `#B23A32` |

### Accent family

| Token | Dark | Light |
|---|---|---|
| `Accent` | `#D0453C` | `#B5372E` |
| `AccentHover` | `#DE5A50` | `#C2443B` |
| `AccentPressed` | `#B33A32` | `#932C25` |
| `AccentSoft` | `#F08A82` | `#C24A40` |
| `AccentTint` | `#241414` | `#FBEAE8` |
| `AccentBorder` | `#4A2626` | `#E9C4BF` |
| `AccentBorderStrong` | `#7A3A36` | `#C98B84` |
| `FocusRing` | `#A6403A` | `#B5372E` |

### Semantic colours

| Token | Dark | Light | Use |
|---|---|---|---|
| `Cyan` / `CyanTint` | `#6FA8C4` / `#12222A` | `#2F7C98` / `#E8F3F7` | neutral technical facts, CLR/architecture |
| `Green` / `GreenTint` | `#4FB98A` / `#10241C` | `#1E8A5F` / `#E7F5EE` | success, status online |
| `Amber` / `AmberTint` | `#D9A441` / `#241C0E` | `#A9761A` / `#FBF2E1` | warning, elevated |
| `Red` / `RedTint` | `#E5675F` / `#2A1616` | `#C0392F` / `#FBE9E7` | error, destructive |
| `RowHover` | `#191D23` | `#F3F4F6` | table hover |
| `RowSelected` | `#20252C` | `#EEF0F3` | table / chip selection |
| `NavHover` | `#171B20` | `#E9EBEE` | navigation hover |
| `NavSelected` | `#1B2027` | `#E4E7EB` | navigation selection |
| `HoverStrong` | `#2A3038` | `#E2E5E9` | pressed emphasis |

### Controls

| Token | Dark | Light |
|---|---|---|
| `ControlSurface` | `#181C22` | `#FFFFFF` |
| `ControlSurfaceHover` | `#1F242B` | `#F5F6F8` |
| `ControlSurfacePressed` | `#12161B` | `#ECEEF1` |
| `ControlBorder` | `#2B323B` | `#D4D8DD` |
| `ControlBorderHover` | `#3C4550` | `#BCC2C9` |
| `InputSurface` | `#0F1216` | `#FFFFFF` |
| `InputSurfaceFocus` | `#12161B` | `#FFFFFF` |
| `InputBorderFocus` | `#A6403A` | `#B5372E` |
| `Selection` | `#4A2A28` | `#F3D5D2` |
| `CheckSurface` | `#12161B` | `#FFFFFF` |
| `CheckBorder` | `#343C46` | `#C6CBD2` |
| `CheckBorderHover` | `#5A4444` | `#B98C87` |
| `CheckBorderChecked` | `#D0453C` | `#B5372E` |
| `CheckGlyph` | `#FFFFFF` | `#FFFFFF` |
| `ChipSurface` | `#14181D` | `#FFFFFF` |
| `ChipBorder` | `#262C34` | `#DDE0E4` |
| `ChipBorderHover` | `#3A424D` | `#C6CBD2` |
| `ChipTextChecked` | `#F0A49C` | `#A5342C` |
| `BadgeSurface` | `#191D23` | `#F4F5F6` |
| `BadgeBorder` | `#2A3038` | `#DDE0E4` |
| `BadgeBorderAccent` | `#4A2626` | `#EFD2CE` |
| `BadgeBorderCyan` | `#234450` | `#CFE4EC` |
| `BadgeBorderGreen` | `#1D4234` | `#CDE7DA` |
| `BadgeBorderAmber` | `#4A3A18` | `#F0E0BE` |
| `BadgeBorderRed` | `#4A2424` | `#F0D3D0` |
| `ScrollThumb` | `#2E343D` | `#C3C8CE` |
| `TooltipBorder` | `#39414C` | `#D8DBDF` |
| `TooltipText` | `#E7E9EC` | `#16181C` |

### Decoration (compatibility, flat by design)

| Token | Dark | Light |
|---|---|---|
| `AccentGradient` | `#D0453C → #C23E36` | `#B5372E → #A5342C` |
| `AccentGradientH` | same, horizontal | same, horizontal |
| `HeroWash` | `#14171B` (solid) | `#FFFFFF` (solid) |
| `VioletGlow` / `CyanGlow` | fully transparent | fully transparent |
| `AccentGlow` (effect) | `BlurRadius 0`, `Opacity 0` | same |
| `CardShadow` (effect) | `Blur 16 · Depth 4 · 0.30` | `Blur 16 · Depth 4 · 0.12` |

### Code / log / analysis colours

| Token | Dark | Light |
|---|---|---|
| `CodeBackground` | `#0C0E11` | `#FFFFFF` |
| `CodeText` | `#C9D1DB` | `#24292F` |
| `CodeKeyword` | `#D98E6A` | `#A04E2E` |
| `CodeType` | `#8FBFD9` | `#2A6A83` |
| `CodeString` | `#A3C98F` | `#2E7D4F` |
| `CodeNumber` | `#D9B87A` | `#8A5A12` |
| `CodeComment` | `#6A7380` | `#8A919B` |
| `CodeDirective` | `#C9A227` | `#93610F` |
| `CodeSelection` | `#3A2A28` | `#F3D5D2` |
| `CodeLineNumber` | `#454D58` | `#A7ADB6` |
| `LogTrace` | `#6A7380` | `#8A919B` |
| `LogInfo` | `#9AA6B4` | `#44506A` |
| `LogSuccess` | `#4FB98A` | `#1E8A5F` |
| `LogWarning` | `#D9A441` | `#93610F` |
| `LogError` | `#E5675F` | `#C0392F` |
| `SeverityCritical` | `#E5675F` | `#C0392F` |
| `SeveritySuspicious` | `#D9A441` | `#93610F` |
| `SeverityInfo` | `#6FA8C4` | `#2A6A83` |
| `StatusOnline` | `#4FB98A` | `#1E8A5F` |
| `StatusOffline` | `#6A7380` | `#8A919B` |

## Runtime switching contract

`Obsidian.Services.ThemeService` (static):

```csharp
public enum AppTheme { System, Dark, Light }
public enum AccentPreset { Violet, Cyan, Green, Amber, Red }

public static AppTheme Mode { get; }
public static bool IsDark { get; }              // effective, after System resolution
public static AccentPreset Accent { get; }
public static event EventHandler? ThemeChanged;

public static void Initialise(AppTheme mode, AccentPreset accent);   // called from App.OnStartup before the main window
public static void Apply(AppTheme mode, AccentPreset accent);        // swaps Application.Current.Resources.MergedDictionaries[0]
```

### Accent presets

`AccentPreset.Red` (display name **Ember**) is the default and the OBSIDIAN
identity. Presets override `Accent`, `AccentHover`, `AccentPressed`, `AccentSoft`,
`AccentTint`, `AccentBorder`, `AccentBorderStrong`, `AccentText`,
`AccentGradient`, `AccentGradientH` and `AccentGlow` on top of the dark/light
palette. Surfaces are never re-tinted, so every preset keeps the same contrast
ladder.

| Preset | Display name | Dark accent | Light accent |
|---|---|---|---|
| `Red` | Ember (default) | `#D0453C` | `#B5372E` |
| `Cyan` | Steel | `#47758C` | `#2A6A83` |
| `Green` | Jade | `#2F7D5C` | `#1B7A55` |
| `Amber` | Amber | `#B5822A` | `#96680F` |
| `Violet` | Iris | `#7E5FBC` | `#6E4FB0` |

`ThemeService` also overrides `FocusRing`, `InputBorderFocus`,
`CheckBorderChecked`, `ChipTextChecked`, `Selection`, `CodeSelection` and
`TextOnAccent` so every accent-driven surface follows the preset. `TextOnAccent`
is either white or `#12161B`, chosen from the accent luminance (>= 4.5:1).

### System mode

`System` resolves
`HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme`
(0 → dark, 1 → light) and re-applies on `SystemEvents.UserPreferenceChanged`.
`ThemeChanged` fires after the dictionary swap, on the UI thread, so cached
brushes (syntax highlighter, log/severity converters, toast accents) refresh.

### Layout tokens

`Services/UiDensity.cs` overrides the layout tokens consumed via
`{DynamicResource}`: `PageMargin`, `CardPadding`, `CardCornerRadius`,
`CardTightPadding`, `CardTightCornerRadius`, `TableRowContentMargin`,
`TableRowMargin`, `NavItemPadding`, `CommandBarPadding` and
`SettingsSectionSpacing`. Defaults are declared in `Themes/Theme.xaml`; the
comfortable/compact values are specified in `DESIGN_SYSTEM.md` §3.
