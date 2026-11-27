using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Moves drivers with synchronous async methods off the UI thread, with bounded read-ahead.</summary>
internal sealed class BackgroundExportSource(IExportSource inner) : IExportSource
{
    public IReadOnlyList<ExportColumn> Columns => inner.Columns;

    public async IAsyncEnumerable<IReadOnlyList<object?>> ReadRowsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = Channel.CreateBounded<IReadOnlyList<object?>>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true
        });
        var producer = Task.Run(async () =>
        {
            try
            {
                await foreach (var row in inner.ReadRowsAsync(stop.Token))
                    await channel.Writer.WriteAsync(row, stop.Token);
                channel.Writer.TryComplete();
            }
            catch (System.Exception error) { channel.Writer.TryComplete(error); }
        }, CancellationToken.None);
        try
        {
            await foreach (var row in channel.Reader.ReadAllAsync(cancellationToken)) yield return row;
        }
        finally
        {
            stop.Cancel();
            await producer;
        }
    }
}
