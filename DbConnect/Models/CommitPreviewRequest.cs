namespace DbConnect.Models;

public sealed record CommitPreviewRequest(string SqlPreview, string SummaryMessage);
