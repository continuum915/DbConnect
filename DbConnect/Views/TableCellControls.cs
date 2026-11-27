using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Views;

/// <summary>Builds display and typed editing controls for table cells.</summary>
internal sealed class TableCellControls : AvaloniaObject
{
    public static readonly AttachedProperty<Action?> CommitActionProperty =
        AvaloniaProperty.RegisterAttached<TableCellControls, Control, Action?>("CommitAction");

    // RAIN-P2-08 — Picks an editor control from the column's classified type. Everything but
    // Boolean pairs its editor with an explicit NULL toggle (see BuildNullToggle); Boolean uses a
    // three-state CheckBox instead, since Checked/Unchecked/Indeterminate already covers
    // true/false/NULL without a second control.
    public static Avalonia.Controls.Control BuildTableCellEditor(
        TableDataTabViewModel vm, EditableRow row, string colName, CellEditorKind editorKind, ColumnDataType columnType)
    {
        var isReadOnly = row.State != EditState.Inserted && !vm.CanEditExistingRows;

        return editorKind switch
        {
            CellEditorKind.Boolean => BuildBooleanEditor(row, colName, isReadOnly),
            CellEditorKind.Date => BuildDateEditor(vm, row, colName, isReadOnly),
            CellEditorKind.DateTime => BuildDateTimeEditor(vm, row, colName, isReadOnly),
            CellEditorKind.Enum => BuildEnumEditor(vm, row, colName, isReadOnly),
            CellEditorKind.Set => BuildSetEditor(vm, row, colName, isReadOnly),
            _ => BuildTextEditor(vm, row, colName, columnType, isReadOnly),
        };
    }

    /// <summary>RAIN-P2-09 — A dropdown listing only the values the provider reports as valid for
    /// this ENUM/native-enum column, so an edit can never write a value the column would reject.</summary>
    private static Avalonia.Controls.Control BuildEnumEditor(
        TableDataTabViewModel vm, EditableRow row, string colName, bool isReadOnly)
    {
        row.Values.TryGetValue(colName, out var current);
        var values = vm.GetEnumValues(colName);
        var comboBox = new ComboBox
        {
            ItemsSource = values,
            IsEnabled = !isReadOnly && current != null,
            SelectedItem = current as string,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        };
        comboBox.SelectionChanged += (_, _) =>
        {
            if (row.Values.GetValueOrDefault(colName) == null)
                return; // the NULL toggle owns the value while it's NULL

            // A cleared selection has no ComboBox item to fall back to — the first provider-listed
            // value is the only guaranteed-valid non-NULL member (unlike other types, an ENUM
            // rejects "" as a placeholder, so CellValueConverter.DefaultNonNullValue can't be used here).
            row.Values[colName] = comboBox.SelectedItem as string ?? values.FirstOrDefault() ?? string.Empty;
        };

        var nullToggle = BuildNullToggle(vm, row, colName, isReadOnly, () =>
        {
            var value = row.Values.GetValueOrDefault(colName);
            comboBox.IsEnabled = !isReadOnly && value != null;
            comboBox.SelectedItem = value as string;
        });

        return ComposeWithNullToggle(comboBox, nullToggle);
    }

    /// <summary>RAIN-P2-09 — A checklist of the MySQL SET column's valid members; the checked
    /// subset is stored comma-joined, matching how MySQL itself renders/accepts a SET literal.</summary>
    private static Avalonia.Controls.Control BuildSetEditor(
        TableDataTabViewModel vm, EditableRow row, string colName, bool isReadOnly)
    {
        row.Values.TryGetValue(colName, out var current);
        var values = vm.GetEnumValues(colName);

        var button = new Button
        {
            IsEnabled = !isReadOnly && current != null,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        };

        var checklist = new StackPanel { Orientation = Avalonia.Layout.Orientation.Vertical, Spacing = 2, Margin = new Avalonia.Thickness(8) };
        var checkBoxes = values.Select(value => new CheckBox
        {
            Content = value,
            IsChecked = SplitSetMembers(current as string).Contains(value),
        }).ToList();
        foreach (var checkBox in checkBoxes)
            checklist.Children.Add(checkBox);

        var flyout = new Flyout { Content = checklist };
        button.Flyout = flyout;

        void UpdateButtonText()
        {
            var selected = string.Join(",", checkBoxes.Where(cb => cb.IsChecked == true).Select(cb => (string)cb.Content!));
            button.Content = string.IsNullOrEmpty(selected) ? "(none)" : selected;
        }
        UpdateButtonText();

        foreach (var checkBox in checkBoxes)
        {
            checkBox.IsCheckedChanged += (_, _) =>
            {
                if (row.Values.GetValueOrDefault(colName) == null)
                    return; // the NULL toggle owns the value while it's NULL

                row.Values[colName] = string.Join(",", checkBoxes.Where(cb => cb.IsChecked == true).Select(cb => (string)cb.Content!));
                UpdateButtonText();
            };
        }

        var nullToggle = BuildNullToggle(vm, row, colName, isReadOnly, () =>
        {
            var value = row.Values.GetValueOrDefault(colName);
            button.IsEnabled = !isReadOnly && value != null;
            var members = SplitSetMembers(value as string);
            foreach (var checkBox in checkBoxes)
                checkBox.IsChecked = members.Contains((string)checkBox.Content!);
            UpdateButtonText();
        });

        return ComposeWithNullToggle(button, nullToggle);
    }

