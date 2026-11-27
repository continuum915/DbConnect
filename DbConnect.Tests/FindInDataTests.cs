using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using DbConnect.Views;
using Xunit;

namespace DbConnect.Tests;

public class FindInDataTests : MainWindowTestBase
{
    private readonly Mock<IDatabaseProvider> _mockProvider = new();
    private readonly DatabaseSession _session;

    public FindInDataTests()
    {
        var caps = new DatabaseProviderCapabilities("\"", "\"", "LIMIT")
        {
            ParameterPrefix = "@",
            SupportsSchemas = false
        };
        _mockProvider.Setup(p => p.ProviderName).Returns("SQLite");
        _mockProvider.Setup(p => p.Capabilities).Returns(caps);
        _mockProvider.Setup(p => p.FormatLimitQuery(It.IsAny<string>(), It.IsAny<int>()))
            .Returns<string, int>((q, l) => q + $" LIMIT {l}");

        _session = new DatabaseSession(new ConnectionInfo { Name = "TestDb", Provider = "SQLite" }, _mockProvider.Object);
    }

    [Theory]
    [InlineData("VARCHAR", true, true)]
    [InlineData("TEXT", true, true)]
    [InlineData("INTEGER", true, false)]
    [InlineData("INTEGER", false, true)]
    [InlineData("BLOB", true, false)]
    public void IsTextCompatible_ValidatesTypesCorrectly(string dataType, bool textOnly, bool expected)
    {
        // Act
        // FindInDataService has a private static helper IsTextCompatible, let's test it via reflection
        var method = typeof(FindInDataService).GetMethod("IsTextCompatible",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);
        var result = (bool)method.Invoke(null, new object[] { dataType, textOnly })!;

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task SearchAsync_FindsMatchesInTextColumns()
    {
        // Arrange
        var rootNodes = new List<SchemaNode>
        {
            new()
            {
                Name = "customers",
                RawName = "customers",
                Type = SchemaNodeType.Table,
                Children = new ObservableCollection<SchemaNode>
                {
                    new() { Name = "id", RawName = "id", DataType = "INTEGER", Type = SchemaNodeType.Column },
                    new() { Name = "email", RawName = "email", DataType = "VARCHAR(100)", Type = SchemaNodeType.Column }
                }
            }
        };

        _mockProvider.Setup(p => p.GetSchemaAsync(It.IsAny<string>()))
            .ReturnsAsync(rootNodes);

        var queryResult = new QueryResult
        {
            ColumnInfos = [new ColumnInfo { Name = "id", Ordinal = 0 }, new ColumnInfo { Name = "email", Ordinal = 1 }],
            ObjectRows =
            [
                new object?[] { 1, "alice@example.com" },
                new object?[] { 2, "bob@example.com" }
            ]
        };

        _mockProvider.Setup(p => p.ExecuteQueryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<QueryExecutionOptions>()))
            .ReturnsAsync(queryResult);

        var service = new FindInDataService();
        var progress = new Progress<FindInDataProgress>();
        var options = new FindInDataOptions(CaseSensitive: false, ExactMatch: false, TextColumnsOnly: true, RowLimitPerTable: 50);

        // Act
        var matches = new List<FindInDataMatch>();
        await foreach (var match in service.SearchAsync(_session, "alice", options, progress, CancellationToken.None))
        {
            matches.Add(match);
        }

        // Assert
        Assert.Single(matches);
        Assert.Equal("customers", matches[0].TableName);
        Assert.Equal("email", matches[0].ColumnName);
        Assert.Equal("alice@example.com", matches[0].Row?["email"]);
    }

    [Fact]
    public async Task SearchAsync_HandlesTableQueryErrorGracefully()
    {
        // Arrange
        var rootNodes = new List<SchemaNode>
        {
            new()
            {
                Name = "customers",
                RawName = "customers",
                Type = SchemaNodeType.Table,
                Children = new ObservableCollection<SchemaNode>
                {
                    new() { Name = "email", RawName = "email", DataType = "VARCHAR(100)", Type = SchemaNodeType.Column }
                }
            }
        };

        _mockProvider.Setup(p => p.GetSchemaAsync(It.IsAny<string>()))
            .ReturnsAsync(rootNodes);

        _mockProvider.Setup(p => p.ExecuteQueryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<QueryExecutionOptions>()))
            .ThrowsAsync(new Exception("Database locked"));

        var service = new FindInDataService();
        var progress = new Progress<FindInDataProgress>();
        var options = new FindInDataOptions();

        // Act
        var matches = new List<FindInDataMatch>();
        await foreach (var match in service.SearchAsync(_session, "alice", options, progress, CancellationToken.None))
        {
            matches.Add(match);
        }

