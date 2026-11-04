using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Turns on NTFS compression for build and test artifact folders, so a long soak or repeated builds use less disk.
//
//   Compress <folder>... [--root <repo>] [--dry-run] [--undo]
//
// A folder is marked compressed, which makes files created in it from then on compressed, and every file already inside is
// compressed too. Files that are open are skipped and reported. Nothing is deleted or changed except the compression flag, so
// --undo (which clears it) restores the original state. With --root, the usual artifact folders of a repository are used:
// bin, obj, TestResults and StrykerOutput under it, and any folders named on the command line besides.

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("NTFS compression needs Windows.");
    return 2;
}

bool dryRun = args.Contains("--dry-run"), undo = args.Contains("--undo");
string? root = null;
var folders = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--root" && i + 1 < args.Length) root = Path.GetFullPath(args[++i]);
    else if (!args[i].StartsWith("--")) folders.Add(Path.GetFullPath(args[i]));
}
if (root is not null)
{
    foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
    {
        string name = Path.GetFileName(dir);
        if (name is "bin" or "obj" or "TestResults" or "StrykerOutput" && !dir.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar)) folders.Add(dir);
    }
}
// Work from the outside in: a folder inside one already handled is covered by it.
folders = folders.Distinct().OrderBy(f => f.Length).Where((f, _) => true).ToList();
var top = new List<string>();
foreach (var f in folders)
{
    if (!top.Any(t => f.StartsWith(t + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) top.Add(f);
}
if (top.Count == 0)
{
    Console.Error.WriteLine("usage: Compress <folder>... [--root <repo>] [--dry-run] [--undo]");
    return 2;
}

long before = 0, after = 0, files = 0, skipped = 0;
foreach (var folder in top.Where(Directory.Exists))
{
    var all = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList();
    long logical = all.Sum(f => new FileInfo(f).Length);
    long onDisk = all.Sum(OnDisk);
    before += onDisk;
    if (!dryRun)
    {
        Set(folder, !undo, directory: true);
        Parallel.ForEach(Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories), d => Set(d, !undo, directory: true));
        Parallel.ForEach(all, new ParallelOptions { MaxDegreeOfParallelism = 8 }, f =>
        {
            if (Set(f, !undo, directory: false)) Interlocked.Increment(ref files);
            else Interlocked.Increment(ref skipped);
        });
    }
    long now = dryRun ? onDisk : all.Where(File.Exists).Sum(OnDisk);
    after += now;
    Console.WriteLine($"{(undo ? "uncompressed" : "compressed"),-12} {folder}: {all.Count} files, {logical / 1048576.0:F1} MB of data, {onDisk / 1048576.0:F1} MB on disk -> {now / 1048576.0:F1} MB");
}
Console.WriteLine($"\n{(dryRun ? "dry run: " : "")}{files} files changed, {skipped} skipped (in use or refused); on disk {before / 1048576.0:F1} MB -> {after / 1048576.0:F1} MB (saved {(before - after) / 1048576.0:F1} MB)");
return 0;

static long OnDisk(string path)
{
    uint high;
    uint low = GetCompressedFileSizeW(path, out high);
    if (low == 0xFFFFFFFF && Marshal.GetLastWin32Error() != 0) return new FileInfo(path).Length;
    return ((long)high << 32) | low;
}

static bool Set(string path, bool compress, bool directory)
{
    const uint FSCTL_SET_COMPRESSION = 0x9C040;
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_ALL = 7, OPEN_EXISTING = 3, BACKUP_SEMANTICS = 0x02000000;
    using SafeFileHandle h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, directory ? BACKUP_SEMANTICS : 0, IntPtr.Zero);
    if (h.IsInvalid) return false;
    ushort format = compress ? (ushort)1 : (ushort)0;   // COMPRESSION_FORMAT_DEFAULT / NONE
    return DeviceIoControl(h, FSCTL_SET_COMPRESSION, ref format, sizeof(ushort), IntPtr.Zero, 0, out _, IntPtr.Zero);
}

[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref ushort inBuffer, int inSize, IntPtr outBuffer, int outSize, out int returned, IntPtr overlapped);

[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
static extern uint GetCompressedFileSizeW(string name, out uint high);
