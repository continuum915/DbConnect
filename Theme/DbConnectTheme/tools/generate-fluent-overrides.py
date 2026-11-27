"""Writes Themes/Resources/FluentOverrides.axaml: Fluent's own resource keys pointed at the DbConnect palette roles.

Run from Theme/DbConnectTheme after changing the maps below: python tools/generate-fluent-overrides.py
Brush keys reference DbConnect.Color.* dynamically; Color-typed keys are read from Colors.axaml, so the palette stays the
only place a hex value is written.
"""
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
RES = ROOT / "Themes" / "Resources"

# Brush keys: Fluent key -> palette role ("Transparent" for none).
BRUSHES = {
    # Buttons: flat face, 1 px button border, hover in the accent tint.
    "ButtonBackground": "Button", "ButtonBackgroundPointerOver": "HoverBg", "ButtonBackgroundPressed": "PressedBg",
    "ButtonBackgroundDisabled": "Disabled",
    "ButtonBorderBrush": "ButtonBorder", "ButtonBorderBrushPointerOver": "ButtonHoverBorder",
    "ButtonBorderBrushPressed": "ButtonBorder", "ButtonBorderBrushDisabled": "BorderSoft",
    "ButtonForeground": "Text", "ButtonForegroundPointerOver": "Text", "ButtonForegroundPressed": "Text",
    "ButtonForegroundDisabled": "TextNull",
    "RepeatButtonBackground": "Button", "RepeatButtonBackgroundPointerOver": "HoverBg", "RepeatButtonBackgroundPressed": "PressedBg",
    "RepeatButtonBorderBrush": "ButtonBorder", "RepeatButtonBorderBrushPointerOver": "ButtonHoverBorder",
    "RepeatButtonForeground": "Text", "RepeatButtonForegroundPointerOver": "Text", "RepeatButtonForegroundPressed": "Text",
    # Toggle buttons: checked is the light accent tint, not the filled accent.
    "ToggleButtonBackground": "Button", "ToggleButtonBackgroundPointerOver": "HoverBg", "ToggleButtonBackgroundPressed": "PressedBg",
    "ToggleButtonBackgroundDisabled": "Disabled",
    "ToggleButtonBackgroundChecked": "CheckedBg", "ToggleButtonBackgroundCheckedPointerOver": "CheckedBg",
    "ToggleButtonBackgroundCheckedPressed": "PressedBg", "ToggleButtonBackgroundCheckedDisabled": "Disabled",
    "ToggleButtonBackgroundIndeterminate": "CheckedBg", "ToggleButtonBackgroundIndeterminatePointerOver": "CheckedBg",
    "ToggleButtonBackgroundIndeterminatePressed": "PressedBg", "ToggleButtonBackgroundIndeterminateDisabled": "Disabled",
    "ToggleButtonBorderBrush": "ButtonBorder", "ToggleButtonBorderBrushPointerOver": "ButtonHoverBorder",
    "ToggleButtonBorderBrushPressed": "ButtonBorder", "ToggleButtonBorderBrushDisabled": "BorderSoft",
    "ToggleButtonBorderBrushChecked": "CheckedBorder", "ToggleButtonBorderBrushCheckedPointerOver": "CheckedBorder",
    "ToggleButtonBorderBrushCheckedPressed": "CheckedBorder", "ToggleButtonBorderBrushCheckedDisabled": "BorderSoft",
    "ToggleButtonBorderBrushIndeterminate": "CheckedBorder", "ToggleButtonBorderBrushIndeterminatePointerOver": "CheckedBorder",
    "ToggleButtonBorderBrushIndeterminatePressed": "CheckedBorder", "ToggleButtonBorderBrushIndeterminateDisabled": "BorderSoft",
    "ToggleButtonForeground": "Text", "ToggleButtonForegroundPointerOver": "Text", "ToggleButtonForegroundPressed": "Text",
    "ToggleButtonForegroundDisabled": "TextNull",
    "ToggleButtonForegroundChecked": "Text", "ToggleButtonForegroundCheckedPointerOver": "Text",
    "ToggleButtonForegroundCheckedPressed": "Text", "ToggleButtonForegroundCheckedDisabled": "TextNull",
    "ToggleButtonForegroundIndeterminate": "Text", "ToggleButtonForegroundIndeterminatePointerOver": "Text",
    "ToggleButtonForegroundIndeterminatePressed": "Text", "ToggleButtonForegroundIndeterminateDisabled": "TextNull",
    # Text boxes: surface field, input border, accent border when focused.
    "TextControlBackground": "Surface", "TextControlBackgroundPointerOver": "Surface", "TextControlBackgroundFocused": "Surface",
    "TextControlBackgroundDisabled": "Disabled",
    "TextControlBorderBrush": "InputBorder", "TextControlBorderBrushPointerOver": "ButtonBorder",
    "TextControlBorderBrushFocused": "Accent", "TextControlBorderBrushDisabled": "FieldBorder",
    "TextControlForeground": "Text", "TextControlForegroundPointerOver": "Text", "TextControlForegroundFocused": "Text",
    "TextControlForegroundDisabled": "TextNull",
    "TextControlPlaceholderForeground": "TextNull", "TextControlPlaceholderForegroundPointerOver": "TextNull",
    "TextControlPlaceholderForegroundFocused": "TextNull", "TextControlPlaceholderForegroundDisabled": "TextNull",
    "TextControlSelectionHighlightColor": "Selection",
    # Combo boxes look like text boxes; the open list uses the row colours.
    "ComboBoxBackground": "Surface", "ComboBoxBackgroundPointerOver": "Surface", "ComboBoxBackgroundPressed": "Surface",
    "ComboBoxBackgroundDisabled": "Disabled", "ComboBoxBackgroundUnfocused": "Surface",
    "ComboBoxBorderBrush": "InputBorder", "ComboBoxBorderBrushPointerOver": "ButtonBorder", "ComboBoxBorderBrushPressed": "Accent",
    "ComboBoxBorderBrushDisabled": "FieldBorder",
    "ComboBoxBackgroundBorderBrushFocused": "Accent", "ComboBoxBackgroundBorderBrushUnfocused": "InputBorder",
    "ComboBoxForeground": "Text", "ComboBoxForegroundDisabled": "TextNull", "ComboBoxForegroundFocused": "Text",
    "ComboBoxForegroundFocusedPressed": "Text",
    "ComboBoxDropDownBackground": "Surface", "ComboBoxDropDownBorderBrush": "DialogBorder",
    "ComboBoxDropDownGlyphForeground": "TextMuted", "ComboBoxDropDownGlyphForegroundDisabled": "IconDisabled",
    "ComboBoxItemBackgroundPointerOver": "HoverBg", "ComboBoxItemBackgroundPressed": "PressedBg",
    "ComboBoxItemBackgroundSelected": "Selection", "ComboBoxItemBackgroundSelectedPointerOver": "Selection",
    "ComboBoxItemBackgroundSelectedPressed": "Selection",
    "ComboBoxItemForeground": "Text", "ComboBoxItemForegroundPointerOver": "Text", "ComboBoxItemForegroundPressed": "Text",
    "ComboBoxItemForegroundSelected": "Text", "ComboBoxItemForegroundSelectedPointerOver": "Text",
    "ComboBoxItemForegroundSelectedPressed": "Text", "ComboBoxItemForegroundDisabled": "TextNull",
    # Check boxes: surface box with an input border; checked is an accent box with a surface tick.
    "CheckBoxForegroundUnchecked": "Text", "CheckBoxForegroundUncheckedPointerOver": "Text", "CheckBoxForegroundUncheckedPressed": "Text",
    "CheckBoxForegroundUncheckedDisabled": "TextNull",
    "CheckBoxForegroundChecked": "Text", "CheckBoxForegroundCheckedPointerOver": "Text", "CheckBoxForegroundCheckedPressed": "Text",
    "CheckBoxForegroundCheckedDisabled": "TextNull",
    "CheckBoxForegroundIndeterminate": "Text", "CheckBoxForegroundIndeterminatePointerOver": "Text",
    "CheckBoxForegroundIndeterminatePressed": "Text", "CheckBoxForegroundIndeterminateDisabled": "TextNull",
    "CheckBoxCheckBackgroundFillUnchecked": "Surface", "CheckBoxCheckBackgroundFillUncheckedPointerOver": "Surface",
    "CheckBoxCheckBackgroundFillUncheckedPressed": "PressedBg", "CheckBoxCheckBackgroundFillUncheckedDisabled": "Disabled",
    "CheckBoxCheckBackgroundStrokeUnchecked": "InputBorder", "CheckBoxCheckBackgroundStrokeUncheckedPointerOver": "ButtonHoverBorder",
    "CheckBoxCheckBackgroundStrokeUncheckedPressed": "Accent", "CheckBoxCheckBackgroundStrokeUncheckedDisabled": "FieldBorder",
    "CheckBoxCheckBackgroundFillChecked": "Accent", "CheckBoxCheckBackgroundFillCheckedPointerOver": "Accent",
    "CheckBoxCheckBackgroundFillCheckedPressed": "Accent", "CheckBoxCheckBackgroundFillCheckedDisabled": "IconDisabled",
    "CheckBoxCheckBackgroundStrokeCheckedPointerOver": "Accent", "CheckBoxCheckBackgroundStrokeCheckedPressed": "Accent",
    "CheckBoxCheckBackgroundStrokeCheckedDisabled": "IconDisabled",
    "CheckBoxCheckBackgroundFillIndeterminate": "Accent", "CheckBoxCheckBackgroundFillIndeterminatePointerOver": "Accent",
    "CheckBoxCheckBackgroundFillIndeterminatePressed": "Accent", "CheckBoxCheckBackgroundFillIndeterminateDisabled": "IconDisabled",
    "CheckBoxCheckBackgroundStrokeIndeterminate": "Accent", "CheckBoxCheckBackgroundStrokeIndeterminatePointerOver": "Accent",
    "CheckBoxCheckBackgroundStrokeIndeterminatePressed": "Accent", "CheckBoxCheckBackgroundStrokeIndeterminateDisabled": "IconDisabled",
    "CheckBoxCheckGlyphForegroundChecked": "Surface", "CheckBoxCheckGlyphForegroundCheckedPointerOver": "Surface",
    "CheckBoxCheckGlyphForegroundCheckedPressed": "Surface", "CheckBoxCheckGlyphForegroundCheckedDisabled": "Surface",
    "CheckBoxCheckGlyphForegroundIndeterminate": "Surface", "CheckBoxCheckGlyphForegroundIndeterminatePointerOver": "Surface",
    "CheckBoxCheckGlyphForegroundIndeterminatePressed": "Surface", "CheckBoxCheckGlyphForegroundIndeterminateDisabled": "Surface",
    # Radio buttons: input-bordered circle, accent dot.
    "RadioButtonForeground": "Text", "RadioButtonForegroundPointerOver": "Text", "RadioButtonForegroundPressed": "Text",
    "RadioButtonForegroundDisabled": "TextNull",
    "RadioButtonOuterEllipseStroke": "InputBorder", "RadioButtonOuterEllipseStrokePointerOver": "ButtonHoverBorder",
    "RadioButtonOuterEllipseStrokePressed": "Accent", "RadioButtonOuterEllipseStrokeDisabled": "FieldBorder",
    "RadioButtonOuterEllipseFill": "Surface", "RadioButtonOuterEllipseFillPointerOver": "Surface",
    "RadioButtonOuterEllipseFillPressed": "PressedBg", "RadioButtonOuterEllipseFillDisabled": "Disabled",
    "RadioButtonOuterEllipseCheckedStroke": "Accent", "RadioButtonOuterEllipseCheckedStrokePointerOver": "Accent",
    "RadioButtonOuterEllipseCheckedStrokePressed": "Accent", "RadioButtonOuterEllipseCheckedStrokeDisabled": "IconDisabled",
    "RadioButtonOuterEllipseCheckedFill": "Surface", "RadioButtonOuterEllipseCheckedFillPointerOver": "Surface",
    "RadioButtonOuterEllipseCheckedFillPressed": "Surface", "RadioButtonOuterEllipseCheckedFillDisabled": "Disabled",
    "RadioButtonCheckGlyphFill": "Accent", "RadioButtonCheckGlyphFillPointerOver": "Accent", "RadioButtonCheckGlyphFillPressed": "Accent",
    "RadioButtonCheckGlyphFillDisabled": "IconDisabled",
    "RadioButtonCheckGlyphStroke": "Accent", "RadioButtonCheckGlyphStrokePointerOver": "Accent",
    "RadioButtonCheckGlyphStrokePressed": "Accent", "RadioButtonCheckGlyphStrokeDisabled": "IconDisabled",
    # Tree rows: row hover, light selection with ordinary text.
    "TreeViewItemBackground": "Transparent", "TreeViewItemBackgroundPointerOver": "RowHover", "TreeViewItemBackgroundPressed": "Selection",
    "TreeViewItemBackgroundSelected": "Selection", "TreeViewItemBackgroundSelectedPointerOver": "Selection",
    "TreeViewItemBackgroundSelectedPressed": "Selection", "TreeViewItemBackgroundSelectedDisabled": "Disabled",
    "TreeViewItemBackgroundDisabled": "Transparent",
    "TreeViewItemForeground": "Text", "TreeViewItemForegroundPointerOver": "Text", "TreeViewItemForegroundPressed": "Text",
    "TreeViewItemForegroundSelected": "Text", "TreeViewItemForegroundSelectedPointerOver": "Text",
    "TreeViewItemForegroundSelectedPressed": "Text", "TreeViewItemForegroundDisabled": "TextNull",
    "TreeViewItemForegroundSelectedDisabled": "TextNull",
    # List rows (ListBox and other list items).
    "SystemControlHighlightListAccentLowBrush": "Selection", "SystemControlHighlightListAccentMediumBrush": "Selection",
    "SystemControlHighlightListAccentHighBrush": "Selection",
    "SystemControlHighlightListLowBrush": "RowHover", "SystemControlHighlightListMediumBrush": "PressedBg",
    # Menus and context menus.
    "MenuFlyoutPresenterBackground": "Surface", "MenuFlyoutPresenterBorderBrush": "DialogBorder",
    "MenuFlyoutItemBackground": "Transparent", "MenuFlyoutItemBackgroundPointerOver": "HoverBg",
    "MenuFlyoutItemBackgroundPressed": "PressedBg", "MenuFlyoutItemBackgroundDisabled": "Transparent",
    "MenuFlyoutItemForeground": "Text", "MenuFlyoutItemForegroundPointerOver": "Text", "MenuFlyoutItemForegroundPressed": "Text",
    "MenuFlyoutItemForegroundDisabled": "TextNull",
    "MenuFlyoutItemKeyboardAcceleratorTextForeground": "TextMuted",
    "MenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver": "TextMuted",
    "MenuFlyoutItemKeyboardAcceleratorTextForegroundPressed": "TextMuted",
    "MenuFlyoutItemKeyboardAcceleratorTextForegroundDisabled": "TextNull",
    "MenuFlyoutSubItemChevron": "TextMuted", "MenuFlyoutSubItemChevronPointerOver": "TextMuted",
    "MenuFlyoutSubItemChevronPressed": "TextMuted", "MenuFlyoutSubItemChevronSubMenuOpened": "TextMuted",
    "MenuFlyoutSubItemChevronDisabled": "TextNull",
    "MenuFlyoutSeparatorBackground": "Divider",
    # Tooltips.
    "ToolTipBackground": "Surface", "ToolTipBorderBrush": "DialogBorder", "ToolTipForeground": "Text",
    # Expanders: a panel header with a soft border.
    "ExpanderHeaderBackground": "Panel", "ExpanderHeaderBackgroundPointerOver": "HoverBg", "ExpanderHeaderBackgroundPressed": "PressedBg",
    "ExpanderHeaderBackgroundDisabled": "Disabled",
    "ExpanderHeaderBorderBrush": "BorderSoft", "ExpanderHeaderBorderBrushPointerOver": "HoverBorder",
    "ExpanderHeaderBorderBrushPressed": "BorderSoft", "ExpanderHeaderBorderBrushDisabled": "BorderSoft",
    "ExpanderHeaderForeground": "Text", "ExpanderHeaderForegroundPointerOver": "Text", "ExpanderHeaderForegroundPressed": "Text",
    "ExpanderHeaderForegroundDisabled": "TextNull",
    "ExpanderChevronBackground": "Transparent", "ExpanderChevronBackgroundPointerOver": "Transparent",
    "ExpanderChevronBackgroundPressed": "Transparent", "ExpanderChevronBackgroundDisabled": "Transparent",
    "ExpanderChevronBorderBrush": "Transparent", "ExpanderChevronBorderBrushPointerOver": "Transparent",
    "ExpanderChevronBorderBrushPressed": "Transparent", "ExpanderChevronBorderBrushDisabled": "Transparent",
    "ExpanderChevronForeground": "TextMuted", "ExpanderChevronForegroundPointerOver": "Text",
    "ExpanderChevronForegroundPressed": "Text", "ExpanderChevronForegroundDisabled": "IconDisabled",
    "ExpanderContentBackground": "Surface", "ExpanderContentBorderBrush": "BorderSoft",
    # Scroll bars: neutral thumbs on a panel track.
    "ScrollBarBackground": "Transparent", "ScrollBarBackgroundPointerOver": "Panel",
    "ScrollBarTrackFill": "Panel", "ScrollBarTrackFillPointerOver": "Panel",
    "ScrollBarTrackStroke": "Transparent", "ScrollBarTrackStrokePointerOver": "BorderSoft",
    "ScrollBarThumbFillPointerOver": "IconDisabled", "ScrollBarThumbFillPressed": "TextMuted", "ScrollBarThumbFillDisabled": "BorderSoft",
    "ScrollBarButtonArrowForeground": "TextMuted", "ScrollBarButtonArrowForegroundPointerOver": "Text",
    "ScrollBarButtonArrowForegroundPressed": "Text", "ScrollBarButtonArrowForegroundDisabled": "IconDisabled",
    "ScrollBarButtonBackground": "Transparent", "ScrollBarButtonBackgroundPointerOver": "HoverBg",
    "ScrollBarButtonBackgroundPressed": "PressedBg",
    # Data grids: grid-header headers, light grid lines, row hover, light selection, accent current cell.
    "DataGridColumnHeaderBackgroundBrush": "GridHeader", "DataGridColumnHeaderHoveredBackgroundBrush": "HoverBg",
    "DataGridColumnHeaderPressedBackgroundBrush": "PressedBg", "DataGridColumnHeaderDraggedBackgroundBrush": "PressedBg",
    "DataGridColumnHeaderForegroundBrush": "Text",
    "DataGridGridLinesBrush": "GridLine", "DataGridFillerColumnGridLinesBrush": "GridLine",
    "DataGridRowSelectedBackgroundBrush": "Selection", "DataGridRowSelectedHoveredBackgroundBrush": "Selection",
    "DataGridRowSelectedUnfocusedBackgroundBrush": "Selection", "DataGridRowSelectedHoveredUnfocusedBackgroundBrush": "Selection",
    "DataGridCellFocusVisualPrimaryBrush": "Accent", "DataGridCellFocusVisualSecondaryBrush": "Accent",
    "DataGridCurrencyVisualPrimaryBrush": "Accent",
    "DataGridDetailsPresenterBackgroundBrush": "Panel", "DataGridScrollBarsSeparatorBackground": "Panel",
    "DataGridRowGroupHeaderBackgroundBrush": "GridHeader", "DataGridRowGroupHeaderHoveredBackgroundBrush": "HoverBg",
    "DataGridRowGroupHeaderPressedBackgroundBrush": "PressedBg", "DataGridRowGroupHeaderForegroundBrush": "Text",
}

