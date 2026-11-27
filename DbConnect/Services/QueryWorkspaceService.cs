using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

public sealed class QueryWorkspaceService
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileGates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public QueryWorkspaceService(string? filePath = null)
    {
        if (filePath != null)
        {
            _filePath = filePath;
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            _gate = GetFileGate(_filePath);
            return;
        }

        var rainFolder = AppDataPaths.RootDirectory;
        Directory.CreateDirectory(rainFolder);
        _filePath = Path.Combine(rainFolder, "query-workspace.json");
        _gate = GetFileGate(_filePath);
    }

    public async Task<int> LoadTimeoutSecondsAsync()
    {
        var state = await LoadStateAsync().ConfigureAwait(false);
        return state.QueryTimeoutSeconds;
    }

    public async Task SaveTimeoutSecondsAsync(int timeoutSeconds)
    {
        await UpdateStateAsync(state =>
        {
            state.QueryTimeoutSeconds = timeoutSeconds;
        }).ConfigureAwait(false);
    }

    public async Task<int> LoadResultRowCapAsync()
    {
        var state = await LoadStateAsync().ConfigureAwait(false);
        return state.ResultRowCap;
    }

    public async Task SaveResultRowCapAsync(int rowCap)
    {
        await UpdateStateAsync(state =>
        {
            state.ResultRowCap = rowCap;
        }).ConfigureAwait(false);
    }

    public async Task<int> LoadTablePageSizeAsync()
    {
        var state = await LoadStateAsync().ConfigureAwait(false);
        return state.TablePageSize;
    }

    public async Task SaveTablePageSizeAsync(int pageSize)
    {
        await UpdateStateAsync(state =>
        {
            state.TablePageSize = pageSize;
        }).ConfigureAwait(false);
    }

    public async Task<TableColumnLayout> LoadTableColumnLayoutAsync(ConnectionInfo connection, string tableName)
    {
        var state = await LoadStateAsync().ConfigureAwait(false);
        return state.ColumnLayouts.TryGetValue(GetTableKey(connection, tableName), out var layout)
            ? layout
            : new TableColumnLayout();
    }

    public async Task SaveTableColumnLayoutAsync(ConnectionInfo connection, string tableName, TableColumnLayout layout)
    {
        await UpdateStateAsync(state =>
        {
            state.ColumnLayouts[GetTableKey(connection, tableName)] = layout.Clone();
        }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<QueryHistoryEntry>> LoadHistoryAsync(ConnectionInfo? connection)
    {
        if (connection == null)
            return [];

        var state = await LoadStateAsync().ConfigureAwait(false);
        var key = GetConnectionKey(connection);
        return state.HistoryByConnection.TryGetValue(key, out var history)
            ? history.OrderByDescending(entry => entry.ExecutedAt).ToList()
            : [];
    }

    public async Task AddHistoryEntryAsync(ConnectionInfo? connection, QueryHistoryEntry entry)
    {
        if (connection == null || string.IsNullOrWhiteSpace(entry.Sql))
            return;

        await UpdateStateAsync(state =>
        {
            var key = GetConnectionKey(connection);
            if (!state.HistoryByConnection.TryGetValue(key, out var history))
            {
                history = [];
                state.HistoryByConnection[key] = history;
            }

            ApplyStoredForm(entry);
            history.ForEach(ApplyStoredForm); // entries written before oversized scripts were limited
            history.RemoveAll(existing => string.Equals(existing.Sql, entry.Sql, StringComparison.Ordinal)
                && existing.Sha256 == entry.Sha256);
            history.Insert(0, entry);
            if (history.Count > 25)
                history.RemoveRange(25, history.Count - 25);
        }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SavedQueryDefinition>> LoadSavedQueriesAsync()
    {
        var state = await LoadStateAsync().ConfigureAwait(false);
        return state.SavedQueries
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.ProviderName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task SaveQueryAsync(SavedQueryDefinition definition)
    {
        await UpdateStateAsync(state =>
        {
            state.SavedQueries.RemoveAll(existing =>
                string.Equals(existing.Name, definition.Name, StringComparison.OrdinalIgnoreCase));
            state.SavedQueries.Add(definition);
        }).ConfigureAwait(false);
    }

    public Task MigrateConnectionsAsync(IEnumerable<ConnectionInfo> connections) => UpdateStateAsync(state =>
    {
        foreach (var connection in connections)
        {
            var oldKey = GetLegacyConnectionKey(connection);
            var key = GetConnectionKey(connection);
            if (state.HistoryByConnection.Remove(oldKey, out var history))
            {
                if (state.HistoryByConnection.TryGetValue(key, out var current))
                    current.AddRange(history);
                else
                    state.HistoryByConnection[key] = history;
            }

            var prefix = oldKey + "\n";
            foreach (var tableKey in state.ColumnLayouts.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                var layout = state.ColumnLayouts[tableKey];
                state.ColumnLayouts.TryAdd(key + tableKey[oldKey.Length..], layout);
                state.ColumnLayouts.Remove(tableKey);
            }
        }
    }, tolerateInvalidState: false);

    public Task DeleteConnectionAsync(ConnectionInfo connection) => UpdateStateAsync(state =>
    {
        foreach (var key in new[] { GetConnectionKey(connection), GetLegacyConnectionKey(connection) })
        {
            state.HistoryByConnection.Remove(key);
            foreach (var tableKey in state.ColumnLayouts.Keys.Where(k => k.StartsWith(key + "\n", StringComparison.Ordinal)).ToList())
                state.ColumnLayouts.Remove(tableKey);
        }
    }, tolerateInvalidState: false);

    private static void ApplyStoredForm(QueryHistoryEntry entry)
    {
        var stored = StoredQueryText.From(entry.Sql);
        if (!stored.IsTruncated)
            return;

        entry.Sql = stored.Text;
        entry.IsTruncated = true;
        entry.FullLength = stored.FullLength;
        entry.Sha256 = stored.Sha256;
    }

    private async Task<QueryWorkspaceState> LoadStateAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath))
                return new QueryWorkspaceState();

            var json = await File.ReadAllTextAsync(_filePath).ConfigureAwait(false);
            return JsonSerializer.Deserialize<QueryWorkspaceState>(json) ?? new QueryWorkspaceState();
        }
        catch
        {
            return new QueryWorkspaceState();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task UpdateStateAsync(Action<QueryWorkspaceState> update, bool tolerateInvalidState = true)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            QueryWorkspaceState state;
            if (!File.Exists(_filePath))
            {
                state = new QueryWorkspaceState();
            }
            else
            {
                try
                {
                    var json = await File.ReadAllTextAsync(_filePath).ConfigureAwait(false);
                    state = JsonSerializer.Deserialize<QueryWorkspaceState>(json) ?? new QueryWorkspaceState();
                }
                catch when (tolerateInvalidState)
                {
                    state = new QueryWorkspaceState();
                }
            }

            if (!tolerateInvalidState)
                ValidateConnectionState(state);
            update(state);
            var updatedJson = JsonSerializer.Serialize(state, JsonOptions);
            await File.WriteAllTextAsync(_filePath, updatedJson).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void ValidateConnectionState(QueryWorkspaceState state)
    {
        if (state.HistoryByConnection is null || state.ColumnLayouts is null ||
            state.HistoryByConnection.Values.Any(history => history is null) ||
            state.ColumnLayouts.Values.Any(layout => layout is null))
            throw new JsonException("Connection workspace collections must not be null.");
    }

    private static string GetConnectionKey(ConnectionInfo connection) => connection.Id.ToString();

    private static string GetLegacyConnectionKey(ConnectionInfo connection)
    {
        var input = $"{connection.Provider}\n{connection.Name}\n{connection.ConnectionString}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }

    private static string GetTableKey(ConnectionInfo connection, string tableName) =>
        $"{GetConnectionKey(connection)}\n{tableName}";

    private static SemaphoreSlim GetFileGate(string filePath) =>
        FileGates.GetOrAdd(Path.GetFullPath(filePath), _ => new SemaphoreSlim(1, 1));

    private sealed class QueryWorkspaceState
    {
        public int QueryTimeoutSeconds { get; set; } = 30;
        public int ResultRowCap { get; set; } = 1000;
        public int TablePageSize { get; set; } = 1000;
        public Dictionary<string, List<QueryHistoryEntry>> HistoryByConnection { get; set; } = [];
        public List<SavedQueryDefinition> SavedQueries { get; set; } = [];
        public Dictionary<string, TableColumnLayout> ColumnLayouts { get; set; } = [];
    }
}
