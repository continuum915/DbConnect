namespace DbConnect.Models;

public sealed class QueryErrorInfo
{
    public string Provider { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public string? Code { get; set; }
    public bool IsTimeout { get; set; }
    public bool IsCanceled { get; set; }
    /// <summary>Location within the SQL sent to the driver. Offset is zero based; line is one based.</summary>
    public QueryErrorPosition? Position { get; set; }

    /// <summary>An error DbConnect reports about an operation it attempted, as opposed to one the database returned for a statement.</summary>
    public static QueryErrorInfo Failure(string provider, string summary, string? detail) => new()
    {
        Provider = provider,
        Summary = summary,
        Detail = detail
    };

    public string ToDisplaySummary()
    {
        var status = IsCanceled
            ? "Cancelled"
            : IsTimeout
                ? "Timed out"
                : "Failed";
        var code = string.IsNullOrWhiteSpace(Code) ? string.Empty : $" ({Code})";
        var detail = string.IsNullOrWhiteSpace(Detail) || Detail == Summary ? string.Empty : $" {Detail}";
        return $"{status}{code}: {Summary}{detail}";
    }
}
