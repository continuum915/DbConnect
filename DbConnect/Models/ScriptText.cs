namespace DbConnect.Models;

/// <summary>SQL held as a range of the script it came from. The text is only copied when it is read.</summary>
public readonly struct ScriptText
{
    private readonly string? _script;
    private readonly int _start;
    private readonly int _length;

    public ScriptText(string script) : this(script, 0, script.Length) { }

    public ScriptText(string script, int start, int length)
    {
        _script = script;
        _start = start;
        _length = length;
    }

    public override string ToString() => _script?.Substring(_start, _length) ?? string.Empty;
}
