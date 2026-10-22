using System.Text;

namespace Starlark.Syntax;

/// <summary>
/// A half-open range <c>[Begin, End)</c> of UTF-16 code unit offsets into a source file.
/// (The Rust original uses UTF-8 byte offsets; columns are still reported in characters.)
/// </summary>
public readonly record struct Span
{
    public int Begin { get; }
    public int End { get; }

    public Span(int begin, int end)
    {
        if (end < begin)
        {
            throw new ArgumentException($"Span end {end} is before begin {begin}");
        }
        Begin = begin;
        End = end;
    }

    public int Length => End - Begin;

    /// <summary>Smallest span enclosing both spans.</summary>
    public Span Merge(Span other) => new(Math.Min(Begin, other.Begin), Math.Max(End, other.End));

    public static Span MergeAll(IEnumerable<Span> spans)
    {
        bool any = false;
        Span acc = default;
        foreach (var s in spans)
        {
            acc = any ? acc.Merge(s) : s;
            any = true;
        }
        return acc;
    }

    /// <summary>Empty span at the end of this span.</summary>
    public Span EndSpan => new(End, End);

    public bool Contains(int pos) => Begin <= pos && pos <= End;

    /// <summary>Whether the spans intersect. Ends are inclusive.</summary>
    public bool Intersects(Span other) => Contains(other.Begin) || Contains(other.End) || other.Contains(Begin);
}

/// <summary>A value with the span it came from.</summary>
public readonly record struct Spanned<T>(T Node, Span Span)
{
    public Spanned<U> Map<U>(Func<T, U> f) => new(f(Node), Span);
}

/// <summary>Line (0-based) and column (0-based, in Unicode scalar values). Prints 1-based.</summary>
public readonly record struct ResolvedPos(int Line, int Column) : IComparable<ResolvedPos>
{
    public int CompareTo(ResolvedPos other) => Line != other.Line ? Line.CompareTo(other.Line) : Column.CompareTo(other.Column);

    public override string ToString() => $"{Line + 1}:{Column + 1}";
}

public readonly record struct ResolvedSpan(ResolvedPos Begin, ResolvedPos End)
{
    public bool Contains(ResolvedPos pos) => Begin.CompareTo(pos) <= 0 && pos.CompareTo(End) < 0;

    public override string ToString()
    {
        bool singleLine = Begin.Line == End.Line;
        bool isEmpty = singleLine && Begin.Column == End.Column;
        if (isEmpty)
        {
            return $"{Begin.Line + 1}:{Begin.Column + 1}";
        }
        if (singleLine)
        {
            return $"{Begin}-{End.Column + 1}";
        }
        return $"{Begin}-{End}";
    }
}

/// <summary>A source file, with a table of line starts for resolving positions.</summary>
public sealed class CodeMap
{
    private readonly int[] _lines;

    public string FileName { get; }
    public string Source { get; }

    public CodeMap(string fileName, string source)
    {
        FileName = fileName;
        Source = source;
        var lines = new List<int> { 0 };
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == '\n')
            {
                lines.Add(i + 1);
            }
        }
        _lines = lines.ToArray();
    }

    public int LineCount => _lines.Length;

    public Span FullSpan => new(0, Source.Length);

    public FileSpan FileSpan(Span span) => new(this, span);

    private int Clamp(int pos) => Math.Clamp(pos, 0, Source.Length);

    /// <summary>0-based line containing <paramref name="pos"/>.</summary>
    public int FindLine(int pos)
    {
        pos = Clamp(pos);
        int i = Array.BinarySearch(_lines, pos);
        return i >= 0 ? i : ~i - 1;
    }

    public ResolvedPos ResolvePos(int pos)
    {
        pos = Clamp(pos);
        int line = FindLine(pos);
        var lineSpan = LineSpan(line);
        int col = pos - lineSpan.Begin;
        // Don't split a surrogate pair.
        if (col > 0 && col < lineSpan.Length && char.IsLowSurrogate(Source[lineSpan.Begin + col]))
        {
            col--;
        }
        return new ResolvedPos(line, CountScalars(Source, lineSpan.Begin, col));
    }

    public ResolvedSpan ResolveSpan(Span span) => new(ResolvePos(span.Begin), ResolvePos(span.End));

    /// <summary>Number of Unicode scalar values in <c>s[start..start+len]</c>.</summary>
    public static int CountScalars(string s, int start, int len)
    {
        int n = 0;
        for (int i = start; i < start + len; i++)
        {
            if (!char.IsLowSurrogate(s[i]) || i == start)
            {
                n++;
            }
        }
        return n;
    }

    public string SourceSpan(Span span) => Source.Substring(Clamp(span.Begin), Clamp(span.End) - Clamp(span.Begin));

    /// <summary>Span of a line, including its terminator. Throws if out of range.</summary>
    public Span LineSpan(int line) =>
        LineSpanOpt(line) ?? throw new ArgumentOutOfRangeException(nameof(line), $"Line {line} is out of range for {FileName}");

    public Span? LineSpanOpt(int line)
    {
        if (line < 0 || line >= _lines.Length)
        {
            return null;
        }
        int end = line + 1 < _lines.Length ? _lines[line + 1] : Source.Length;
        return new Span(_lines[line], end);
    }

    /// <summary>Line span without trailing <c>\n</c> or <c>\r\n</c>.</summary>
    public Span LineSpanTrimNewline(int line)
    {
        var span = LineSpan(line);
        int end = span.End;
        if (end > span.Begin && Source[end - 1] == '\n')
        {
            end--;
        }
        if (end > span.Begin && Source[end - 1] == '\r')
        {
            end--;
        }
        return new Span(span.Begin, end);
    }

    /// <summary>Text of a line without its terminator.</summary>
    public string SourceLine(int line) => SourceSpan(LineSpan(line)).TrimEnd('\n', '\r');

    public string SourceLineAtPos(int pos) => SourceLine(FindLine(pos));
}

/// <summary>A file and a span within it.</summary>
public sealed record FileSpan(CodeMap File, Span Span)
{
    public string FileName => File.FileName;

    public string SourceSpan => File.SourceSpan(Span);

    public ResolvedSpan ResolveSpan() => File.ResolveSpan(Span);

    public override string ToString() => $"{FileName}:{ResolveSpan()}";
}
