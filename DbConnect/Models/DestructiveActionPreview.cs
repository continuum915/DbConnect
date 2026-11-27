namespace DbConnect.Models;

/// <summary>
/// The exact destructive action shown to the user before execution.
/// </summary>
public sealed record DestructiveActionPreview(
    string Action,
    string ObjectType,
    string ObjectName,
    string TargetConnection,
    string EnvironmentTier,
    string Sql);
