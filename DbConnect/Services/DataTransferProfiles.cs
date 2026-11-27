using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>A profile's two sides as open sessions, or why they cannot be found.</summary>
public sealed record DataTransferEndpoints(DatabaseSession? Source, DatabaseSession? Target, string? Error);

/// <summary>
/// Saved Data Transfer profiles, kept in the app's data folder beside automation jobs, and how a profile runs: its
/// connections are found among the open ones (by id, then by name), the databases it picked are opened on their
/// servers, its tables are found in the source by schema and name, and <see cref="DataTransferJob"/> copies them.
/// </summary>
public sealed class DataTransferProfiles(string? dataDirectory = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly string _directory = dataDirectory ?? AppDataPaths.RootDirectory;
    private string File => Path.Combine(_directory, "data-transfer-profiles.json");

    public async Task<List<DataTransferProfile>> LoadAsync()
    {
        if (!System.IO.File.Exists(File)) return [];
        return JsonSerializer.Deserialize<List<DataTransferProfile>>(await System.IO.File.ReadAllTextAsync(File), Json) ?? [];
    }

    public async Task<DataTransferProfile?> FindAsync(string name) =>
        (await LoadAsync()).FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Saves the profile, replacing one of the same name.</summary>
    public async Task SaveAsync(DataTransferProfile profile)
    {
        var profiles = await LoadAsync();
        profiles.RemoveAll(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
        profiles.Add(profile);
        await WriteAsync(profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public async Task DeleteAsync(string name)
    {
        var profiles = await LoadAsync();
        profiles.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        await WriteAsync(profiles);
    }

    private async Task WriteAsync(List<DataTransferProfile> profiles)
    {
        Directory.CreateDirectory(_directory);
        var temporary = File + ".tmp";
        await System.IO.File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(profiles, Json));
        System.IO.File.Move(temporary, File, overwrite: true);
    }

    /// <summary>The open sessions a profile's two sides use; each must be connected first.</summary>
    public static async Task<DataTransferEndpoints> ResolveAsync(DataTransferProfile profile, IReadOnlyList<DatabaseSession> open)
    {
        var (source, sourceError) = await ResolveAsync(profile.Source, open, "source");
        if (source == null) return new(null, null, sourceError);
        var (target, targetError) = await ResolveAsync(profile.Target, open, "target");
        return new(source, target, targetError);
    }

    private static async Task<(DatabaseSession?, string?)> ResolveAsync(DataTransferEndpoint side, IReadOnlyList<DatabaseSession> open, string role)
    {
        // By id, which survives a rename; by name for a connection saved again under a new id.
        var candidates = open.Where(s => side.ConnectionId is { } id && s.Connection.Id == id).ToList();
        if (candidates.Count == 0) candidates = open.Where(s => string.Equals(s.Name, side.ConnectionName, StringComparison.Ordinal)).ToList();
        if (candidates.Count == 0) return (null, $"Open the {role} connection \"{side.ConnectionName}\" first, then run the transfer again.");
        if (side.Database == null) return (candidates[0], null);
        if (candidates.FirstOrDefault(s => s.DatabaseLabel == side.Database) is { } open1) return (open1, null);
        var session = candidates.FirstOrDefault(s => s.Server != null) ?? candidates[0];
        if (session.Server == null)
            return (null, $"The {role} connection \"{side.ConnectionName}\" no longer shows every database, so {side.Database} cannot be opened.");
        return await session.Server.OpenAsync(side.Database) is { } database
            ? (database, null)
            : (null, $"The {role} database {side.Database} could not be opened.");
    }

    /// <summary>Runs a profile: its tables as the source lists them now; tables it names that are gone are reported as failed.</summary>
    public static async Task<IReadOnlyList<DataTransferTableOutcome>> RunAsync(DataTransferProfile profile, DatabaseSession source, DatabaseSession target,
        IProgress<DataTransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var listed = (await DumpCatalog.ResolveAsync(source, DumpSelection.Database)).Tables.Where(t => t.Kind == SchemaNodeType.Table).ToList();
        var tables = listed.Where(profile.Includes).ToList();
        var outcomes = tables.Count == 0 ? [] : (await DataTransferJob.RunAsync(source, target, tables, profile.ToOptions(tables), progress, cancellationToken)).ToList();
        foreach (var missing in profile.Tables.Where(t => !tables.Any(l => l.Name == t.Name && (l.Schema ?? "") == (t.Schema ?? ""))))
            outcomes.Add(new(new DatabaseObjectIdentity(null, missing.Schema, missing.Name, SchemaNodeType.Table), DataTransferTableStatus.Failed, 0,
                TimeSpan.Zero, "The source has no table of this name any more."));
        return outcomes;
    }
}
