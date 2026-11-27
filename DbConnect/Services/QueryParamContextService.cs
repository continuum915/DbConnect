using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

public class QueryParamContextService
{
    private readonly string _storageDir;

    public QueryParamContextService()
    {
        _storageDir = Path.Combine(AppDataPaths.RootDirectory, "paramcontexts");

        if (!Directory.Exists(_storageDir))
        {
            Directory.CreateDirectory(_storageDir);
        }
    }

    public async Task<List<QueryParamContext>> LoadAsync(string? providerName = null, string? connectionName = null, string? databaseName = null)
    {
        var results = new List<QueryParamContext>();
        if (!Directory.Exists(_storageDir)) return results;

        foreach (var file in Directory.GetFiles(_storageDir, "*.json"))
        {
            try
            {
                using var stream = File.OpenRead(file);
                var context = await JsonSerializer.DeserializeAsync<QueryParamContext>(stream);
                if (context != null)
                {
                    // Filter by scope if provided
                    if (providerName != null && context.ProviderName != providerName) continue;
                    if (connectionName != null && context.ConnectionName != connectionName) continue;
                    // DatabaseName is often null for some providers, so be flexible

                    results.Add(context);
                }
            }
            catch
            {
                // Ignore corrupt files
            }
        }

        return results.OrderByDescending(x => x.LastUsedAt).ToList();
    }

    public async Task SaveAsync(QueryParamContext context)
    {
        context.LastUsedAt = DateTimeOffset.UtcNow;
        var fileName = $"{context.Id}.json";
        var filePath = Path.Combine(_storageDir, fileName);

        using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, context, new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task DeleteAsync(Guid id)
    {
        var fileName = $"{id}.json";
        var filePath = Path.Combine(_storageDir, fileName);
        if (File.Exists(filePath))
        {
            await Task.Run(() => File.Delete(filePath));
        }
    }

    public string GetFingerprint(string sql, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = new StringBuilder();
        var whitespace = false;
        for (var i = 0; i < sql.Length; i++)
        {
            SqlScanCancellation.Check(cancellationToken, i);
            if (char.IsWhiteSpace(sql[i])) { whitespace = normalized.Length > 0; continue; }
            if (whitespace) normalized.Append(' ');
            normalized.Append(sql[i]);
            whitespace = false;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return normalized.ToString().ToLowerInvariant();
    }
}
