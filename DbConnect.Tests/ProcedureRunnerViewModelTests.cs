using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using Xunit;

namespace DbConnect.Tests;

public class ProcedureRunnerViewModelTests
{
    private readonly Mock<IDatabaseProvider> _mockProvider = new();
    private readonly DatabaseSession _session;

    public ProcedureRunnerViewModelTests()
    {
        _session = new DatabaseSession(new ConnectionInfo { Name = "Test" }, _mockProvider.Object);
    }

    [Fact]
    public async Task InitializeAsync_UsesDetectedParametersAndFiltersOutputs()
    {
        var detectedParameters = new List<ProcedureParameter>
        {
            new() { Name = "p1", DataType = "int", Direction = ParameterDirection.In, DefaultValue = "5", OrdinalPosition = 1 },
            new() { Name = "p2", DataType = "text", Direction = ParameterDirection.Out, OrdinalPosition = 2 }
        };

        _mockProvider
            .Setup(p => p.GetProcedureParametersAsync(It.IsAny<string>(), "public", "my_proc", It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var vm = new ProcedureRunnerViewModel(_session, "public", "my_proc", SchemaNodeType.Procedure, "public.my_proc", detectedParameters);

        await vm.InitializeAsync();

        Assert.Single(vm.Parameters);
        Assert.Equal("p1", vm.Parameters[0].Name);
        Assert.Equal("5", vm.Parameters[0].Value);
        Assert.Contains("Loaded 1 input parameter", vm.StatusMessage);
    }

    [Fact]
    public async Task ExecuteAsync_PopulatesResultsAndConvertsTypedValues()
    {
        var parameters = new List<ProcedureParameter>
        {
            new() { Name = "@p1", DataType = "int", Direction = ParameterDirection.In, OrdinalPosition = 1 },
            new() { Name = "@p2", DataType = "nvarchar", Direction = ParameterDirection.Out, OrdinalPosition = 2 }
        };
        IReadOnlyList<DbParameterValue>? capturedValues = null;

        _mockProvider
            .Setup(p => p.GetProcedureParametersAsync(It.IsAny<string>(), "public", "my_proc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(parameters);

        var executionResult = new RoutineExecutionResult
        {
            ResultSets = new List<QueryResult> { new QueryResult { TabLabel = "Res 1" } },
            Messages = new List<string> { "Hello" },
            OutputParameters = new List<(string, object?)> { ("@p2", "secret") }
        };

        _mockProvider
            .Setup(p => p.ExecuteRoutineAsync(
                It.IsAny<string>(),
                "public",
                "my_proc",
                "public.my_proc",
                SchemaNodeType.Procedure,
                It.IsAny<IReadOnlyList<DbParameterValue>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, SchemaNodeType, IReadOnlyList<DbParameterValue>, CancellationToken>((_, _, _, _, _, values, _) => capturedValues = values)
            .ReturnsAsync(executionResult);

        var vm = new ProcedureRunnerViewModel(_session, "public", "my_proc", SchemaNodeType.Procedure, "public.my_proc", parameters);
        await vm.InitializeAsync();
        vm.Parameters[0].Value = "123";

        await vm.ExecuteCommand.ExecuteAsync(null);

        Assert.NotNull(capturedValues);
        Assert.Equal(2, capturedValues!.Count);
        Assert.Equal(123, capturedValues[0].Value);
        Assert.Null(capturedValues[1].Value);
        Assert.Single(vm.ResultSets);
        Assert.Single(vm.Messages);
        Assert.Equal("Hello", vm.Messages[0]);
        Assert.Single(vm.OutputParameters);
        Assert.Equal("@p2", vm.OutputParameters[0].Name);
        Assert.Equal("secret", vm.OutputParameters[0].Value);
        Assert.Contains("completed successfully", vm.StatusMessage);
    }

    [Fact]
    public async Task ExecuteAsync_WithNullDataType_PassesRawValueThrough()
    {
        var parameters = new List<ProcedureParameter>
        {
            new() { Name = "p1", DataType = null!, Direction = ParameterDirection.In, OrdinalPosition = 1 }
        };
        IReadOnlyList<DbParameterValue>? capturedValues = null;

        _mockProvider
            .Setup(p => p.GetProcedureParametersAsync(It.IsAny<string>(), "public", "my_proc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(parameters);
        _mockProvider
            .Setup(p => p.ExecuteRoutineAsync(
                It.IsAny<string>(),
                "public",
                "my_proc",
                "public.my_proc",
                SchemaNodeType.Procedure,
                It.IsAny<IReadOnlyList<DbParameterValue>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, SchemaNodeType, IReadOnlyList<DbParameterValue>, CancellationToken>((_, _, _, _, _, values, _) => capturedValues = values)
            .ReturnsAsync(new RoutineExecutionResult());

        var vm = new ProcedureRunnerViewModel(_session, "public", "my_proc", SchemaNodeType.Procedure, "public.my_proc", parameters);
        await vm.InitializeAsync();
        vm.Parameters[0].Value = "123";

        await vm.ExecuteCommand.ExecuteAsync(null);

        Assert.NotNull(capturedValues);
        Assert.Equal("123", capturedValues![0].Value);
        Assert.Contains("completed successfully", vm.StatusMessage);
    }

    [Fact]
    public async Task ExecuteAsync_HandlesErrors()
    {
        _mockProvider
            .Setup(p => p.GetProcedureParametersAsync(It.IsAny<string>(), "public", "my_proc", It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockProvider
            .Setup(p => p.ExecuteRoutineAsync(
                It.IsAny<string>(),
                "public",
                "my_proc",
                "public.my_proc",
                SchemaNodeType.Procedure,
                It.IsAny<IReadOnlyList<DbParameterValue>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Exception("Kaboom"));

        var vm = new ProcedureRunnerViewModel(_session, "public", "my_proc", SchemaNodeType.Procedure, "public.my_proc");

        await vm.ExecuteCommand.ExecuteAsync(null);

        Assert.Contains("Execution failed: Kaboom", vm.StatusMessage);
        Assert.Single(vm.Messages);
        Assert.Equal("[Error] Kaboom", vm.Messages[0]);
    }
}
