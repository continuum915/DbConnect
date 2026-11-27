using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbConnect.Views;

namespace DbConnect.Tests.Styles;

/// <summary>Ticket 02: GoToLineDialog, the proof dialog on the shared form and footer styles.</summary>
public sealed class GoToLineDialogTests
{
    [AvaloniaFact]
    public void UsesTheSharedFormAndFooterWithOneDefaultAndOneCancelButton()
    {
        var dialog = new GoToLineDialog(100);
        dialog.Show();

        var descendants = dialog.GetVisualDescendants().ToList();
        Assert.Single(descendants.OfType<Grid>(), g => g.Classes.Contains("form"));
        var footer = Assert.Single(descendants.OfType<Border>(), b => b.Classes.Contains("dialog-footer"));
        var buttons = footer.GetVisualDescendants().OfType<Button>().ToList();
        var go = Assert.Single(buttons, b => b.IsDefault);
        Assert.Contains("primary", go.Classes);
        Assert.Single(buttons, b => b.IsCancel);
        dialog.Close();
    }

    [AvaloniaFact]
    public async Task Enter_GoesToTheTypedLine()
    {
        var (owner, dialog, result) = Open(100);
        dialog.FindControl<TextBox>("LineNumberBox")!.Text = "42";
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(42, await result);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Enter_WithAnOutOfRangeLine_StaysOpenAndShowsTheRange()
    {
        var (owner, dialog, result) = Open(100);
        dialog.FindControl<TextBox>("LineNumberBox")!.Text = "500";
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(result.IsCompleted);
        var error = dialog.FindControl<TextBlock>("ErrorText")!;
        Assert.True(error.IsVisible);
        Assert.Equal("Enter a number between 1 and 100.", error.Text);
        dialog.Close();
        Assert.Null(await result);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Escape_Cancels()
    {
        var (owner, dialog, result) = Open(100);
        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(await result);
        owner.Close();
    }

    private static (Window Owner, GoToLineDialog Dialog, Task<int?> Result) Open(int maxLine)
    {
        var owner = new Window();
        owner.Show();
        var dialog = new GoToLineDialog(maxLine);
        var result = dialog.ShowDialog<int?>(owner);
        Dispatcher.UIThread.RunJobs();
        return (owner, dialog, result);
    }
}
