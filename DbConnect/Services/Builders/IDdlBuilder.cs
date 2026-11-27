using System.Collections.Generic;
using DbConnect.Models;

namespace DbConnect.Services.Builders;

/// <summary>
/// One comment to set or clear. <see cref="Column"/> is null for the table's own comment;
/// <see cref="Comment"/> is null to clear it; <see cref="Existed"/> says whether one was there.
/// </summary>
public sealed record CommentChange(string? Column, string? Comment, bool Existed);

public interface IDdlBuilder
{
    string ProviderName { get; }
    string Quote(string identifier);

    /// <summary>
    /// A builder that names tables (and, where the engine scopes them by schema, indexes) inside
    /// <paramref name="owner"/>: the schema, or the database on engines without schemas. Null
    /// keeps names unqualified. Throws <see cref="System.NotSupportedException"/> when the
    /// engine's change path cannot address a table there.
    /// </summary>
    IDdlBuilder InSchema(string? owner);

    string GenerateCreateTable(string tableName, IList<ColumnDefinition> columns);
    // FK-aware overload: foreign keys are declared inside CREATE TABLE.
    string GenerateCreateTable(string tableName, IList<ColumnDefinition> columns, IList<ForeignKeyDefinition> foreignKeys);

    /// <summary>
    /// CREATE TABLE with its primary and unique keys in their member order, its foreign keys with their
    /// ordered column pairs and qualified referenced tables, and its checks.
    /// </summary>
    string GenerateCreateTable(string tableName, IList<ColumnDefinition> columns, TableConstraints constraints);
    // indexes and the stored definition preserve SQLite rebuild semantics; ignored by other providers.
    string GenerateAlterTable(string tableName, IList<ColumnDefinition> original, IList<ColumnDefinition> modified, IList<IndexDefinition>? indexes = null, TableRebuildDefinition? definition = null);
    string GenerateCreateIndex(IndexDefinition index);
    string GenerateDropIndex(string indexName, string? tableName = null);
    string GenerateCreateView(string viewName, string selectSql, bool createOrReplace = false);
    string GenerateAddUniqueConstraint(string tableName, string constraintName, IList<string> columns);
    // Returns a comment line (starting with "--") when the engine cannot add one to an existing table.
    string GenerateAddForeignKey(ForeignKeyDefinition fk);
    string GenerateDropConstraint(string tableName, string constraintName);

    /// <summary>True when a column's comment is part of its definition (MySQL), so changing it restates the column.</summary>
    bool ColumnCommentsInDefinition => false;

    /// <summary>Statements that set or clear comments; column comments are skipped when <see cref="ColumnCommentsInDefinition"/>.</summary>
    IReadOnlyList<string> GenerateCommentChanges(string tableName, IReadOnlyList<CommentChange> changes) => [];

    /// <summary>
    /// Why altering <paramref name="original"/> into <paramref name="modified"/> would lose part of
    /// the column as loaded (its collation, generation, identity or engine properties), or null
    /// when the alter keeps everything it does not change.
    /// </summary>
    string? AlterColumnLoss(ColumnDefinition original, ColumnDefinition modified, TableDesignColumn loaded) => null;

    /// <summary>Which modifiers and collations the engine's types take, for splitting loaded types and validating edits.</summary>
    ColumnTypeRules TypeRules => ColumnTypeRules.Unknown(ProviderName);

    /// <summary>How the engine names, renames and replaces primary and unique keys.</summary>
    TableKeyRules KeyRules => TableKeyRules.None(ProviderName);

    /// <summary>
    /// Statements that add (no From), drop (no To), rename (same kind and columns) or replace a key on an
    /// existing table; a comment line (starting with "--") when the engine cannot make the change.
    /// </summary>
    IReadOnlyList<string> GenerateKeyChange(string tableName, TableKeyChange change) =>
        [$"-- {ProviderName} keys cannot be changed on an existing table in the designer."];

    /// <summary>How the engine renames and replaces check constraints.</summary>
    TableCheckRules CheckRules => TableCheckRules.None(ProviderName);

    /// <summary>
    /// Statements that add (no From), drop (no To), rename or replace a check on an existing table;
    /// a comment line (starting with "--") when the engine cannot make the change.
    /// </summary>
    IReadOnlyList<string> GenerateCheckChange(string tableName, TableCheckChange change) =>
        [$"-- {ProviderName} checks cannot be changed on an existing table in the designer."];

    /// <summary>The referential actions the engine accepts and how it renames and replaces foreign keys.</summary>
    TableForeignKeyRules ForeignKeyRules => TableForeignKeyRules.None(ProviderName);

    /// <summary>
    /// Statements that add (no From), drop (no To), rename or replace a foreign key on an existing table;
    /// a comment line (starting with "--") when the engine cannot make the change.
    /// </summary>
    IReadOnlyList<string> GenerateForeignKeyChange(string tableName, TableForeignKeyChange change) =>
        [$"-- {ProviderName} foreign keys cannot be changed on an existing table in the designer."];

    /// <summary>How the engine renames and re-creates indexes, and which index properties it restates.</summary>
    TableIndexRules IndexRules => TableIndexRules.None(ProviderName);

    /// <summary>
    /// Statements that create (no From), drop (no To), rename or re-create an independent index of the table;
    /// a comment line (starting with "--") when the engine cannot make the change.
    /// </summary>
    IReadOnlyList<string> GenerateIndexChange(string tableName, TableIndexChange change) =>
        [$"-- {ProviderName} indexes cannot be changed in the designer."];

    /// <summary>Which identity and generation settings the engine has and which of them its ALTER can change.</summary>
    ColumnGenerationRules GenerationRules => ColumnGenerationRules.None(ProviderName);
}
