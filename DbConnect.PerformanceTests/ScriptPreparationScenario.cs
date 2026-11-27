using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.PerformanceTests;

public sealed record PreparationObservations(string Dialect, int ExpectedInserts, int GoBatches,
    int? ParsedStatements, int? SpanCount, int? DetectedParameters, string[] ExecutionUnitDiscrepancies);

public sealed class ScriptPreparationScenario
{
    public static readonly string[] Ids = ["prepare.parameters", "prepare.split", "prepare.spans",
        "prepare.execution", "prepare.highlighter"];
    public static readonly string[] ParameterIds = ["parameter.detect", "parameter.substitute"];
    private readonly string _id;
    private readonly ImportFixture _fixture;
    private readonly QueryTabViewModel? _tab;
    private IReadOnlyList<string>? _statements;
    private IReadOnlyList<StatementSpan>? _spans;
    private List<DetectedParameter>? _parameters;
    private RunningStatementHighlighter? _highlighter;
    private SqlSubstitution? _substitution;

    public ScriptPreparationScenario(string id, ImportFixture fixture)
    {
        if (!Ids.Contains(id) && !ParameterIds.Contains(id))
            throw new ArgumentException("Unknown preparation scenario.", nameof(id));
        if (ParameterIds.Contains(id) != (fixture.Metadata.Profile == "actual-parameters"))
            throw new ArgumentException("Actual parameters require a dedicated scenario/profile.");
        _id = id;
        _fixture = fixture;
        if (id == "prepare.highlighter")
            _tab = new QueryTabViewModel { QueryText = fixture.Sql };
    }

    public void Execute()
    {
        var sql = _fixture.Sql;
        if (_id == "parameter.substitute")
            _substitution = SqlParameterDetector.SubstituteWithOffsets(sql,
                [new QueryParamEntry { Placeholder = "@value", Value = "D'Angelo", DataType = "string" }]);
        if (_id is "prepare.parameters" or "prepare.execution" or "parameter.detect")
            _parameters = SqlParameterDetector.Detect(sql);
        if (_id is "prepare.split" or "prepare.execution")
            _statements = SqlBatchParser.SplitStatements(sql, SqlBatchDialect.SqlServer);
        if (_id is "prepare.spans" or "prepare.execution")
            _spans = SqlBatchParser.GetStatementSpans(sql, SqlBatchDialect.SqlServer);
        if (_id == "prepare.highlighter")
            _highlighter = new(_tab!, sql, 0, SqlBatchDialect.SqlServer);
    }

    public PreparationObservations Validate()
    {
        if (_id == "parameter.detect" && (_parameters?.Count != 1 || _parameters[0].Placeholder != "@value"))
            throw new InvalidDataException("Actual parameter detection differs from the generator oracle.");
        if (_substitution is not null) ParameterValidation.Validate(_fixture, _substitution);
        if (!ParameterIds.Contains(_id) && _parameters is { Count: > 0 })
            throw new InvalidDataException("The representative import contains no real parameters.");
        // Parse only outside timing for scenarios that do not return spans themselves.
        var spans = _spans ?? (_statements is not null || _highlighter is not null
            ? SqlBatchParser.GetStatementSpans(_fixture.Sql, SqlBatchDialect.SqlServer) : null);
        if (spans is not null)
        {
            if (_statements is not null && _statements.Count != spans.Count)
                throw new InvalidDataException("Split/span counts disagree.");
            var insertIndex = 0;
            for (var index = 0; index < spans.Count; index++)
            {
                var span = spans[index];
                if (span.Start < 0 || span.Length <= 0 || span.End > _fixture.Sql.Length ||
                    (index > 0 && span.Start < spans[index - 1].End))
                    throw new InvalidDataException("Invalid or unordered statement range.");
                if (_statements is not null && !_fixture.Sql.AsSpan(span.Start, span.Length).SequenceEqual(_statements[index]))
                    throw new InvalidDataException("Split text differs from source span.");
                if (_highlighter is not null)
                {
                    _highlighter.ShowAsync(index).GetAwaiter().GetResult();
                    if (_tab!.RunningStatementSpan != span)
                        throw new InvalidDataException("Highlighter did not expose the parsed range.");
                }
                while (insertIndex < _fixture.Expected.Count && _fixture.Expected[insertIndex].Start < span.End)
                {
                    var expected = _fixture.Expected[insertIndex++];
                    if (expected.Start < span.Start || expected.Start + expected.Length > span.End)
                        throw new InvalidDataException("An independently generated INSERT range was split or lost.");
                }
            }
            if (insertIndex != _fixture.Expected.Count)
                throw new InvalidDataException("Preparation omitted INSERTs.");
        }
        var discrepancies = new List<string>();
        if (spans is not null && spans.Count != _fixture.Metadata.GoDirectives)
            discrepancies.Add($"SQL Server parser exposes {spans.Count} units versus {_fixture.Metadata.GoDirectives} GO-terminated batches; semicolons split within batches.");
        if (spans is not null && spans.Count != _fixture.Metadata.Inserts)
            discrepancies.Add($"Parser units ({spans.Count}) differ from executable INSERT count ({_fixture.Metadata.Inserts}); trailing comment padding can be returned as a unit.");
        return new("SqlServer", _fixture.Metadata.Inserts, _fixture.Metadata.GoDirectives,
            _statements?.Count, spans?.Count, _parameters?.Count, discrepancies.ToArray());
    }
}
