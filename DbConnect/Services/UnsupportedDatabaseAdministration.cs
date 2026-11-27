namespace DbConnect.Services;

/// <summary>Uses the administration interface defaults for providers without user management.</summary>
internal sealed class UnsupportedDatabaseAdministration : IDatabaseAdministrationProvider
{
    internal static readonly IDatabaseAdministrationProvider Instance = new UnsupportedDatabaseAdministration();
}
