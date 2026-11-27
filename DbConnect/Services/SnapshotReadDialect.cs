using System.Collections.Generic;

namespace DbConnect.Services;

/// <summary>
/// How a dump reads every table's data from one consistent snapshot on one session: <see cref="Begin"/> opens a
/// read-only snapshot transaction and <see cref="End"/> closes it. When <see cref="Probe"/> is set, it must return 1
/// first (SQL Server: snapshot isolation is enabled for the database); otherwise the dump reads without a snapshot.
/// <see cref="Description"/> names the mechanism in the dump's header.
/// </summary>
public sealed record SnapshotReadDialect(string Description, IReadOnlyList<string> Begin, IReadOnlyList<string> End, string? Probe = null);
