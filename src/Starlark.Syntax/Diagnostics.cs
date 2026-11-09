using System.Text;

namespace Starlark.Syntax;

/// <summary>
/// A syntax error with its location. <see cref="Exception.Message"/> (and <see cref="object.ToString"/>)
/// is a rustc-style rendered diagnostic, matching the original's <c>Display</c> output.
/// </summary>
public sealed class StarlarkSyntaxException : Exception
{
    /// <summary>The bare message, e.g. <c>Parse error: unexpected new line, expected expression</c>.</summary>
    public string RawMessage { get; }

    public FileSpan Location { get; }

    private string? _rendered;

    public StarlarkSyntaxException(string rawMessage, Span span, CodeMap codemap)
        : base(rawMessage)
    {
        RawMessage = rawMessage;
        Location = new FileSpan(codemap, span);
    }

    /// <summary>Rendered lazily: the parser collects recoverable errors it may never report.</summary>
    public override string Message => _rendered ??= SnippetRenderer.Render(RawMessage, Location.File, Location.Span);

    public override string ToString() => Message;
}

/// <summary>Renders an error with a source snippet, in the style of rustc / annotate-snippets.</summary>
public static class SnippetRenderer
{
    public static string Render(string label, CodeMap cm, Span span)
    {
        var rs = cm.ResolveSpan(span);
        int firstLine = rs.Begin.Line;
        int lastLine = rs.End.Line;
        int width = (lastLine + 1).ToString().Length;
        string pad = new(' ', width);
        var sb = new StringBuilder();

        sb.Append("error: ").Append(label).Append('\n');
        sb.Append(pad).Append("--> ").Append(cm.FileName).Append(':').Append(rs.Begin).Append('\n');
        sb.Append(pad).Append(" |\n");

        // The snippet is bounded whatever the input: a line longer than MaxWidth is shown clipped around the error column, and a span over
        // more than five lines shows its first two and last two lines. The '-->' header above always gives the full position.
        int shift;
        if (firstLine == lastLine)
        {
            string text = Clip(cm.SourceLine(firstLine), rs.Begin.Column, out shift);
            AppendSource(sb, firstLine, width, "", text);
            int carets = Math.Min(MaxWidth, Math.Max(1, rs.End.Column - rs.Begin.Column));
            sb.Append(pad).Append(" | ").Append(' ', rs.Begin.Column - shift).Append('^', carets).Append('\n');
            return sb.ToString();
        }

        // Multi-line span.
        string firstText = cm.SourceLine(firstLine);
        int beginCol = rs.Begin.Column;
        bool compact = IsBlank(Prefix(firstText, beginCol));
        string shown = Clip(firstText, beginCol, out shift);
        if (compact)
        {
            AppendSource(sb, firstLine, width, "/ ", shown);
        }
        else
        {
            AppendSource(sb, firstLine, width, "  ", shown);
            sb.Append(pad).Append(" |  ").Append('_', beginCol - shift + 1).Append("^\n");
        }
        int lastCol = Math.Max(rs.End.Column - 1, 0);
        bool elide = lastLine - firstLine + 1 > 5;
        for (int line = firstLine + 1; line <= lastLine; line++)
        {
            if (elide && line > firstLine + 1 && line < lastLine - 1)
            {
                if (line == firstLine + 2) sb.Append(pad).Append(" | | ...\n");
                continue;
            }
            AppendSource(sb, line, width, "| ", Clip(cm.SourceLine(line), line == lastLine ? lastCol : 0, out shift));
        }
        sb.Append(pad).Append(" | |").Append('_', lastCol - shift + 1).Append("^\n");
        return sb.ToString();
    }

    /// <summary>The widest source text shown for one line of a snippet.</summary>
    private const int MaxWidth = 240;

    /// <summary>
    /// <paramref name="text"/> if it is at most <see cref="MaxWidth"/> chars, otherwise a window of that width around
    /// <paramref name="col"/> with "..." at each cut end. <paramref name="shift"/> is how far columns move left in the result.
    /// </summary>
    private static string Clip(string text, int col, out int shift)
    {
        shift = 0;
        if (text.Length <= MaxWidth) return text;
        int start = Math.Clamp(col - MaxWidth / 2, 0, text.Length - MaxWidth);
        if (start > 0 && char.IsLowSurrogate(text[start])) start++;
        int end = Math.Min(text.Length, start + MaxWidth);
        if (end < text.Length && char.IsLowSurrogate(text[end])) end++;
        shift = start > 0 ? start - 3 : 0;
        return (start > 0 ? "..." : "") + text[start..end] + (end < text.Length ? "..." : "");
    }

    private static void AppendSource(StringBuilder sb, int line, int width, string marker, string text)
    {
        sb.Append((line + 1).ToString().PadRight(width)).Append(" | ").Append(marker).Append(text);
        if (text.Length == 0)
        {
            // Don't leave trailing whitespace after an empty line's marker.
            while (sb.Length > 0 && sb[^1] == ' ')
            {
                sb.Length--;
            }
        }
        sb.Append('\n');
    }

    /// <summary>First <paramref name="scalars"/> Unicode scalar values of <paramref name="s"/>.</summary>
    private static string Prefix(string s, int scalars)
    {
        int i = 0;
        for (int n = 0; n < scalars && i < s.Length; n++)
        {
            i += char.IsHighSurrogate(s[i]) && i + 1 < s.Length ? 2 : 1;
        }
        return s[..i];
    }

    private static bool IsBlank(string s) => s.All(char.IsWhiteSpace);
}
