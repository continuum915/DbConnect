using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.Tests;

public sealed class SshTrustDialogHeadlessTests
{
    [AvaloniaTheory]
    [InlineData("Trust once", true, false)]
    [InlineData("Trust always", true, true)]
    [InlineData("Decline", false, false)]
    public async Task ConnectionDialogShowsUnknownFingerprintAndHonoursClickedChoice(string choice, bool accepted, bool persisted)
    {
        using var context = new SshTrustTestContext();
        var vm = ViewModel(context);
        var owner = new ConnectionDialog { DataContext = vm };
        owner.Show();
        try
        {
            var test = vm.TestConnectionCommand.ExecuteAsync(null);
            var prompt = await WaitForPrompt(owner);
            var shown = ShownText(prompt);
            Assert.Contains("Unknown SSH host", shown);
            Assert.Contains("bastion.example:22", shown);
            Assert.Contains("ssh-ed25519", shown);
            Assert.Contains("SHA256:presented-key", shown);
            Assert.DoesNotContain(Buttons(prompt), button => Equals(button.Content, "Replace trusted key"));
            Click(prompt, choice);
            await test.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(accepted, vm.TestResult!.Success);
            if (!accepted) Assert.Equal(ConnectionFailureCategory.SshHostKeyRejected, vm.TestResult.Category);
            Assert.Equal(persisted ? context.Transport.Key : null, new KnownHostsStore(context.DirectoryPath).Find("bastion.example", 22));
            Assert.Equal(0, context.Transport.ActiveSessions);
            Assert.Equal(0, context.Transport.ActiveForwards);
        }
        finally { owner.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("Replace trusted key", true)]
    [InlineData("Decline", false)]
    public async Task ChangedKeyShowsWarningAndBothFingerprintsWithOnlyExplicitReplacement(string choice, bool accepted)
    {
        using var context = new SshTrustTestContext();
        var old = context.Transport.Key;
        Assert.True(context.Store.Save(old, null));
        context.Transport.Key = old with { Sha256Fingerprint = "SHA256:replacement-key" };
        var vm = ViewModel(context);
        var owner = new ConnectionDialog { DataContext = vm };
        owner.Show();
        try
        {
            var test = vm.TestConnectionCommand.ExecuteAsync(null);
            var prompt = await WaitForPrompt(owner);
            var shown = ShownText(prompt);
            Assert.Contains("WARNING: SSH host key changed", shown);
            Assert.Contains("The connection is blocked", shown);
            Assert.Contains("SHA256:presented-key", shown);
            Assert.Contains("SHA256:replacement-key", shown);
            Assert.DoesNotContain(Buttons(prompt), button => Equals(button.Content, "Trust once") || Equals(button.Content, "Trust always"));
            Click(prompt, choice);
            await test.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(accepted, vm.TestResult!.Success);
            Assert.Equal(accepted ? context.Transport.Key : old, new KnownHostsStore(context.DirectoryPath).Find(old.Host, old.Port));
            Assert.Equal(0, context.Transport.ActiveSessions);
            Assert.Equal(0, context.Transport.ActiveForwards);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task CancelTestClosesThePromptAndLeavesNoTrustOrTunnel()
    {
        using var context = new SshTrustTestContext();
        var vm = ViewModel(context);
        var owner = new ConnectionDialog { DataContext = vm };
        owner.Show();
        try
        {
            var test = vm.TestConnectionCommand.ExecuteAsync(null);
            var prompt = await WaitForPrompt(owner);
            vm.TestConnectionCancelCommand.Execute(null);
            await test.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();
            Assert.False(prompt.IsVisible);
            Assert.Equal("Test cancelled.", vm.StatusMessage);
            Assert.Null(vm.TestResult);
            Assert.Null(context.Store.Find("bastion.example", 22));
            Assert.Equal(0, context.Transport.ActiveSessions);
            Assert.Equal(0, context.Transport.ActiveForwards);
        }
        finally { owner.Close(); }
    }

    private static ConnectionDialogViewModel ViewModel(SshTrustTestContext context) =>
        new(context.Databases, new ConnectionSettingsService(), context.Establisher)
        {
            SelectedProvider = "PostgreSQL",
            Host = "db.internal",
            Database = "app",
            Username = "u",
            UseSshTunnel = true,
            SshHost = "bastion.example",
            SshPort = "22",
            SshUsername = "u"
        };

    private static async Task<SshTrustDialog> WaitForPrompt(Window owner)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!owner.OwnedWindows.OfType<SshTrustDialog>().Any())
            await Task.Delay(10, timeout.Token);
        Dispatcher.UIThread.RunJobs();
        return owner.OwnedWindows.OfType<SshTrustDialog>().Single();
    }

    private static string ShownText(Window window) => string.Join(" | ", window.GetVisualDescendants()
        .OfType<TextBlock>().Where(block => block.IsEffectivelyVisible).Select(block => block.Text));

    private static IEnumerable<Button> Buttons(Window window) => window.GetVisualDescendants()
        .OfType<Button>().Where(button => button.IsEffectivelyVisible);

    private static void Click(Window window, string caption)
    {
        var button = Buttons(window).Single(button => Equals(button.Content, caption));
        Assert.True(button.IsEnabled);
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
}
