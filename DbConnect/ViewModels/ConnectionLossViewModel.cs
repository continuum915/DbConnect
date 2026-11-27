using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DbConnect.ViewModels;

/// <summary>
/// The banner a tab shows when its connection drops. It says whether an open transaction was lost (the server
/// discards an uncommitted transaction on disconnect) and offers Reconnect, which opens a new session through the
/// normal path (tunnel and session setup included) without running anything again.
/// </summary>
public sealed partial class ConnectionLossViewModel(Func<Task> reconnect) : ObservableObject
{
    [ObservableProperty] private bool _isLost;
    [ObservableProperty] private bool _isReconnecting;
    [ObservableProperty] private string _message = string.Empty;

    public void MarkLost(string connectionName, bool transactionWasOpen)
    {
        IsLost = true;
        Message = $"The connection to {connectionName} was lost." +
                  (transactionWasOpen
                      ? " Your open transaction was rolled back by the server; its changes were not committed."
                      : "") +
                  " Reconnect opens a new session; nothing is run again.";
    }

    /// <summary>The status for a failed load; marks the connection lost when the session itself was dropped.</summary>
    public string Describe(DbConnect.Services.IDbSession session, string connectionName, string failure)
    {
        if (!session.IsConnectionLost) return failure;
        MarkLost(connectionName, transactionWasOpen: false);
        return failure + " (connection lost)";
    }

    [RelayCommand]
    private async Task ReconnectAsync()
    {
        IsReconnecting = true;
        try
        {
            await reconnect();
            IsLost = false;
            Message = string.Empty;
        }
        catch (Exception ex)
        {
            Message = $"Reconnect failed: {ex.Message}";
        }
        finally
        {
            IsReconnecting = false;
        }
    }

    [RelayCommand]
    private void Dismiss() => IsLost = false;
}
