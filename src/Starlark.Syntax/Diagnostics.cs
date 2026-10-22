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

        if (firstLine == lastLine)
        {
            string text = cm.SourceLine(firstLine);
            AppendSource(sb, firstLine, width, "", text);
            int carets = Math.Max(1, rs.End.Column - rs.Begin.Column);
            sb.Append(pad).Append(" | ").Append(' ', rs.Begin.Column).Append('^', carets).Append('\n');
            return sb.ToString();
        }

        // Multi-line span.
        string firstText = cm.SourceLine(firstLine);
        int beginCol = rs.Begin.Column;
        bool compact = IsBlank(Prefix(firstText, beginCol));
        if (compact)
        {
            AppendSource(sb, firstLine, width, "/ ", firstText);
        }
        else
        {
            AppendSource(sb, firstLine, width, "  ", firstText);
            sb.Append(pad).Append(" |  ").Append('_', beginCol + 1).Append("^\n");
        }
        for (int line = firstLine + 1; line <= lastLine; line++)
        {
            AppendSource(sb, line, width, "| ", cm.SourceLine(line));
        }
        int lastCol = Math.Max(rs.End.Column - 1, 0);
        sb.Append(pad).Append(" | |").Append('_', lastCol + 1).Append("^\n");
        return sb.ToString();
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
