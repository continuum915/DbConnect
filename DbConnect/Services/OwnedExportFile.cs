using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DbConnect.Services;

/// <summary>
/// Writes an export to a temporary file this operation creates beside the destination, and publishes it only after
/// the writer finished normally and the file is closed. On failure or cancel the temporary file is deleted; if that
/// fails too, its path is kept in <see cref="RetainedTemporaryPath"/> so the outcome can name it.
/// </summary>
internal sealed class OwnedExportFile(IExportFileOperations files, string destination, string extension)
{
    public string Destination { get; } = Path.GetFullPath(destination);
    public string? RetainedTemporaryPath { get; private set; }

    public async Task WriteAndPublishAsync(Func<Stream, Task> write, CancellationToken cancellationToken)
    {
        string? temporary = Path.Combine(Path.GetDirectoryName(Destination)!, $".dbconnect-export-{Guid.NewGuid():N}.{extension}");
        var owns = false;
        try
        {
            await using (var stream = files.CreateTemporary(temporary))
            {
                owns = true;
                await write(stream);
            }
            cancellationToken.ThrowIfCancellationRequested();
            files.Publish(temporary, Destination);
            temporary = null;
        }
        finally
        {
            if (temporary != null && owns)
            {
                try { files.DeleteTemporary(temporary); }
                catch { RetainedTemporaryPath = temporary; }
            }
        }
    }
}