# Color-typed keys: Fluent key -> palette role.
COLORS = {
    "DataGridRowHoveredBackgroundColor": "RowHover",
    "ScrollBarThumbBackgroundColor": "Border",
    "ScrollBarPanningThumbBackgroundColor": "Border",
}

# Plain values, the same in both variants.
VALUES = """  <x:Double x:Key="DataGridRowSelectedBackgroundOpacity">1</x:Double>
  <x:Double x:Key="DataGridRowSelectedHoveredBackgroundOpacity">1</x:Double>
  <x:Double x:Key="DataGridRowSelectedUnfocusedBackgroundOpacity">1</x:Double>
  <x:Double x:Key="DataGridRowSelectedHoveredUnfocusedBackgroundOpacity">1</x:Double>
  <x:Double x:Key="TreeViewItemIndent">14</x:Double>
  <Thickness x:Key="TextControlBorderThemeThicknessFocused">1</Thickness>
  <Thickness x:Key="MenuFlyoutPresenterThemePadding">0,3</Thickness>
  <Thickness x:Key="MenuFlyoutItemThemePadding">10,3,10,3</Thickness>
  <Thickness x:Key="MenuFlyoutItemThemePaddingNarrow">10,3,10,3</Thickness>
  <x:Double x:Key="MenuFlyoutThemeMinHeight">22</x:Double>
  <x:Double x:Key="MenuBarHeight">24</x:Double>
  <Thickness x:Key="ToolTipBorderThemePadding">6,3</Thickness>
  <x:Double x:Key="ToolTipContentThemeFontSize">12</x:Double>"""


