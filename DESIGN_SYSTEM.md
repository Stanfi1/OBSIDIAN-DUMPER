# OBSIDIAN — Design System

> Version 2.0 · The visual contract for **OBSIDIAN .NET Dumper**.
> Everything in `src/OBSIDIAN` implements this document. If a screen needs a new
> tone, spacing step or component shape, it is added here first, then to both
> palettes, then used.

---

## 0. Principles

OBSIDIAN is a precision instrument for .NET developers: it lists processes,
attaches to a CLR host, dumps managed modules, and explains what it found
(metadata, C#, IL, strings, protector fingerprints). The interface must read like
a professional diagnostic tool — Rider, ILSpy, WinDbg, Process Explorer — and not
like a web dashboard.

1. **Data first.** The table/row is the hero. Chrome exists to frame it, never to
   compete with it.
2. **Density with air.** High information density is a feature; it is achieved
   with tight, consistent rhythm and strong alignment, not with whitespace
   removal. Nothing touches, nothing floats.
3. **One accent, used sparingly.** Obsidian red marks identity, the primary
   action and the active state. Nothing else. A red surface is never decoration.
4. **Flat by default.** Depth comes from surface steps and 1px hairlines. There
   are no glows, no glass, no gradients behind content, no ambient blobs.
5. **Sharp geometry.** 2–4px radii. Room-scale rounding is a consumer-web
   signal; developer tools are crisp.
6. **Technical text is monospaced.** Anything the user may compare or copy —
   addresses, hashes, versions, paths, IL, timestamps — is set in Cascadia Mono
   with tabular figures.
7. **Motion is feedback, not decoration.** 120–160 ms, ease-out, opacity and a
   short translate. No animation on high-frequency interactions.
8. **Every pixel is justified.** No icon exists to fill space, no card exists
   without a boundary reason, no divider without a group to separate.

Anti-goals (explicitly forbidden): gradient page headers, purple/blue palettes,
glassmorphism, drop-shadow stacks on every card, oversized marketing headings,
rounded 16px cards inside 12px cards, random MDL2 glyphs, animated counters,
empty hero areas.

---

## 1. Colour palette

Two runtime palettes are shipped (`Themes/Palette.Dark.xaml`,
`Themes/Palette.Light.xaml`). Both define the **same key set**; the style sheet
only ever reads tokens through `{DynamicResource}` so the palette can be swapped
at runtime.

### 1.1 Dark — "Graphite" (default)

| Role | Token | Value |
|---|---|---|
| App background | `Bg0` | `#0B0C0E` |
| Page canvas | `Bg1` | `#0E1013` |
| Panel / card | `Panel` | `#14171B` |
| Panel alternate | `PanelAlt` | `#171B20` |
| Elevated (menu, dialog) | `Elevated` | `#1B1F25` |
| Inset well (code, console, inputs) | `SurfaceSunken` | `#0A0B0E` |
| Raised surface | `SurfaceRaised` | `#1A1E24` |
| Table header surface | `SurfaceHeader` | `#121519` |
| Sidebar | `SidebarSurface` | `#0F1114` |
| Command bar | `CommandBarSurface` | `#101215` |
| Status bar | `StatusBarSurface` | `#0B0C0E` |
| Title bar | `WindowBar` | `#0B0C0E` |
| Border | `Border` | `#262B33` |
| Border subtle | `BorderSoft` | `#1D2229` |
| Border strong | `BorderStrong` | `#39414C` |
| Divider | `Separator` | `#1B2027` |
| Primary text | `Text` | `#E7E9EC` |
| Secondary text | `TextDim` | `#ADB4BE` |
| Muted text | `Muted` | `#868E99` |
| Faint text | `Dim` | `#7A8492` |
| **Obsidian accent** | `Accent` | `#D0453C` |
| Accent hover | `AccentHover` | `#DE5A50` |
| Accent pressed | `AccentPressed` | `#B33A32` |
| Accent text on dark | `AccentText` | `#F08A82` |
| Accent tint surface | `AccentTint` | `#241414` |
| Accent border | `AccentBorder` | `#4A2626` |
| Accent border strong | `AccentBorderStrong` | `#7A3A36` |
| Success | `Green` / `GreenText` | `#4FB98A` / `#8FD6B6` |
| Warning | `Amber` / `AmberText` | `#D9A441` / `#E8C07A` |
| Error / destructive | `Red` / `RedText` | `#E5675F` / `#F49A9A` |
| Informational | `Cyan` / `CyanText` | `#6FA8C4` / `#9CC7D8` |
| Row hover | `RowHover` | `#191D23` |
| Row selected | `RowSelected` | `#20252C` |
| Focus ring | `FocusRing` | `#A6403A` |
| Scroll thumb | `ScrollThumb` | `#2E343D` |

### 1.2 Light — "Paper"

Same roles, inverted: `Bg0 #F4F4F5`, `Bg1 #EDEEF0`, `Panel #FFFFFF`,
`PanelAlt #F7F8F9`, `Border #D8DBDF`, `Text #16181C`, `TextDim #3F454E`,
`Muted #646B75`, `Dim #8A919B`, `Accent #B5372E`, `AccentText #A5342C`,
`AccentTint #FBEAE8`, `RowHover #F3F4F6`, `RowSelected #EEF0F3`.

The light theme is a working theme, not an inverted screenshot: borders get
slightly darker relative to surfaces, selection keeps a red tint, and code
colours switch to a readable print-like set.

### 1.3 Accent presets

Five user-selectable presets override `Accent`, `AccentHover`, `AccentPressed`,
`AccentSoft`, `AccentTint`, `AccentText`, `AccentBorder`, `AccentBorderStrong`,
`AccentGradient`, `AccentGradientH`:

| Preset | Dark accent | Light accent | Note |
|---|---|---|---|
| **Ember** (default, `AccentPreset.Red`) | `#D0453C` | `#B5372E` | OBSIDIAN identity |
| Steel (`Cyan`) | `#47758C` | `#2A6A83` | cool, low-saturation |
| Jade (`Green`) | `#2F7D5C` | `#1B7A55` | |
| Amber (`Amber`) | `#B5822A` | `#96680F` | uses graphite text on its fill |
| Iris (`Violet`) | `#7E5FBC` | `#6E4FB0` | legacy choice, desaturated |

Accent presets only re-tint the *accent family*. Surfaces never change, so every
preset keeps the same contrast ladder. Every preset is tuned so the accent
supports at least 4.5:1 against white or graphite text; `TextOnAccent` is chosen
at runtime from the accent luminance.

### 1.4 Allocation rules

- Red is used for: brand mark, primary button fill, active nav marker, selected
  tab underline, focus ring, destructive confirmation emphasis, error text.
- Red is **not** used for: status "success", data graphics, table stripes,
  informative badges, backgrounds larger than a chip.
- Green / amber / red are strictly semantic (success / warning / error).
- Steel (`Cyan*`) is the informational tone for neutral technical facts (CLR
  runtime, architecture, module kind).

---

## 2. Typography

| Token | Family | Size | Weight | Use |
|---|---|---|---|---|
| `DisplayFont` | Segoe UI Variable Display | — | — | page titles, product wordmark |
| `TextFont` | Segoe UI Variable Text | 12.5 | 400 | all UI text (window default) |
| `MonoFont` | Cascadia Mono | — | — | paths, hashes, addresses, IL, ids, timers |

Type scale (WPF sizes in DIP):

| Step | Size | Weight | Style key | Use |
|---|---|---|---|---|
| Display | 18 | SemiBold | `H1` | page title in the command bar |
| Title | 15 | SemiBold | `H2` | panel titles, assembly name |
| Subtitle | 13 | SemiBold | `H3` | group titles, section headers |
| Body | 12.5 | Regular | default `TextBlock` | table cells, descriptions |
| Body strong | 12.5 | SemiBold | — | selected row, primary labels |
| Caption | 11.5 | Regular | `Caption` | supporting sentence |
| Label | 10 | SemiBold | `LabelCaps` | uppercase section labels |
| Table header | 9.5 | SemiBold | `TableHeaderText` | column headers (uppercase) |
| Micro | 9.5 | Regular | `DimText` | hints, badges |
| Mono body | 11 | Regular | `MonoText` | technical values |
| Mono micro | 9.5–10 | Regular | inline | addresses, sizes, timestamps |

Rules:

- Section labels are uppercase, `Dim`, 10px, and carry 0.4px of visual tracking
  (approximated by a small left inset, never by changing the font).
- Numeric columns use `MonoFont` so digits align (tabular behaviour). Never use
  proportional digits for counts, sizes, addresses or durations.
- Monospace text is one step smaller than the body text it sits next to; this
  keeps its optical weight equal.
- No letter-spacing hacks, no italic UI text (italics only in code comments).
- Headings never exceed 18px inside the app. This is a tool, not a landing page.

---

## 3. Spacing system

A 4px base grid; only these steps exist:

| Token | Value | Use |
|---|---|---|
| `space-0` | 0 | flush |
| `space-1` | 2 | badge inner, hairline offset |
| `space-2` | 4 | icon↔label, tight pairs |
| `space-3` | 6 | chip gaps, row inner |
| `space-4` | 8 | control padding, field gap |
| `space-5` | 12 | panel padding, column gap |
| `space-6` | 16 | page padding (comfortable) |
| `space-7` | 20 | card padding |
| `space-8` | 24 | section separation |
| `space-9` | 32 | empty-state blocks |

Density tokens (overridden by `Services/UiDensity.cs`):

| Token | Comfortable | Compact |
|---|---|---|
| `PageMargin` | `16,12,16,12` | `12,8,12,8` |
| `CardPadding` | `16` | `12` |
| `CardTightPadding` | `12` | `10` |
| `TableRowContentMargin` | `8,0,10,0` | `6,0,8,0` |
| `TableRowMargin` | `0,0,0,1` | `0` |
| `CommandBarPadding` | `16,10,16,10` | `14,8,14,8` |
| `NavItemPadding` | `8,6,8,6` | `8,5,8,5` |
| `SettingsSectionSpacing` | `0,12,0,0` | `0,8,0,0` |

---

## 4. Component metrics

| Component | Height | Padding | Radius | Border |
|---|---|---|---|---|
| Title bar | 40 | `12,0` | 0 | bottom hairline |
| Command bar | 56 | `16,10` | 0 | bottom hairline |
| Status bar | 26 | `16,0` | 0 | top hairline |
| Sidebar | 224 wide | `10,10` | 0 | right hairline |
| Primary button | 30 | `12,7` | 3 | none |
| Secondary button | 30 | `12,7` | 3 | 1px |
| Ghost button | 28 | `8,5` | 3 | none |
| Icon button | 28×28 | — | 3 | none |
| Window caption button | 44×30 | — | 0 | none |
| Text input | 30 | `9,6` | 3 | 1px |
| Combo box | 30 | `9,6` | 3 | 1px |
| Check box | 14×14 box | `0,0,0,8` | 2 | 1px |
| Filter chip | 26 | `8,4` | 2 | 1px |
| Badge | 18 | `5,1` | 2 | 1px |
| Table header | 28 | `8,0` | 0 | bottom hairline |
| Table row | 30 (comfortable) / 26 (compact) | `8,0,10,0` | 2 | none |
| Panel | auto | 16 | 4 | 1px |
| Inset well | auto | 12 | 3 | 1px |
| Toast | auto | `12,10` | 4 | 1px + left bar |
| Tooltip | auto | `8,5` | 3 | 1px |

Concentric rule: a control inside a 4px-radius panel uses 2–3px radius; an input
inside a 3px inset uses 2px. Outer radius = inner radius + padding.

---

## 5. Border radius

| Token | Value | Applies to |
|---|---|---|
| `RadiusXs` | 2 | badges, chips, table rows, check boxes |
| `RadiusSm` | 3 | buttons, inputs, selects, wells, tooltips |
| `RadiusMd` | 4 | panels, cards, toasts |
| `RadiusLg` | 6 | dialogs only |
| `RadiusPill` | 999 | status pills (rare, target chip) |

No radius above 6px anywhere in the product.

---

## 6. Elevation & shadow

Borders carry structure; shadows carry only true layering (popups, toasts,
dialogs).

| Token | Value | Use |
|---|---|---|
| `CardShadow` | `Blur 16 · Depth 4 · Opacity 0.30` (dark) / `0.12` (light) | toasts, popups |
| `AccentGlow` | disabled (`Opacity 0`) | kept for token compatibility, never visible |

Never stack a shadow on a panel that already sits inside a bordered region.
No inner shadows. No coloured shadows.

---

## 7. Icon system

- Single family: **Segoe MDL2 Assets** (`IconFont`), 12–16px, one stroke weight.
- Icons are monochrome and inherit `Foreground`; state changes are colour and
  opacity changes, never a second glyph.
- Icon buttons carry a `ToolTip`; icon-only controls are 28×28 with a full-row
  hit area where possible.
- Glyphs are chosen for meaning only: `E8FD` process grid, `E8B7` folder /
  workspace, `E721` search / inspector, `E896` injection, `E7C3` log, `E713`
  settings, `E72C` refresh, `E74E` dump, `E8E5` open file, `E838` browse, `E8BB`
  close, `E921/E922/E923` caption buttons, `E73E` success, `E7BA` warning,
  `E783` error, `E946` information.
- No decorative icons in panels; an icon appears only on an action or a state.

### 7.1 Brand mark

The mark is the product's only graphic device: a **graphite hexagon with an ember
core** — obsidian with light trapped inside. `Assets/obsidian-mark.png` (512px
master) and `Assets/obsidian.ico` (16–256px) are generated from the same
geometry and used at 16px in the title bar and 32px in Settings → About.

Rules:

- The core diamond never drops below 35% of the mark width, otherwise the mark
  reads as a plain dark shape below 24px.
- The hexagon keeps a 1px lighter rim (`#606A77`) so it stays visible against
  `#0B0C0E`; the body is graphite (`#3A434E` → `#161A20`), never pure black.
- The core is the accent red family (`#E05246` → `#8E2822`); it is the only place
  where a red gradient is allowed.
- No text, no letters, no additional shapes; the mark is never recoloured by the
  accent preset.

---

## 8. Buttons

| Style | Background | Text | Border | Hover | Pressed | Disabled |
|---|---|---|---|---|---|---|
| `PrimaryButton` | `Accent` | `TextOnAccent` | none | `AccentHover` | `AccentPressed` | 40% opacity |
| `SecondaryButton` | `ControlSurface` | `Text` | `ControlBorder` | `ControlSurfaceHover` + `ControlBorderHover` | `ControlSurfacePressed` | 40% |
| `GhostButton` | transparent | `TextDim` | none | `ControlSurface` + `Text` | `ControlSurfacePressed` | 35% |
| `IconButton` | transparent | `Muted` | none | `ControlSurface` + `Text` | `ControlSurfacePressed` | 30% |
| `LinkButton` | transparent | `AccentSoft` | none | underline-free, `AccentText` | `AccentPressed` | 35% |
| `DangerButton` | transparent | `Red` | `RedTint`→hover | `RedTint` | `RedTint` darker | 35% |

- Buttons never scale, never glow, never animate colour for longer than 120 ms.
- Primary buttons are used at most once per visible region.
- `TextOnAccent` is resolved per accent (white or graphite `#12161B`) so the
  label keeps at least 4.5:1 contrast on every preset.

### 8.1 Menus and popups

- `ContextMenu` uses the `Elevated` surface, a 1px `Border`, 3px radius, 3px
  inner padding, 208px minimum width and the system drop shadow.
- `MenuItem`: 11.5px text, `9,6` padding; hover/active is `AccentTint` with
  `Text`; disabled is `Dim` at 60 %; separators are 1px `Separator` lines.
- Menus flip automatically at screen edges and never show a white system
  surface in either theme.

---

## 9. Inputs

- 1px `ControlBorder`, `InputSurface` fill, 3px radius, 30px height.
- Focus: border `FocusRing` + 1px outer `AccentTint` ring (implemented with a
  1px border change only — no animated halo).
- Placeholder text is `Dim` at 11px and is a real overlay `TextBlock` bound to
  the empty state (existing pattern in the views).
- Leading search glyph is `Dim` at 11px, 8px from the left edge.
- Invalid/error inputs use `Red` border and a 10.5px `RedText` message below.
- Password/secret fields are not used in this product.

### 9.1 Tooltips

- Tooltips are **theme-consistent**: light surface on the light theme, dark
  surface on the dark theme, 1px border, 3px radius, `8,5` padding, system
  shadow.
- Content wraps at 420px so long module paths and command lines stay readable.
- Text inherits the tooltip foreground. There is no global `TextBlock`
  foreground override that could leak the opposite theme's text colour into a
  menu, tooltip or button.

## 10. Navigation

- Sidebar width 224, one column, 6 items in fixed order:
  Targets · Workspace · Inspector · Injection · Activity · Settings.
- Item: 34px tall, 8px horizontal padding, icon 16px in a 20px column, label
  12.5px, optional count badge (`Badge`, mono 9.5).
- Hover: `NavHover` background. Selected: `AccentTint` background + 2px accent
  bar on the leading edge + `Text` label + `AccentText` icon; the count badge
  uses `BadgeAccent`.
- No rounded pills, no icon-only mode, no collapsible groups.
- Keyboard: Ctrl+1…7 map to the order above; the list is focusable.

## 11. Tables

- Header: 28px, uppercase 9.5px SemiBold `Dim`, bottom `Separator` hairline,
  stationary above a virtualised `ListBox`.
- Row: 30px (comfortable) / 26px (compact), 1px bottom `GridLine`, hover
  `RowHover`, selected `RowSelected` + 2px accent bar on the leading edge.
- Cells: primary text left aligned; numeric/technical cells right aligned in
  mono; secondary line under the primary label is 9.5px `Dim` and clipped, never
  wrapped.
- Column gaps come from fixed column widths in the row template and the matching
  header template — they must be edited together.
- Empty state lives inside the table body, centred, with one icon, one sentence,
  one optional action.

## 12. Log viewer

- Monospace grid: `time(72) · level(46) · scope(96) · message(*)`.
- Row 22px, no borders between rows (the log reads as a block), hover
  `RowHover`, 2px level stripe on the leading edge for `Warning`/`Error` only.
- Level colours: trace `LogTrace`, info `LogInfo`, success `LogSuccess`, warning
  `LogWarning`, error `LogError`.
- Toolbar: level chips, search field, Follow/Timestamps toggles, count, Copy,
  Export, Clear.
- Empty state: centred glyph + title + hint. Unread jump pill sits above the last
  row, not over it.

## 13. Code / IL viewer

- Background `CodeBackground`, gutter `CodeLineNumber`, 12.4px Cascadia Mono,
  1.55 line-height.
- Token colours: keyword, type, string, number, comment, directive (see §1).
- Search hits use `CodeSelection` background with `CodeText` foreground.
- The gutter is right-aligned and separated by a `Separator` hairline.

## 14. States

| State | Treatment |
|---|---|
| Hover | surface step +1, text step +1; 120 ms |
| Focus | 1px `FocusRing` border; visible on keyboard focus only (`IsKeyboardFocused`) |
| Active/selected | `RowSelected` / `NavSelected` + 2px accent bar; filter chips and segmented choices use `HoverStrong` + `BorderStrong` + `Text` (never accent fill) |
| Pressed | surface step −1, no motion |
| Disabled | 40% opacity on the whole control, cursor default, tooltip explains why when relevant |
| Busy | inline progress bar (3px) + status text; the action button keeps its label and disables |
| Empty | centred glyph (`Dim`), 12.5px title, 11px hint, optional single action |
| Error | `RedTint` surface, `Red` 3px leading bar, `RedText` message, monospace details |
| Success | `GreenTint` surface, `Green` leading bar, `GreenText` message |

## 15. Motion

| Token | Duration | Curve | Use |
|---|---|---|---|
| Instant | 0–90 ms | — | hover, focus, press |
| Fast | 120 ms | `CubicEase EaseOut` | chip/toggle state |
| Page | 140 ms opacity / 160 ms 6px translate | `CubicEase EaseOut` | page switch (respects the Animations setting) |
| Toast | 180 ms fade / 200 ms 4px slide | `CubicEase EaseOut` | toast entrance |

No looping motion except the indeterminate progress bar. No animation of layout
properties. `Animations = false` must render the final state immediately.

## 16. Responsiveness & scaling

- Minimum window: 1180×740. The sidebar stays 224; detail panels shrink fluidly
  (Targets 280–360px, Workspace 280–340px, ratio 3:1 / 2.6:1 against the table);
  the flexible name column truncates with a tooltip, fixed technical columns
  keep their minimum widths, and long runtime labels truncate in the title chip
  and summary strip.
- **Two-row toolbars.** Toolbars whose controls do not fit one line are laid out
  as two explicit rows (search/filters first, secondary options and actions
  second) instead of relying on ad-hoc wrapping; each row keeps its right-hand
  action group aligned.
- Caption buttons keep their hit area and accessible names at every size, and
  the window fills the work area edge-to-edge when maximised.
- Pages use `*` grids; fixed widths are reserved for the details column (340),
  the type list (280), the methods list (240) and monospace columns.
- All layout is DPI-independent: no pixel fonts, no `SnapsToDevicePixels`
  hacks beyond `UseLayoutRounding` + `SnapsToDevicePixels` on windows.
- Text never truncates without a tooltip carrying the full value.

## 17. Data visualisation

- Entropy bars: 3px track `SurfaceSunken`, fill `Accent` at ≤40%, `Amber` at
  ≤70%, `Red` above; value in mono to the right.
- Metrics strips: number 16px SemiBold mono-ish, label 9.5px uppercase `Dim`.
  No sparkline decoration, no donut charts.
- Progress: 3px `Accent` fill on `SurfaceSunken` track, indeterminate animation
  only while a scan runs.

## 18. Copy & tone

- Sentence case for labels; uppercase only for section labels, table headers,
  badges and tab headers.
- Verbs first in actions: "Dump selected", "Attach & map modules", "Copy path".
- Errors state what happened, what it affects, and what to do next; they never
  blame the user and never expose a raw HRESULT without a readable prefix.
- Paths and technical identifiers are shown verbatim, never wrapped mid-token.

## 19. Accessibility

- Text contrast ≥ 4.5:1 against its own surface (body) and ≥ 3:1 for 10px+ labels
  (`Dim` on `Panel` is 4.6:1, `Muted` on `Panel` is 5.9:1).
- Accent on `TextOnAccent` is 5.1:1 (white on `#D0453C`).
- Focus is always visible and never colour-only: border + 1px ring.
- State is never communicated by colour alone: status pills carry text, errors
  carry an icon.
- Every icon-only button has an accessible name (`ToolTip` +
  `AutomationProperties.Name` where the glyph is the only content).
- Motion honours `Animations = false`.

## 20. Implementation rules

1. Palette files define colours only; `Theme.xaml` defines styles only.
2. Views contain no literal colours and no inline `FontSize`/`Foreground` where a
   style exists.
3. Resource keys are stable; new keys are added to both palettes in the same
   change.
4. Views bind to existing view-model members; the redesign never renames a
   binding path.
5. Reuse before duplication: `Card`, `CardTight`, `CardBare`, `FieldLabel`,
   `FieldValue`, `Badge*`, `PrimaryButton`, `SecondaryButton`, `GhostButton`,
   `IconButton`, `SearchBox`, `FilterChip`, `TableList`, `TableRow`,
   `ObsidianTabControl`, `ObsidianTab`, `ObsidianProgress`, `ContextMenu`,
   `MenuItem`, `Separator`, `ToolTip`.
6. Layout math is explicit: fixed column widths appear in the header and in the
   row template and must stay in sync.

## 21. Memory & binary inspection

- The Memory page uses the same `ObsidianTabControl` and table language as the
  rest of the app: MAP, SCAN and RUNTIME are peer views, not nested cards.
- MAP toolbar: search and filter chips occupy the first row, the summary the
  second, actions on the right. Committed regions are the default; reserved
  ranges require the `Reserved` chip.
- Region table: `base(110) / size(76) / protect(64) / type(64) / module(*) /
  flags(96)`; base, size, protection and flags are mono, flags turn `RedText`
  for suspicious (RWX/private-exec) rows.
- SCAN: encoding selector, pattern field, case/executable options, primary
  `Scan`, secondary `Cancel`, live progress and a status line. Hits show address
  (accent mono), module, context and protection; double-click opens hex.
- RUNTIME: a single six-cell metric strip (runtime, threads, modules, objects,
  heap, walkability), then the type table `type(*) / objects(90) / size(90) /
  share(90)`. Errors use the red inset; warnings stay amber and quiet.
- Inspector PE: two cards (headers, sections) plus a full-width data-directory
  card; resources use a 300px list + fluid preview; HEX owns the whole tab.
- Hex grid: offset(82) / 16 bytes in mono / ASCII in muted mono, 20px rows,
  header row names the bytes. Search accepts text or hex with `??` wildcards;
  go-to accepts hex (`0x1A20`).
- CARVE uses a single primary action, a framework toggle, a progress/status
  line and a five-column result table (`assembly / address / size / method /
  file`); the empty state explains that it walks memory rather than modules.
- MANAGED ASSEMBLY is a separate card below native injection: path + browse,
  type/method pair, argument, one primary action, and an explanatory hint. Use
  the amber note for host/architecture limitations; keep managed results in the
  same result card and history as native injections.
- Tab bodies must inherit `Text`; the content presenter sets it explicitly so a
  selected tab's accent label never tints page copy. The template names the
  content host `PART_SelectedContentHost` so the content stays in the
  accessibility tree.
