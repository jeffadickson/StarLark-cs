using System.Text;

namespace CommentCheck;

/// <summary>
/// Inserts comment lines into Starlark files from a spec, and nothing else, so that "add comments" can be done in bulk and then proved
/// to be only that by <c>CommentCheck same</c>.
/// </summary>
/// <remarks>
/// <para>The spec is plain text:</para>
/// <code>
/// @file apps/Basic/scripts/basic.star
/// @def take_output
/// Everything printed since the last call, and clear the buffer.
/// @line text of a line (trimmed; the comment goes above it, at its indentation)
/// Why the next statement does what it does.
/// </code>
/// <para>
/// A <c>@def NAME</c> block goes directly above <c>def NAME(</c> at column 0 and is skipped if a comment is already there. A
/// <c>@line</c> block goes above the one line whose trimmed text starts with the given text; it must match exactly one line.
/// Body lines become <c># </c> lines; an empty body line becomes a bare <c>#</c>. Nothing is written to a file unless every block for it applies.
/// </para>
/// </remarks>
public static class Insert
{
    /// <summary>Applies the spec. Returns the number of problems (0 means everything was inserted or already present).</summary>
    public static int Run(string specPath, string root)
    {
        var blocks = Parse(File.ReadAllLines(specPath));
        int problems = 0, inserted = 0, kept = 0;
        foreach (var group in blocks.GroupBy(b => b.File))
        {
            string path = Path.Combine(root, group.Key);
            if (!File.Exists(path)) { Console.WriteLine($"MISSING FILE {group.Key}"); problems++; continue; }
            string original = File.ReadAllText(path);
            bool crlf = original.Contains("\r\n");
            var lines = original.Replace("\r\n", "\n").Split('\n').ToList();
            int fileInserted = 0;
            var failed = new List<string>();
            // Resolve every target against the file as it is now, then insert from the bottom up so an insert never moves a later target.
            var resolved = group.Select(b => (Block: b, At: Find(lines, b))).OrderByDescending(r => r.At).ToList();
            foreach (var (b, at) in resolved)
            {
                if (at == -2) { failed.Add($"{b.Kind} '{b.Target}' matches more than one line"); continue; }
                if (at < 0) { failed.Add($"{b.Kind} '{b.Target}' not found"); continue; }
                if (b.Kind == "def" && at > 0 && lines[at - 1].TrimStart().StartsWith('#')) { kept++; continue; }
                string indent = new(' ', lines[at].Length - lines[at].TrimStart().Length);
                var comment = b.Body.Select(t => t.Length == 0 ? indent + "#" : indent + "# " + t);
                lines.InsertRange(at, comment);
                fileInserted++;
            }
            if (failed.Count > 0)
            {
                foreach (var f in failed) Console.WriteLine($"PROBLEM {group.Key}: {f}");
                problems += failed.Count;
                continue;   // all or nothing per file
            }
            if (fileInserted > 0)
            {
                string text = string.Join("\n", lines);
                File.WriteAllText(path, crlf ? text.Replace("\n", "\r\n") : text);
            }
            inserted += fileInserted;
            Console.WriteLine($"{group.Key}: {fileInserted} comment block(s) inserted");
        }
        Console.WriteLine($"{inserted} inserted, {kept} functions already had a comment, {problems} problem(s)");
        return problems;
    }

    private sealed record Block(string File, string Kind, string Target, List<string> Body);

    private static List<Block> Parse(string[] spec)
    {
        var blocks = new List<Block>();
        string file = "";
        Block? current = null;
        foreach (var raw in spec)
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("@file ")) { file = line[6..].Trim(); current = null; }
            else if (line.StartsWith("@def ")) blocks.Add(current = new Block(file, "def", line[5..].Trim(), []));
            else if (line.StartsWith("@line ")) blocks.Add(current = new Block(file, "line", line[6..].Trim(), []));
            else if (line.StartsWith("@@")) { }   // a comment in the spec itself
            else if (current is not null) current.Body.Add(line);
        }
        foreach (var b in blocks) { while (b.Body.Count > 0 && b.Body[^1].Length == 0) b.Body.RemoveAt(b.Body.Count - 1); }
        return blocks;
    }

    /// <summary>The index of the target line, -1 if there is none and -2 if there is more than one.</summary>
    private static int Find(List<string> lines, Block b)
    {
        // `@line #103 text` pins the target to line 103 of the file as it was before any comment in this run went in, and still
        // checks that the line there starts with the text: so the spec cannot silently land on the wrong line if the file moves.
        if (b.Target.StartsWith('#'))
        {
            int space = b.Target.IndexOf(' ');
            if (space > 1 && int.TryParse(b.Target[1..space], out int number))
            {
                int index = number - 1;   // blocks are applied bottom-up, so lines above the target have not moved
                string want = b.Target[(space + 1)..];
                return index >= 0 && index < lines.Count && lines[index].TrimStart().StartsWith(want, StringComparison.Ordinal) ? index : -1;
            }
        }
        int found = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            bool hit = b.Kind == "def"
                ? lines[i].StartsWith("def " + b.Target + "(")
                : lines[i].TrimStart().StartsWith(b.Target, StringComparison.Ordinal);
            if (!hit) continue;
            if (found >= 0) return -2;
            found = i;
        }
        return found;
    }
}