def palette():
    text = (RES / "Colors.axaml").read_text(encoding="utf-8")
    light, dark = text.split('x:Key="Dark"')
    parse = lambda part: dict(re.findall(r'x:Key="DbConnect\.Color\.(\w+)">(#\w+)<', part))
    return parse(light), parse(dark)


def variant(colors):
    out = []
    for key, role in BRUSHES.items():
        colour = "Transparent" if role == "Transparent" else "{DynamicResource DbConnect.Color.%s}" % role
        out.append(f'      <SolidColorBrush x:Key="{key}" Color="{colour}" />')
    for key, role in COLORS.items():
        out.append(f'      <Color x:Key="{key}">{colors[role]}</Color>')
    return "\n".join(out)


HEADER = """<!-- GENERATED by tools/generate-fluent-overrides.py; edit the maps there, not this file.
     Fluent's own resource keys pointed at the DbConnect palette, so Fluent's control templates draw flat, in the
     design's roles, in both variants. -->
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
"""


def main():
    light, dark = palette()
    body = (HEADER + VALUES + "\n\n  <ResourceDictionary.ThemeDictionaries>\n"
            + '    <ResourceDictionary x:Key="Light">\n' + variant(light) + "\n    </ResourceDictionary>\n"
            + '    <ResourceDictionary x:Key="Dark">\n' + variant(dark) + "\n    </ResourceDictionary>\n"
            + "  </ResourceDictionary.ThemeDictionaries>\n</ResourceDictionary>\n")
    (RES / "FluentOverrides.axaml").write_text(body, encoding="utf-8", newline="\n")


if __name__ == "__main__":
    main()
