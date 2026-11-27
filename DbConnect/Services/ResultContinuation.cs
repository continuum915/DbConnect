using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Rows read by one fetch from a parked result.</summary>
public sealed record ResultFetch(IReadOnlyList<object?[]> Rows, bool Exhausted, IReadOnlyList<QueryResult> LaterResults,
    string? Error = null)
{
    public static ResultFetch Closed { get; } = new([], true, [], "The result is no longer open.");
}

/// <summary>
/// A result that reached its row cap and kept its reader open so more rows can be fetched. The provider's
/// executor stays inside the reader read (<see cref="DbReaderResults"/>) serving fetch requests, while the
/// session hands the first page back and keeps its connection reserved; anything else on the session closes
/// the result first. Only a session that arms the continuation lets a reader park, so other sessions keep
/// the plain cap.
/// </summary>
public sealed class ResultContinuation
{
    private readonly TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<Request> _requests = Channel.CreateUnbounded<Request>();
    private volatile bool _open;

    private sealed record Request(int Rows, bool Close, TaskCompletionSource<ResultFetch> Done);

    /// <summary>Set by the session that will keep the connection reserved while the reader is parked.</summary>
    internal bool IsArmed { get; private set; }

    internal void Arm() => IsArmed = true;

    /// <summary>Completes when a reader parks.</summary>
    public Task Parked => _parked.Task;

    /// <summary>True while more rows can be fetched.</summary>
    public bool IsOpen => _open;

    /// <summary>The result whose reader is parked.</summary>
    public QueryResult? Result { get; private set; }

    /// <summary>Every result the statement produced up to and including the parked one.</summary>
    public IReadOnlyList<QueryResult> ResultsSoFar { get; private set; } = [];

    /// <summary>Reads up to <paramref name="rows"/> more rows. Fetches run one at a time, in order.</summary>
    public Task<ResultFetch> FetchAsync(int rows)
    {
        if (!_open) return Task.FromResult(ResultFetch.Closed);
        var request = new Request(Math.Max(1, rows), false, new(TaskCreationOptions.RunContinuationsAsynchronously));
        return _requests.Writer.TryWrite(request) ? request.Done.Task : Task.FromResult(ResultFetch.Closed);
    }

    /// <summary>Stops serving; the reader is then disposed by its executor.</summary>
    public Task CloseAsync()
    {
        if (!_open) return Task.CompletedTask;
        var request = new Request(0, true, new(TaskCreationOptions.RunContinuationsAsynchronously));
        return _requests.Writer.TryWrite(request) ? request.Done.Task : Task.CompletedTask;
    }

    /// <summary>
    /// Called by the reader at the cap, positioned on the first row not yet returned: publishes the first page,
    /// then serves fetches until the result is exhausted or closed. <paramref name="readRest"/> reads any later
    /// result sets once this one is exhausted.
    /// </summary>
    internal async Task ServeAsync(DbDataReader reader, int[] ordinals, QueryResult result, IReadOnlyList<QueryResult> resultsSoFar,
        Func<Task<List<QueryResult>>>? readRest)
    {
        object?[]? current = DbReaderResults.ReadRowValues(reader, ordinals);
        Result = result;
        ResultsSoFar = resultsSoFar;
        _open = true;
        _parked.TrySetResult();
        try
        {
            await foreach (var request in _requests.Reader.ReadAllAsync())
            {
                if (request.Close)
                {
                    request.Done.TrySetResult(ResultFetch.Closed);
                    return;
                }
                var rows = new List<object?[]>(Math.Min(request.Rows, 10_000));
                if (current != null)
                {
                    rows.Add(current);
                    current = null;
                }
                try
                {
                    var exhausted = false;
                    while (rows.Count < request.Rows)
                    {
                        // Reads are never cancelled midway: a cancelled read can leave the reader unusable.
                        if (!await reader.ReadAsync(CancellationToken.None))
                        {
                            exhausted = true;
                            break;
                        }
                        rows.Add(DbReaderResults.ReadRowValues(reader, ordinals));
                    }
                    var later = exhausted && readRest != null ? await readRest() : [];
                    request.Done.TrySetResult(new ResultFetch(rows, exhausted, later));
                    if (exhausted) return;
                }
                catch (Exception ex)
                {
                    request.Done.TrySetResult(new ResultFetch(rows, true, [], DiagnosticRedactor.Redact(ex.Message)));
                    return;
                }
            }
        }
        finally
        {
            _open = false;
            _requests.Writer.TryComplete();
            while (_requests.Reader.TryRead(out var pending)) pending.Done.TrySetResult(ResultFetch.Closed);
        }
    }
}
