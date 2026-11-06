using System.Diagnostics;

// Builds the documentation site: publishes the WebAssembly playground and copies it, with the pages in docs/site, into one
// folder that any static web server can serve. Optionally checks the result in a headless browser.
//
//   BuildSite [repo root] [--out site-out] [--check]
//
// With --check it serves the folder on a local port and runs tools/WebCheck on the playground self test and on the deep
// dive's self check; the exit code is non-zero if either fails.

// The repository root is the first argument that is neither an option nor the value of --out.
string root = Path.GetFullPath(args.Where((a, i) => !a.StartsWith("--") && (i == 0 || args[i - 1] != "--out")).FirstOrDefault()
    ?? Directory.GetCurrentDirectory());
string outDir = Path.GetFullPath(Path.Combine(root, Value("--out") ?? "site-out"));
bool check = args.Contains("--check");
if (!File.Exists(Path.Combine(root, "Starlark.slnx")))
{
    Console.Error.WriteLine($"{root} is not the starlark-cs repository (no Starlark.slnx)");
    return 2;
}

// The output folder is deleted and rebuilt, so refuse anything that could hold other files: it must be new, empty, or a
// folder this tool wrote before (it leaves a marker file there), and never the repository, one of its parents, or a drive root.
const string marker = ".buildsite";
if (UnsafeOutDir(outDir, root) is { } why)
{
    Console.Error.WriteLine($"refusing to use {outDir} as the output folder: {why}");
    return 2;
}

string publish = Path.Combine(Path.GetTempPath(), "starlark-site-publish");
if (Directory.Exists(publish)) Directory.Delete(publish, recursive: true);
Console.WriteLine("publishing the WebAssembly playground ...");
if (Run("dotnet", root, "publish", "web/Playground", "-c", "Release", "-o", publish, "-v", "q") != 0) return 1;

if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
Directory.CreateDirectory(outDir);
File.WriteAllText(Path.Combine(outDir, marker), "Written by tools/BuildSite; the folder is deleted and rebuilt on every run.\n");
foreach (var f in Directory.GetFiles(Path.Combine(root, "docs", "site")))
    File.Copy(f, Path.Combine(outDir, Path.GetFileName(f)));
Copy(Path.Combine(publish, "wwwroot"), Path.Combine(outDir, "playground"));
Console.WriteLine($"site written to {outDir}");
if (!check) return 0;

// Serve the folder and check both pages in a headless browser.
const int port = 8799;
// Another server on the port would answer the checks with its own content, so make sure the port is free first.
if (PortInUse(port))
{
    Console.Error.WriteLine($"port {port} is already in use: stop whatever is serving on it and try again");
    Console.WriteLine("site check FAILED");
    return 1;
}
var server = Start("dotnet", root, "run", "--project", "tools/Serve", "--", outDir, port.ToString());
try
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    bool ready = false;
    for (int i = 0; i < 120 && !ready && !server.HasExited; i++)
    {
        try { ready = (await http.GetAsync($"http://localhost:{port}/index.html")).IsSuccessStatusCode; }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
        if (!ready) await Task.Delay(500);
    }
    if (!ready || server.HasExited)
    {
        Console.Error.WriteLine(server.HasExited
            ? $"Serve exited (code {server.ExitCode}) before it served the site on port {port}"
            : $"Serve did not start on port {port} within 60 seconds");
        Console.WriteLine("site check FAILED");
        return 1;
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

// Why outDir must not be deleted, or null if it is safe: a folder that does not exist, is empty, or carries the marker,
// and is not the repository, inside docs/ or src/, a parent of the repository, or a drive root.
static string? UnsafeOutDir(string outDir, string root)
{
    static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
    var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    string o = Norm(outDir), r = Norm(root);
    bool Within(string child, string parent) =>
        child.Equals(parent, cmp) || child.StartsWith(parent + Path.DirectorySeparatorChar, cmp);
    if (Path.GetPathRoot(o) is { } driveRoot && o.Equals(Norm(driveRoot), cmp)) return "it is a drive root";
    if (Within(r, o)) return "it is the repository or one of its parent folders";
    foreach (var keep in new[] { "docs", "src", "tests", "tools", "web", "apps", "examples", ".git" })
    {
        if (Within(o, Path.Combine(r, keep))) return $"it is inside the repository's {keep} folder";
    }
    if (!Directory.Exists(o)) return null;
    if (File.Exists(Path.Combine(o, ".buildsite"))) return null;
    // A folder written by a BuildSite from before the marker holds only the site pages and the playground folder.
    var expected = Directory.GetFiles(Path.Combine(r, "docs", "site")).Select(Path.GetFileName).Append("playground").ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (Directory.EnumerateFileSystemEntries(o).All(e => expected.Contains(Path.GetFileName(e)))) return null;
    return "it is not empty and was not written by BuildSite (no .buildsite marker file); delete it yourself or pick another folder";
}

static bool PortInUse(int port)
{
    try
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
        listener.Start();
        listener.Stop();
        return false;
    }
    catch (System.Net.Sockets.SocketException)
    {
        return true;
    }
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
