using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Views;

public partial class SshTrustDialog : Window
{
    public SshTrustDialog() => InitializeComponent();

    /// <summary>Marshals the SSH handshake callback to the UI; cancellation closes any pending prompt.</summary>
    public static async Task<SshTrustDecision> PromptAsync(Window owner, SshTrustRequest request, CancellationToken cancellationToken)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!owner.IsVisible)
                return SshTrustDecision.Decline;
            var vm = new SshTrustPromptViewModel(request);
            var dialog = new SshTrustDialog { DataContext = vm };
            vm.Complete = decision => dialog.Close(decision);
            using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(SshTrustDecision.Decline)));
            return await dialog.ShowDialog<SshTrustDecision>(owner);
        });
    }
}
