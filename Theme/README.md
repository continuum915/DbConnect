# Win7AeroTheme

`Win7AeroTheme` is a Windows 7 Aero-inspired theme package for Avalonia. It focuses on glossy desktop-era controls, pale Explorer-style surfaces, blue hover/focus states, and a green progress fill while keeping native OS window chrome.

## Usage

Reference the theme package and include it after `FluentTheme`:

```xml
<Application.Styles>
  <FluentTheme />
  <StyleInclude Source="avares://Win7AeroTheme/Themes/Win7AeroTheme.axaml" />
</Application.Styles>
```

The v1 theme intentionally does not copy Microsoft icons, images, sounds, `.msstyles`, or custom Aero Glass title bars.

## Projects

- `Win7AeroTheme`: reusable theme resources and control styles.
- `Win7AeroTheme.Gallery`: sample desktop app that previews themed controls.
- `Win7AeroTheme.Tests`: theme loading and resource contract tests.

## Status in DbConnect

DbConnect no longer uses this theme. Since the UI redesign the app runs on `DbConnectTheme`, a flat, dense look. `Win7AeroTheme` is kept, building and tested, as a reference for a possible later Aero-style pass over the new layout; do not change it as part of DbConnect work.

# DbConnectTheme

`DbConnectTheme` is DbConnect's theme: the design palette (`DbConnect.Color.*` / `DbConnect.Brush.*` in `Light` and `Dark` dictionaries), metrics and type tokens (`DbConnect.Space/Thickness/Size/Icon/FontSize/FontFamily`), flat control styles, and the embedded Inter and JetBrains Mono fonts (SIL OFL 1.1; licence texts in `DbConnectTheme/Fonts/`).

```xml
<Application.Styles>
  <FluentTheme />
  <StyleInclude Source="avares://DbConnectTheme/Themes/DbConnectTheme.axaml" />
</Application.Styles>
```
