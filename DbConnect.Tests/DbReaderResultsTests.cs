using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;
using DbConnect.Services;
using Xunit;

namespace DbConnect.Tests;

/// <summary>
/// RAIN-P2-15 — how <see cref="DbReaderResults"/> turns a driver's column schema into <see cref="ColumnInfo"/>.
/// The fake reader stands in for SQL Server's browse mode, which only the Docker lane runs for real.
/// </summary>
public class DbReaderResultsTests
{
    [Fact]
    public async Task HiddenColumns_AreLeftOutOfColumnsAndRows()
    {
        // Browse mode appends the key a query left out as a hidden column.
        using var reader = new SchemaReader(
            [new FakeColumn("name", "items", null), new FakeColumn("id", "items", null, isHidden: true)],
            ["Ada", 1]);
        var result = new QueryResult();

        await DbReaderResults.ReadAsync(reader, result, null);

        var column = Assert.Single(result.ColumnInfos);
        Assert.Equal("name", column.Name);
        Assert.Equal(0, column.Ordinal);
        Assert.Equal(["Ada"], Assert.Single(result.ObjectRows));
    }

    [Fact]
    public async Task HiddenColumns_BeforeVisibleOnes_KeepValuesAlignedWithColumns()
    {
        using var reader = new SchemaReader(
            [new FakeColumn("id", "items", null, isHidden: true), new FakeColumn("name", "items", null), new FakeColumn("amount", "items", null)],
            [1, "Ada", 10]);

        var result = Assert.Single(await DbReaderResults.ReadAllAsync(reader, null));

        Assert.Equal(["name", "amount"], result.ColumnInfos.Select(c => c.Name));
        Assert.Equal([0, 1], result.ColumnInfos.Select(c => c.Ordinal));
        Assert.Equal(["Ada", 10], Assert.Single(result.ObjectRows));
    }

    [Fact]
    public async Task ColumnFromKnownTable_WithoutBaseColumnName_IsItsOwnSourceColumn()
    {
        // SQL Server sends a base column name only for an aliased column.
        using var reader = new SchemaReader(
            [new FakeColumn("id", "items", ""), new FakeColumn("label", "items", "name")],
            [1, "Ada"]);
        var result = new QueryResult();

        await DbReaderResults.ReadAsync(reader, result, null);

        Assert.Equal(("items", "id"), (result.ColumnInfos[0].SourceTable, result.ColumnInfos[0].SourceColumn));
        Assert.Equal(("items", "name"), (result.ColumnInfos[1].SourceTable, result.ColumnInfos[1].SourceColumn));
    }

    [Fact]
    public async Task Expression_WithEmptySourceNames_ReportsNoSource()
    {
        using var reader = new SchemaReader([new FakeColumn("bumped", "", "bumped")], [11]);
        var result = new QueryResult();

        await DbReaderResults.ReadAsync(reader, result, null);

        var column = Assert.Single(result.ColumnInfos);
        Assert.Null(column.SourceTable);
        Assert.Null(column.SourceColumn);
    }

    [Fact]
    public async Task ColumnSchemaFailure_FallsBackToReaderFields()
    {
        using var reader = new SchemaReader([new FakeColumn("id", "items", "id")], [1], schemaFails: true);
        var result = new QueryResult();

        await DbReaderResults.ReadAsync(reader, result, null);

        var column = Assert.Single(result.ColumnInfos);
        Assert.Equal("id", column.Name);
        Assert.Null(column.SourceTable);
        Assert.Equal([1], Assert.Single(result.ObjectRows));
    }

    private sealed class FakeColumn : DbColumn
    {
        public FakeColumn(string name, string? baseTable, string? baseColumn, bool isHidden = false)
        {
            ColumnName = name;
            BaseTableName = baseTable;
            BaseColumnName = baseColumn;
            IsHidden = isHidden;
            DataType = typeof(object);
        }
    }

    /// <summary>A one-row reader over <paramref name="values"/> that reports <paramref name="columns"/> as its schema.</summary>
    private sealed class SchemaReader(FakeColumn[] columns, object[] values, bool schemaFails = false) : DbDataReader, IDbColumnSchemaGenerator
    {
        private bool _read;

        public ReadOnlyCollection<DbColumn> GetColumnSchema() =>
            schemaFails ? throw new InvalidOperationException("schema lookup failed") : new ReadOnlyCollection<DbColumn>(columns);

        public override bool Read() => !_read && (_read = true);
        public override bool NextResult() => false;
        public override int FieldCount => columns.Length;
        public override string GetName(int ordinal) => columns[ordinal].ColumnName;
        public override object GetValue(int ordinal) => values[ordinal];
        public override string GetDataTypeName(int ordinal) => "object";
        public override Type GetFieldType(int ordinal) => typeof(object);
        public override int RecordsAffected => -1;
        public override bool HasRows => true;
        public override bool IsClosed => false;
        public override int Depth => 0;
        public override object this[int ordinal] => values[ordinal];
        public override object this[string name] => values[GetOrdinal(name)];
        public override int GetOrdinal(string name) => Array.FindIndex(columns, c => c.ColumnName == name);
        public override bool IsDBNull(int ordinal) => values[ordinal] is null or DBNull;
        public override int GetValues(object[] target) { values.CopyTo(target, 0); return values.Length; }
        public override IEnumerator GetEnumerator() => throw new NotSupportedException();
        public override bool GetBoolean(int ordinal) => throw new NotSupportedException();
        public override byte GetByte(int ordinal) => throw new NotSupportedException();
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override char GetChar(int ordinal) => throw new NotSupportedException();
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override DateTime GetDateTime(int ordinal) => throw new NotSupportedException();
        public override decimal GetDecimal(int ordinal) => throw new NotSupportedException();
        public override double GetDouble(int ordinal) => throw new NotSupportedException();
        public override float GetFloat(int ordinal) => throw new NotSupportedException();
        public override Guid GetGuid(int ordinal) => throw new NotSupportedException();
        public override short GetInt16(int ordinal) => throw new NotSupportedException();
        public override int GetInt32(int ordinal) => throw new NotSupportedException();
        public override long GetInt64(int ordinal) => throw new NotSupportedException();
        public override string GetString(int ordinal) => throw new NotSupportedException();
    }
}
