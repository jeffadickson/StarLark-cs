using System.Diagnostics;
using System.Text.RegularExpressions;

// Loads a page in a headless browser (Microsoft Edge or Chrome) and prints the text the page ends with, so a build can check
// that the WebAssembly playground and the HTML documentation really run. No browser automation library is needed: the
// browser's own --dump-dom flag waits for the page's scripts (up to a virtual time budget) and prints the final DOM.
//
//   WebCheck <url> [--expect TEXT] [--seconds N]
//
// Exit code 0 if the page text contains TEXT (default "ALL PASSED"), 1 if not, 2 if no browser was found.

string? url = args.FirstOrDefault(a => !a.StartsWith("--"));
if (url is null)
{
    Console.Error.WriteLine("usage: WebCheck <url> [--expect TEXT] [--seconds N]");
    return 2;
}
string expect = Value("--expect") ?? "ALL PASSED";
int seconds = int.TryParse(Value("--seconds"), out var s) ? s : 60;

string[] candidates =
[
    @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    @"C:\Program Files\Google\Chrome\Application\chrome.exe",
    "/usr/bin/google-chrome", "/usr/bin/chromium", "/usr/bin/microsoft-edge",
];
string? browser = candidates.FirstOrDefault(File.Exists);
if (browser is null)
{
    Console.Error.WriteLine("no headless browser found (looked for Edge and Chrome)");
    return 2;
}

// A throwaway profile, so the check never touches the user's browser profile and starts with an empty cache.
string profile = Path.Combine(Path.GetTempPath(), "webcheck-" + Guid.NewGuid().ToString("N"));
var psi = new ProcessStartInfo(browser)
{
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
};
foreach (var a in new[] { "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", $"--user-data-dir={profile}",
                          $"--virtual-time-budget={seconds * 1000}", "--dump-dom", url })
    psi.ArgumentList.Add(a);

string dom;
using (var p = Process.Start(psi)!)
{
    var read = p.StandardOutput.ReadToEndAsync();
    _ = p.StandardError.ReadToEndAsync();
    if (!p.WaitForExit(TimeSpan.FromSeconds(seconds + 60)))
    {
        p.Kill(entireProcessTree: true);
        Console.Error.WriteLine("the browser did not finish in time");
        return 1;
    }
    dom = await read;
}
try { Directory.Delete(profile, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }

// Show the page as text: tags removed, the common entities decoded.
string text = Regex.Replace(dom, "<script[\\s\\S]*?</script>|<style[\\s\\S]*?</style>", "");
text = Regex.Replace(text, "<br\\s*/?>", "\n");
text = Regex.Replace(text, "<[^>]+>", "");
text = text.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&amp;", "&");
Console.WriteLine(text.Trim());
bool ok = text.Contains(expect, StringComparison.Ordinal);
Console.WriteLine(ok ? $"webcheck: found \"{expect}\"" : $"webcheck: \"{expect}\" NOT FOUND");
return ok ? 0 : 1;

string? Value(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
