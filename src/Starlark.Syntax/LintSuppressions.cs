namespace Starlark.Syntax;

/// <summary>
/// Lint issues suppressed using inline comments of the shape
/// <c># starlark-lint-disable ISSUE_NAME, ISSUE_NAME, ...</c>.
/// </summary>
public sealed class LintSuppressions
{
    private const string Prefix = "starlark-lint-disable ";

    private sealed record SuppressionInfo(Span TokenSpan, Span EffectiveSpan, bool SuppressNextLine);

    private readonly Dictionary<string, List<SuppressionInfo>> _suppressions = [];

    /// <summary>Is the lint <paramref name="issueShortName"/> suppressed for <paramref name="issueSpan"/>?</summary>
    public bool IsSuppressed(string issueShortName, Span issueSpan)
    {
        if (!_suppressions.TryGetValue(issueShortName, out var infos))
        {
            return false;
        }
        return infos.Any(info =>
            // Is this suppression the last thing in a suppress-next-line issue span? (issueSpan includes the line terminator)
            info.SuppressNextLine && issueSpan.End - 1 == info.TokenSpan.End
                ? false
                : issueSpan.Intersects(info.EffectiveSpan));
    }

    /// <summary>Parse a single comment line and extract any lint suppressions.</summary>
    internal static List<string> ParseComment(string comment)
    {
        var res = new List<string>();
        string trimmed = comment.TrimStart();
        if (trimmed.StartsWith(Prefix, StringComparison.Ordinal))
        {
            foreach (var name in trimmed[Prefix.Length..].Split([' ', ',']))
            {
                string n = name.Trim();
                if (n.Length > 0)
                {
                    res.Add(n);
                }
            }
        }
        return res;
    }

    /// <summary>Accumulates blocks of comments and builds a <see cref="LintSuppressions"/>.</summary>
    internal sealed class Builder
    {
        private readonly LintSuppressions _result = new();
        private readonly List<Span> _tokenSpans = [];
        private readonly List<Span> _effectiveSpans = [];
        private readonly HashSet<string> _shortNames = [];
        private int _lastLine;

        /// <summary>Call for each comment in a block of comments.</summary>
        public void ParseComment(CodeMap codemap, string comment, int start, int end)
        {
            var parsed = LintSuppressions.ParseComment(comment);
            if (parsed.Count == 0 && _shortNames.Count == 0)
            {
                return;
            }
            var tokenSpan = new Span(start, end);
            int line = codemap.FindLine(start);
            var effective = codemap.LineSpanTrimNewline(line);
            _shortNames.UnionWith(parsed);
            _tokenSpans.Add(tokenSpan);
            _effectiveSpans.Add(effective);
            _lastLine = line;
        }

        /// <summary>Call after the last comment in a block of comments.</summary>
        public void EndOfCommentBlock(CodeMap codemap)
        {
            if (_shortNames.Count > 0)
            {
                Update(codemap);
            }
            else
            {
                Clear();
            }
        }

        private void Clear()
        {
            _tokenSpans.Clear();
            _effectiveSpans.Clear();
            _shortNames.Clear();
        }

        private void Update(CodeMap codemap)
        {
            int numberOfTokens = _tokenSpans.Count;
            var tokenSpan = Span.MergeAll(_tokenSpans);
            var effective = Span.MergeAll(_effectiveSpans);
            var names = _shortNames.ToList();
            int lastLine = _lastLine;
            Clear();

            // In case the suppression comment has preceding whitespace.
            string before = codemap.SourceSpan(new Span(effective.Begin, tokenSpan.Begin));
            bool suppressNextLine = numberOfTokens > 1
                || effective == tokenSpan
                || (effective.End == tokenSpan.End && before.Trim().Length == 0);
            if (suppressNextLine && codemap.LineSpanOpt(lastLine + 1) is { } next)
            {
                // Include the next line, in case the suppression was put on the line before the issue.
                effective = effective.Merge(next);
            }

            foreach (var name in names)
            {
                if (!_result._suppressions.TryGetValue(name, out var list))
                {
                    _result._suppressions[name] = list = [];
                }
                list.Add(new SuppressionInfo(tokenSpan, effective, suppressNextLine));
            }
        }

        public LintSuppressions Build() => _result;
    }
}
