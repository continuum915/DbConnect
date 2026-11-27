using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Optional session capability; only results executed on this session can be captured.</summary>
public interface IQueryExportSession
{
    QueryExportCapture? CaptureQueryExport(QueryResult result);
}

public sealed record QueryExportCapture(IExportSource Source, string ResultIdentity, System.Func<bool> IsCurrent);
