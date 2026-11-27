using System.Collections.Generic;
using DbConnect.Models;
using DbConnect.Services.Builders;

namespace DbConnect.Services;

public static class DdlGenerator
{
    public static string GenerateCreateTable(string tableName, IList<ColumnDefinition> columns, string provider) =>
        DdlBuilderFactory.For(provider).GenerateCreateTable(tableName, columns);

    public static string GenerateAlterTable(string tableName, IList<ColumnDefinition> original, IList<ColumnDefinition> modified, string provider) =>
        DdlBuilderFactory.For(provider).GenerateAlterTable(tableName, original, modified);

    public static string GenerateCreateIndex(IndexDefinition index, string provider) =>
        DdlBuilderFactory.For(provider).GenerateCreateIndex(index);

    public static string GenerateDropIndex(string indexName, string provider) =>
        DdlBuilderFactory.For(provider).GenerateDropIndex(indexName);

    public static string GenerateCreateView(string viewName, string selectSql, string provider) =>
        DdlBuilderFactory.For(provider).GenerateCreateView(viewName, selectSql);

    public static string Quote(string identifier, string provider) =>
        DdlBuilderFactory.For(provider).Quote(identifier);

    public static string Quote(string identifier, DatabaseProviderCapabilities capabilities) =>
        capabilities.QuoteIdentifier(identifier);
}
