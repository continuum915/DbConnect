using System;
using System.Buffers;
using System.IO;
using System.Threading;

namespace DbConnect.Services;

/// <summary>An immutable text revision whose string is materialized once, when a consumer requests it.</summary>
public sealed class QueryTextSnapshot
{
    private readonly Lazy<string> _text;
    private Func<TextReader>? _openReader;
    public int Length { get; }
    public string Text => _text.Value;

    public bool TryGetMaterializedText(out string? text)
    {
        text = _text.IsValueCreated ? _text.Value : null;
        return text != null;
    }

    /// <summary>Factories must represent one immutable revision and support background readers.</summary>
    public QueryTextSnapshot(int length, Func<string> materialize, Func<TextReader> openReader)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Length = length;
        _openReader = openReader;
        _text = new Lazy<string>(() =>
        {
            var text = materialize();
            // Once cached, the string replaces the rope snapshot rather than retaining both after close.
            Volatile.Write(ref _openReader, null);
            return text;
        });
    }

    public static QueryTextSnapshot FromString(string text)
    {
        var snapshot = new QueryTextSnapshot(text.Length, () => text, () => new StringReader(text));
        // The supplied string already exists. Publish that fact to weak reuse without another read.
        _ = snapshot.Text;
        return snapshot;
    }

    public bool ContentEquals(QueryTextSnapshot other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (Length != other.Length) return false;
        if (_text.IsValueCreated && other._text.IsValueCreated)
            return string.Equals(Text, other.Text, StringComparison.Ordinal);
        using var left = OpenReader();
        using var right = other.OpenReader();
        var a = ArrayPool<char>.Shared.Rent(4096);
        var b = ArrayPool<char>.Shared.Rent(4096);
        try
        {
            while (true)
            {
                var count = left.ReadBlock(a, 0, 4096);
                if (count != right.ReadBlock(b, 0, 4096) || !a.AsSpan(0, count).SequenceEqual(b.AsSpan(0, count)))
                    return false;
                if (count == 0) return true;
            }
        }
        finally { ArrayPool<char>.Shared.Return(a); ArrayPool<char>.Shared.Return(b); }
    }

    private TextReader OpenReader()
    {
        var factory = Volatile.Read(ref _openReader);
        return factory != null ? factory() : new StringReader(Text);
    }
}