    private static HashSet<string> SplitSetMembers(string? value) =>
        string.IsNullOrEmpty(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    private static Avalonia.Controls.Control BuildTextEditor(
        TableDataTabViewModel vm, EditableRow row, string colName, ColumnDataType columnType, bool isReadOnly)
    {
        row.Values.TryGetValue(colName, out var current);
        var textBox = new TextBox
        {
            IsReadOnly = isReadOnly,
            IsEnabled = !isReadOnly && current != null,
            Text = CellValueConverter.FormatForEditor(current, columnType),
            BorderThickness = new Avalonia.Thickness(0),
            Padding = new Avalonia.Thickness(4, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        };
        // revert-grid-fixups — Typed text is written back only when the grid commits the edit (see
        // OnCellEditEnding), never on focus loss: a cancelled edit, or one a revert cancels, must
        // not leak its text into the row.
        void Commit()
        {
            if (row.Values.GetValueOrDefault(colName) == null)
                return; // the NULL toggle owns the value while it's NULL

            try
            {
                row.Values[colName] = CellValueConverter.Parse(textBox.Text, columnType);
            }
            catch (FormatException)
            {
                // Reject the edit rather than corrupt the stored value with an unparsable string.
            }
        }

        var nullToggle = BuildNullToggle(vm, row, colName, isReadOnly, () =>
        {
            var value = row.Values.GetValueOrDefault(colName);
            textBox.IsEnabled = !isReadOnly && value != null;
            textBox.Text = CellValueConverter.FormatForEditor(value, columnType);
        });

        var editor = ComposeWithNullToggle(textBox, nullToggle);
        editor.SetValue(CommitActionProperty, Commit);
        return editor;
    }

    private static Avalonia.Controls.Control BuildBooleanEditor(EditableRow row, string colName, bool isReadOnly)
    {
        row.Values.TryGetValue(colName, out var current);
        var checkBox = new CheckBox
        {
            IsThreeState = true,
            IsEnabled = !isReadOnly,
            IsChecked = CellValueConverter.Coerce(current, ColumnDataType.Boolean) as bool?,
            Margin = new Avalonia.Thickness(6, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        checkBox.IsCheckedChanged += (_, _) => row.Values[colName] = checkBox.IsChecked;
        return checkBox;
    }

    private static Avalonia.Controls.Control BuildDateEditor(TableDataTabViewModel vm, EditableRow row, string colName, bool isReadOnly)
    {
        row.Values.TryGetValue(colName, out var raw);
        var current = CellValueConverter.Coerce(raw, ColumnDataType.Date);
        var datePicker = new DatePicker
        {
            IsEnabled = !isReadOnly && current != null,
            SelectedDate = ToDateTimeOffset(current),
        };
        datePicker.SelectedDateChanged += (_, _) =>
        {
            if (row.Values.GetValueOrDefault(colName) == null)
                return; // the NULL toggle owns the value while it's NULL

            row.Values[colName] = datePicker.SelectedDate?.Date
                ?? CellValueConverter.DefaultNonNullValue(ColumnDataType.Date);
        };

        var nullToggle = BuildNullToggle(vm, row, colName, isReadOnly, () =>
        {
            var value = row.Values.GetValueOrDefault(colName);
            datePicker.IsEnabled = !isReadOnly && value != null;
            datePicker.SelectedDate = ToDateTimeOffset(value);
        });

        return ComposeWithNullToggle(datePicker, nullToggle);
    }

    private static Avalonia.Controls.Control BuildDateTimeEditor(TableDataTabViewModel vm, EditableRow row, string colName, bool isReadOnly)
    {
        row.Values.TryGetValue(colName, out var raw);
        var currentValue = CellValueConverter.Coerce(raw, ColumnDataType.DateTime) as DateTime?;

        var datePicker = new DatePicker { IsEnabled = !isReadOnly && currentValue != null, SelectedDate = ToDateTimeOffset(currentValue) };
        var timePicker = new TimePicker { IsEnabled = !isReadOnly && currentValue != null, SelectedTime = currentValue?.TimeOfDay };

        void CommitDateTime()
        {
            if (row.Values.GetValueOrDefault(colName) == null)
                return; // the NULL toggle owns the value while it's NULL

            var date = datePicker.SelectedDate?.Date ?? DateTime.Today;
            var time = timePicker.SelectedTime ?? TimeSpan.Zero;
            row.Values[colName] = date.Add(time);
        }
        datePicker.SelectedDateChanged += (_, _) => CommitDateTime();
        timePicker.SelectedTimeChanged += (_, _) => CommitDateTime();

        var pickers = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
        pickers.Children.Add(datePicker);
        pickers.Children.Add(timePicker);

        var nullToggle = BuildNullToggle(vm, row, colName, isReadOnly, () =>
        {
            var value = row.Values.GetValueOrDefault(colName) as DateTime?;
            datePicker.IsEnabled = !isReadOnly && value != null;
            timePicker.IsEnabled = !isReadOnly && value != null;
            datePicker.SelectedDate = ToDateTimeOffset(value);
            timePicker.SelectedTime = value?.TimeOfDay;
        });

        return ComposeWithNullToggle(pickers, nullToggle);
    }

    /// <summary>
    /// A checkbox that flips the cell between NULL and a type-correct placeholder via
    /// <see cref="TableDataTabViewModel.ToggleCellNull"/>, then runs <paramref name="onToggled"/>
    /// so the paired editor control (built alongside this one, in the same closure) reflects the
    /// new value immediately — <c>EditableRow.Values</c> is a plain dictionary with no change
    /// notification, so nothing else would keep the two controls in sync.
    /// </summary>
    private static CheckBox BuildNullToggle(
        TableDataTabViewModel vm, EditableRow row, string colName, bool isReadOnly, Action onToggled)
    {
        var checkBox = new CheckBox
        {
            Content = "NULL",
            IsEnabled = !isReadOnly,
            IsChecked = row.Values.GetValueOrDefault(colName) == null,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(4, 0, 4, 0),
        };
        checkBox.IsCheckedChanged += (_, _) =>
        {
            vm.ToggleCellNull(row, colName);
            onToggled();
        };
        return checkBox;
    }

    private static Avalonia.Controls.Control ComposeWithNullToggle(Avalonia.Controls.Control mainEditor, Avalonia.Controls.Control nullToggle)
    {
        var panel = new Avalonia.Controls.Grid { ColumnDefinitions = new Avalonia.Controls.ColumnDefinitions("*,Auto") };
        Avalonia.Controls.Grid.SetColumn(mainEditor, 0);
        Avalonia.Controls.Grid.SetColumn(nullToggle, 1);
        panel.Children.Add(mainEditor);
        panel.Children.Add(nullToggle);
        return panel;
    }

    private static DateTimeOffset? ToDateTimeOffset(object? value) =>
        value is DateTime dt ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), TimeSpan.Zero) : null;

    public static Avalonia.Controls.Control BuildTableCell(EditableRow row, string colName)
    {
        row.Values.TryGetValue(colName, out var val);

        if (val is byte[] bytes && bytes.Length > 0)
        {
            var fmt = ImageFormatDetector.Detect(bytes);
            if (fmt != null)
            {
                try
                {
                    using var ms = new MemoryStream(bytes);
                    return new Image
                    {
                        Source = new Bitmap(ms),
                        Height = 24,
                        Stretch = Stretch.Uniform,
                        Margin = new Avalonia.Thickness(2, 1),
                    };
                }
                catch { /* fall through to text */ }
            }
        }

        // NULL reads as "(Null)" in the null colour, italic; an edited cell of an existing row sits on the dirty colour.
        var block = new TextBlock
        {
            Text = val is null or DBNull ? "(Null)" : GridCellDisplay.FormatValue(val),
            Padding = new Avalonia.Thickness(6, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        if (val is null or DBNull) block.Classes.Add("null-cell");
        if (row.State != EditState.Modified || !row.IsColumnChanged(colName)) return block;

        var cell = new Border { Child = block };
        cell.Classes.Add("dirty-cell");
        return cell;
    }

}
