using System.Diagnostics;

// Builds the documentation site: publishes the WebAssembly playground and copies it, with the pages in docs/site, into one
// folder that any static web server can serve. Optionally checks the result in a headless browser.
//
//   BuildSite [repo root] [--out site-out] [--check]
//
// With --check it serves the folder on a local port and runs tools/WebCheck on the playground self test and on the deep
// dive's self check; the exit code is non-zero if either fails.

string root = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? Directory.GetCurrentDirectory());
string outDir = Path.GetFullPath(Path.Combine(root, Value("--out") ?? "site-out"));
bool check = args.Contains("--check");
if (!File.Exists(Path.Combine(root, "Starlark.slnx")))
{
    Console.Error.WriteLine($"{root} is not the starlark-cs repository (no Starlark.slnx)");
    return 2;
}

string publish = Path.Combine(Path.GetTempPath(), "starlark-site-publish");
if (Directory.Exists(publish)) Directory.Delete(publish, recursive: true);
Console.WriteLine("publishing the WebAssembly playground ...");
if (Run("dotnet", root, "publish", "web/Playground", "-c", "Release", "-o", publish, "-v", "q") != 0) return 1;

if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
Directory.CreateDirectory(outDir);
foreach (var f in Directory.GetFiles(Path.Combine(root, "docs", "site")))
    File.Copy(f, Path.Combine(outDir, Path.GetFileName(f)));
Copy(Path.Combine(publish, "wwwroot"), Path.Combine(outDir, "playground"));
Console.WriteLine($"site written to {outDir}");
if (!check) return 0;

// Serve the folder and check both pages in a headless browser.
const int port = 8799;
var server = Start("dotnet", root, "run", "--project", "tools/Serve", "--", outDir, port.ToString());
try
{
    using var http = new HttpClient();
    for (int i = 0; i < 120; i++)
    {
        try { if ((await http.GetAsync($"http://localhost:{port}/index.html")).IsSuccessStatusCode) break; }
        catch (HttpRequestException) { }
        await Task.Delay(500);
    }
    int a = Run("dotnet", root, "run", "--project", "tools/WebCheck", "--", $"http://localhost:{port}/playground/selftest.html", "--seconds", "90");
    int b = Run("dotnet", root, "run", "--project", "tools/WebCheck", "--", $"http://localhost:{port}/deep-dive.html?selfcheck", "--seconds", "90");
    Console.WriteLine(a == 0 && b == 0 ? "site check passed" : "site check FAILED");
    return a == 0 && b == 0 ? 0 : 1;
}
finally
{
    try { server.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
}

static void Copy(string from, string to)
{
    Directory.CreateDirectory(to);
    foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
    foreach (var d in Directory.GetDirectories(from)) Copy(d, Path.Combine(to, Path.GetFileName(d)));
}

static Process Start(string exe, string dir, params string[] arguments)
{
    var psi = new ProcessStartInfo(exe) { WorkingDirectory = dir, UseShellExecute = false };
    foreach (var a in arguments) psi.ArgumentList.Add(a);
    return Process.Start(psi)!;
}

static int Run(string exe, string dir, params string[] arguments)
{
    using var p = Start(exe, dir, arguments);
    p.WaitForExit();
    return p.ExitCode;
}

string? Value(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
