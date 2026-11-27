namespace DbConnect.Services;

/// <summary>
/// How a script turns foreign-key checks off while it loads and restores them at its end, for engines that write
/// foreign keys inside CREATE TABLE: a dump of tables in a foreign-key cycle cannot otherwise load them.
/// <see cref="Caveat"/> is what the dump's header must tell whoever runs the script, if anything.
/// </summary>
public sealed record ForeignKeyCheckSwitch(string Off, string On, string? Caveat = null);
