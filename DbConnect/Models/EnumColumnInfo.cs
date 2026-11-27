using System.Collections.Generic;

namespace DbConnect.Models;

/// <summary>
/// RAIN-P2-09 — The allowed values for one ENUM/SET column, as reported by the provider's
/// own metadata (MySQL's <c>information_schema.COLUMNS.COLUMN_TYPE</c>, PostgreSQL's
/// <c>pg_enum</c>). <see cref="AllowsMultipleValues"/> distinguishes a MySQL SET (a column can
/// hold several members at once, comma-joined) from an ENUM/native Postgres enum (exactly one
/// member).
/// </summary>
public sealed record EnumColumnInfo(IReadOnlyList<string> Values, bool AllowsMultipleValues);
