using System;

namespace DbConnect.Models;

public enum ParameterPlaceholderStyle
{
    NamedAt,      // @name (SQL Server, SQLite)
    NamedColon,   // :name (SQLite, Oracle)
    PositionalDollar, // $1, $2 (PostgreSQL)
    PositionalQuestion, // ?, ?1 (MySQL, SQLite, DuckDB)
}

public record DetectedParameter(
    string Placeholder,
    int? OrdinalIndex,
    ParameterPlaceholderStyle Style,
    string ProviderHint);
