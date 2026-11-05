using System.Diagnostics;
using Starlark.Syntax;

// Two checks for Starlark source files, so that "add comments" can be proved to be only that.
//
//   CommentCheck same <git-ref> <file>...   each file must parse to the same program as its version at <git-ref>
//   CommentCheck coverage <file>...         report every function with no comment directly above it
//
// "same" parses both versions with the library's own parser and compares the printed programs. A comment never reaches the program
// tree, so a change that only adds, removes or rewrites comments gives identical text, and any change to code, a string, a name or the
// order of statements gives different text. Exit code 1 means a difference (or an undocumented function in "coverage" mode).

if (args.Length < 2) { Console.Error.WriteLine("usage: CommentCheck same <git-ref> <file>... | coverage <file>..."); return 2; }
var dialect = Dialect.AllOptionsInternal with { EnableFutures = true, EnableTopLevelStmt = true };
int bad = 0;

if (args[0] == "same")
{
    string gitRef = args[1];
    foreach (var path in args.Skip(2))
    {
        string oldText = Git(gitRef, path);
        string newText = File.ReadAllText(path);
        string a = Printed(path, oldText), b = Printed(path, newText);
        if (a == b)
        {
            int removed = oldText.Split('\n').Count(l => !IsComment(l) && l.Trim().Length > 0);
            Console.WriteLine($"same   {path}  ({removed} code lines unchanged; comments {Comments(oldText)} -> {Comments(newText)})");
        }
        else
        {
            bad++;
            Console.WriteLine($"DIFFERENT  {path}");
            var al = a.Split('\n'); var bl = b.Split('\n');
            for (int i = 0; i < Math.Max(al.Length, bl.Length); i++)
            {
                string x = i < al.Length ? al[i] : "<end>", y = i < bl.Length ? bl[i] : "<end>";
                if (x != y) { Console.WriteLine($"  first difference at printed line {i + 1}:\n    was: {x}\n    now: {y}"); break; }
            }
        }
    }
    return bad == 0 ? 0 : 1;
}

if (args[0] == "insert")
{
    // CommentCheck insert <spec> [<repo root>]: add the comments in the spec; see Insert.cs.
    return CommentCheck.Insert.Run(args[1], args.Length > 2 ? args[2] : Directory.GetCurrentDirectory()) == 0 ? 0 : 1;
}

if (args[0] == "coverage")
{
    int defs = 0;
    foreach (var path in args.Skip(1))
    {
        var lines = File.ReadAllLines(path);
        int fileDefs = 0, missing = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].TrimStart();
            if (!t.StartsWith("def ")) continue;
            fileDefs++;
            // A function is documented by a comment line (or a docstring) directly above it, or a docstring as its first body line.
            string above = i > 0 ? lines[i - 1].Trim() : "";
            bool doc = above.StartsWith('#') || (i + 1 < lines.Length && lines[i + 1].TrimStart().StartsWith('"'));
            if (!doc)
            {
                missing++;
                bad++;
                Console.WriteLine($"  {path}:{i + 1}  {t.Split('(')[0]}");
            }
        }
        defs += fileDefs;
    }
    Console.WriteLine($"{defs} functions, {bad} without a comment");
    return bad == 0 ? 0 : 1;
}

Console.Error.WriteLine("unknown mode " + args[0]);
return 2;

string Printed(string path, string text)
{
    try { return AstModule.Parse(path, text, dialect).Statement.ToString(); }
    catch (StarlarkSyntaxException e) { return "PARSE ERROR: " + e.Message.Split('\n')[0]; }
}

static bool IsComment(string line) => line.TrimStart().StartsWith('#');
static int Comments(string text) => text.Split('\n').Count(IsComment);

static string Git(string gitRef, string path)
{
    // git wants forward slashes in a path inside a commit, and a path relative to the repository root.
    // Read git's output as UTF-8. Without this the process uses the console's code page and any non-ASCII character in the old version is
    // decoded wrongly, which showed up as a false "different" on a file containing the half-block character.
    var psi = new ProcessStartInfo("git", $"show {gitRef}:{path.Replace(Path.DirectorySeparatorChar, '/')}")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = System.Text.Encoding.UTF8,
    };
    using var p = Process.Start(psi)!;
    string o = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    return p.ExitCode == 0 ? o : "";
}
