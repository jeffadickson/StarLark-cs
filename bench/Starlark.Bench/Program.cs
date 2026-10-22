using System.Diagnostics;
using Starlark.Syntax;

// Usage: Starlark.Bench <dir-of-.star-files>
var files = Directory.GetFiles(args[0], "*.star").Select(File.ReadAllText).ToList();
long bytes = files.Sum(f => (long)f.Length);
var dialect = Dialect.AllOptionsInternal;

long LexOnce()
{
    long n = 0;
    foreach (var f in files) n += Lexer.LexAll(new CodeMap("x", f)).Count;
    return n;
}
void ParseOnce() { foreach (var f in files) AstModule.Parse("x", f, dialect); }

for (int i = 0; i < 20; i++) { LexOnce(); ParseOnce(); }   // warm up the JIT

const int Rounds = 100;
var sw = Stopwatch.StartNew();
long tokens = 0;
for (int i = 0; i < Rounds; i++) tokens = LexOnce();
double lexMs = sw.Elapsed.TotalMilliseconds / Rounds;
sw.Restart();
for (int i = 0; i < Rounds; i++) ParseOnce();
double parseMs = sw.Elapsed.TotalMilliseconds / Rounds;

Console.WriteLine($"{files.Count} files, {bytes / 1024.0:F0} KiB, {tokens} tokens");
Console.WriteLine($"lex only : {lexMs:F2} ms/pass  = {bytes / 1048576.0 / (lexMs / 1000):F1} MiB/s, {tokens / (lexMs / 1000) / 1e6:F1} Mtok/s");
Console.WriteLine($"lex+parse: {parseMs:F2} ms/pass  = {bytes / 1048576.0 / (parseMs / 1000):F1} MiB/s");
