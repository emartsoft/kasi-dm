# ADR 0031: The UI design system

- **Status:** Proposed (UIR-0 of M2.14; UIR-1 to UIR-7 use it)
- **Requirements:** KASI-UI-001, KASI-UI-006, KASI-UI-010 | **Plan:** [`docs/plans/ui-redesign.md`](../plans/ui-redesign.md)
- **Design source:** [`docs/wireframes/app_ui_redesign/`](../wireframes/app_ui_redesign/)

## Context

The owner supplied a redesign of the main window, the per-download window and Settings. Every screen needs the same surfaces, colours, type, icons and small controls, or the redesign becomes seven restyles that drift apart. This ADR fixes them once.

## Decision

1. **Tokens are theme resources** in `src/Kasi.App/Styles/Tokens.axaml`: a `Dark` and a `Light` dictionary with the same keys. Colours are named by role (`KasiBaseColor`, `KasiMicaColor`, `KasiCardColor`, `KasiBorderColor`, `KasiAccentColor`, `KasiTextPrimaryColor`, `KasiSuccessColor` and so on) and each has a brush of the same name. **The brushes live inside each theme dictionary**, because a brush defined outside takes its colour from the application's theme and not from the window's (found while testing a window in the other theme). Dark is the design's palette (base `#0f0f13`, mica `#16161c`, card `#1e1e26`, border `#2c2d3a`, accent `#6366f1`); light is derived from the same roles with a deeper accent (`#4f46e5`) so that text and the accent stay readable on white.
2. **One accent, the design's indigo,** in both themes, whatever accent Windows has: `FluentAvaloniaTheme` is given `CustomAccentColor` and `PreferUserAccentColor="False"`, and the stock accent button and switch are given the design's fill and a white knob. Light and dark still follow the Windows setting (KASI-UI-006).
3. **Type and icons are the Windows ones:** `KasiTextFont` is Segoe UI Variable then Segoe UI, `KasiMonoFont` is Consolas then Cascadia Mono, `KasiIconFont` is Segoe Fluent Icons then Segoe MDL2 Assets (Windows 10; the same code points). No font or icon file is bundled and no package is added.
4. **Shape:** card radius 8, control radius 6, chip radius 4, and a spacing scale of 4, 8, 12, 16 and 24.
5. **Styles** (`Styles/Controls.axaml`): `Border.kasi-card`; the text roles `kasi-title`, `kasi-secondary`, `kasi-mono`; `kasi-icon` for a glyph.
6. **Controls** (`Controls/`): `StatusChip` (a coloured pill: neutral, accent, info, success, warning or danger), `FileTypeChip` (label and colour from the file name: ISO amber, MSI blue, EXE green, archives purple, video red), `ToggleRow` (a setting as a card with a switch that carries the title as its automation name), `Sparkline` (one drawn control for the speed charts, with `SparklineGeometry` as its testable maths), and `Icons` (the code points in use).
7. **Checked by tests:** every token exists in both themes; text, secondary text, the accent as text and each state colour reach a contrast ratio of 4.5 to 1 on a card in both themes (the accent as a fill, 3 to 1); a window in the other theme gets that theme's colours; the chips, the toggle row and the geometry behave as described above.

## Consequences

- Nothing in the app uses these yet except through the new classes: UIR-1 to UIR-7 move each window onto them.
- A colour changes in one place and in both themes. A state colour that fails the contrast test cannot be merged.
- TRD section 12 is amended in this change: the design of record for the main window, the per-download window and Settings is now the redesign.
