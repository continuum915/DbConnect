namespace DbConnect.PerformanceTests;

public sealed record ImportConfiguration(int PairsPerBatch, int LiteralPadding, bool ActualParameters)
{
    public static readonly string[] Profiles = ["representative", "many-small-batches", "few-large-batches", "long-lines", "actual-parameters"];

    public static ImportConfiguration For(string profile) => profile switch
    {
        "representative" => new(32, 0, false),
        "many-small-batches" => new(1, 0, false),
        "few-large-batches" => new(512, 0, false),
        "long-lines" => new(32, 4096, false),
        "actual-parameters" => new(32, 0, true),
        _ => throw new ArgumentException("Unknown import profile.", nameof(profile))
    };
}