        // Assert
        Assert.Single(matches);
        Assert.True(matches[0].IsError);
        Assert.Equal("customers", matches[0].TableName);
        Assert.Contains("Database locked", matches[0].ErrorMessage);
    }

    [Fact]
    public async Task SearchAsync_ContainsMode_EscapesLikeWildcardsInSql()
    {
        var rootNodes = new List<SchemaNode>
        {
            new()
            {
                Name = "products",
                RawName = "products",
                Type = SchemaNodeType.Table,
                Children = new ObservableCollection<SchemaNode>
                {
                    new() { Name = "name", RawName = "name", DataType = "VARCHAR(100)", Type = SchemaNodeType.Column }
                }
            }
        };

        _mockProvider.Setup(p => p.GetSchemaAsync(It.IsAny<string>()))
            .ReturnsAsync(rootNodes);

        string? capturedSql = null;
        _mockProvider.Setup(p => p.ExecuteQueryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<QueryExecutionOptions>()))
            .Callback<string, string, QueryExecutionOptions>((_, sql, _) => capturedSql = sql)
            .ReturnsAsync(new QueryResult { ColumnInfos = [new ColumnInfo { Name = "name", Ordinal = 0 }], ObjectRows = [] });

        var service = new FindInDataService();
        var options = new FindInDataOptions(ExactMatch: false);

        await foreach (var _ in service.SearchAsync(_session, "100%_done", options, new Progress<FindInDataProgress>(), CancellationToken.None))
        {
        }

        Assert.NotNull(capturedSql);
        Assert.Contains("100\\%\\_done", capturedSql);
        Assert.Contains("ESCAPE '\\'", capturedSql);
    }

    [Fact]
    public async Task ViewModel_SearchCommand_AddsErrorEntryToResults()
    {
        var rootNodes = new List<SchemaNode>
        {
            new()
            {
                Name = "customers",
                RawName = "customers",
                Type = SchemaNodeType.Table,
                Children = new ObservableCollection<SchemaNode>
                {
                    new() { Name = "email", RawName = "email", DataType = "VARCHAR(100)", Type = SchemaNodeType.Column }
                }
            }
        };

        _mockProvider.Setup(p => p.GetSchemaAsync(It.IsAny<string>()))
            .ReturnsAsync(rootNodes);

        _mockProvider.Setup(p => p.ExecuteQueryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<QueryExecutionOptions>()))
            .ThrowsAsync(new Exception("Database locked"));

        var mainVm = CreateMainWindowViewModel();
        var sessions = new ObservableCollection<DatabaseSession> { _session };
        var vm = new FindInDataViewModel(sessions, mainVm)
        {
            SelectedSession = _session,
            SearchTerm = "alice"
        };

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Single(vm.Results);
        Assert.True(vm.Results[0].IsError);
        Assert.Equal("customers", vm.Results[0].TableName);
        Assert.Contains("Database locked", vm.Results[0].ErrorMessage);
    }

    [Theory]
    [InlineData("100%", "100\\%")]
    [InlineData("a_b", "a\\_b")]
    [InlineData("path\\dir", "path\\\\dir")]
    public void EscapeLikePattern_EscapesWildcards(string input, string expectedFragment)
    {
        var escaped = FindInDataService.EscapeLikePattern(input);
        Assert.Contains(expectedFragment, escaped);
    }

    [Fact]
    public async Task ViewModel_SearchCommand_PopulatesResultsCorrectly()
    {
        // Arrange
        var rootNodes = new List<SchemaNode>
        {
            new()
            {
                Name = "customers",
                RawName = "customers",
                Type = SchemaNodeType.Table,
                Children = new ObservableCollection<SchemaNode>
                {
                    new() { Name = "email", RawName = "email", DataType = "VARCHAR(100)", Type = SchemaNodeType.Column }
                }
            }
        };

        _mockProvider.Setup(p => p.GetSchemaAsync(It.IsAny<string>()))
            .ReturnsAsync(rootNodes);

        var queryResult = new QueryResult
        {
            ColumnInfos = [new ColumnInfo { Name = "email", Ordinal = 0 }],
            ObjectRows = [new object?[] { "alice@example.com" }]
        };

        _mockProvider.Setup(p => p.ExecuteQueryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<QueryExecutionOptions>()))
            .ReturnsAsync(queryResult);

        var mainVm = CreateMainWindowViewModel();
        var sessions = new ObservableCollection<DatabaseSession> { _session };
        var vm = new FindInDataViewModel(sessions, mainVm)
        {
            SelectedSession = _session,
            SearchTerm = "alice"
        };

        // Act
        await vm.SearchCommand.ExecuteAsync(null);

        // Assert
        Assert.Single(vm.Results);
        var tableGroup = vm.Results[0];
        Assert.Equal("customers", tableGroup.TableName);
        Assert.Single(tableGroup.Columns);
        var colGroup = tableGroup.Columns[0];
        Assert.Equal("email", colGroup.ColumnName);
        Assert.Single(colGroup.Rows);
        Assert.Equal("alice@example.com", colGroup.Rows[0].Row["email"]);
    }

    [Fact]
    public void ViewLocator_ResolvesFindInDataViewModel()
    {
        var vmType = typeof(FindInDataViewModel);
        var viewTypeName = vmType.FullName!
            .Replace(".ViewModels.", ".Views.", StringComparison.Ordinal)
            .Replace("ViewModel", "View", StringComparison.Ordinal);

        var type = vmType.Assembly.GetType(viewTypeName);

        Assert.NotNull(type);
        Assert.Equal(typeof(FindInDataView), type);
    }
}
