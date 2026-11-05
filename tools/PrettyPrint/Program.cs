using System.Text;
using Basic;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Starlark.Eval;
using Starlark.Syntax;

// Pretty prints the TRS-80 Level II BASIC interpreter (written in Starlark) as a PDF: a cover, an overview, the sample programs with
// their output as the interpreter really produced it, then the whole source with syntax colouring, line numbers and a table of contents.
//
//   PrettyPrint [--out file.pdf] [--font "Consolas"]
//
// The Starlark source is coloured with the library's own lexer, so what is a keyword, string, number or comment is exactly what the
// interpreter itself thinks. The BASIC programs are coloured by a small scanner here. Nothing is typed in by hand: the source and the
// output come from the files and from running the machine.

string outPath = Path.GetFullPath(Arg("--out") ?? "Level-II-BASIC-in-Starlark.pdf");
string fontName = Arg("--font") ?? "Consolas";
string? Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

GlobalFontSettings.FontResolver = new WindowsFonts();

var doc = new PdfDocument();
doc.Info.Title = "TRS-80 Level II BASIC, written in Starlark";
doc.Info.Author = "starlark-cs";
doc.Info.Subject = "The interpreter source and sample programs";
var pen = new Printer(doc, fontName);

// ---------------------------------------------------------------- content

string[] sourceFiles = ["basic.star", "expr.star", "lex.star", "numbers.star", "screen.star", "editor.star"];
var sources = sourceFiles.ToDictionary(f => f, f => (Scripts.Read(f) ?? throw new InvalidOperationException(f + " is missing")).Replace("\r\n", "\n").TrimEnd('\n'));
string[] programs = ["hello", "fib", "primes", "sine", "guess"];

var roles = new Dictionary<string, string>
{
    ["basic.star"] = "The machine: program storage, statements, control flow, errors, INPUT, the run loop",
    ["expr.star"] = "Expressions: compiled to reverse Polish notation, then evaluated, with no recursion",
    ["lex.star"] = "The tokenizer: keywords anywhere in a line, two-character names, numbers",
    ["numbers.star"] = "Numbers: integer, single and double precision, printing, and the math functions",
    ["screen.star"] = "The 64 x 16 screen, block graphics and PRINT USING",
    ["editor.star"] = "The EDIT command as a function of one keystroke",
};

// Run the sample programs on the real machine to capture their output.
var outputs = new Dictionary<string, string>();
foreach (var name in programs)
{
    var m = new Machine();
    m.TakeOutput();
    m.Load(Scripts.ReadProgram(name)!);
    var status = m.Enter("RUN");
    if (name == "guess")
    {
        // A fixed binary search against the seeded generator, so the page shows a whole game.
        int lo = 1, hi = 100;
        for (int i = 0; i < 12 && status == "waiting"; i++)
        {
            int guess = (lo + hi) / 2;
            status = m.Enter(guess.ToString());
            var o = m.TakeOutput();
            if (o.Contains("TOO LOW")) lo = guess + 1; else if (o.Contains("TOO HIGH")) hi = guess - 1; else break;
        }
        m = new Machine();
        m.TakeOutput();
        m.Load(Scripts.ReadProgram(name)!);
        status = m.Enter("RUN");
        lo = 1; hi = 100;
        var transcript = new StringBuilder(m.TakeOutput().Replace("\r", ""));
        for (int i = 0; i < 12 && status == "waiting"; i++)
        {
            int guess = (lo + hi) / 2;
            status = m.Enter(guess.ToString());
            var o = m.TakeOutput().Replace("\r", "");
            transcript.Append(o);
            if (o.Contains("TOO LOW")) lo = guess + 1; else if (o.Contains("TOO HIGH")) hi = guess - 1; else break;
        }
        outputs[name] = transcript.ToString();
    }
    else
    {
        outputs[name] = m.TakeOutput().Replace("\r", "");
    }
}

// ---------------------------------------------------------------- pages

pen.Cover("TRS-80 Level II BASIC", "written in Starlark",
[
    "A complete Level II BASIC interpreter, about 3,000 lines of Starlark.",
    "The host program only supplies the keyboard and the screen.",
    "",
    "Source files:",
    ..sourceFiles.Select(f => $"  {f,-14} {sources[f].Split('\n').Length,5} lines"),
    "",
    $"Printed {DateTime.Now:yyyy-MM-dd} from the files in the repository.",
]);

pen.ReserveContents();
pen.NewPage();
pen.Heading("How it works", 1);
pen.Paragraph(
    "Starlark has no while loop and no recursion, so the interpreter is one flat loop. run() executes one BASIC statement per turn of a bounded " +
    "for loop. GOTO, GOSUB and FOR are only changes to the program counter and to two plain lists used as stacks. Expressions are compiled with " +
    "the shunting-yard algorithm and evaluated with an explicit stack, so nesting depth is a number we choose and can never overflow the host.");
pen.Paragraph(
    "The whole machine is one dictionary. The host calls submit(state, line, budget) with each typed line and takes back what was printed. " +
    "Nothing in the script touches the console, the disk or the clock, and RND is a generator written in the script, so the same program prints " +
    "the same thing every time. INPUT works by returning \"waiting\" and being called again with the answer.");
pen.Heading("What it reproduces from Level II", 2);
pen.Bullets(
[
    "Line-numbered programs; RUN, LIST, NEW, CONT, DELETE, CLEAR and EDIT (the key-by-key line editor).",
    "PRINT with ; , TAB( SPC( @ and USING; LET, IF/THEN/ELSE, FOR/NEXT/STEP, GOTO, GOSUB/RETURN, ON..GOTO/GOSUB, INPUT, DATA/READ/RESTORE.",
    "DIM, DEF FN, DEFINT/SNG/DBL/STR, RANDOM, CLS, SET/RESET/POINT, PEEK/POKE on video memory, TRON/TROFF, STOP, END, ERROR, ON ERROR GOTO, RESUME.",
    "Integer, single and double precision numbers (a single is rounded to 24 bits) and strings; the usual functions; two-letter error codes (?SN ERROR IN 100).",
    "Keywords are found anywhere in a line, so FORI=1TO10 works with no spaces, as on the real machine, and names have two significant characters.",
    "A FOR loop always runs its body once. True is -1. Numbers print with a sign slot and a trailing space.",
]);
pen.Heading("Where it differs", 2);
pen.Bullets(
[
    "SIN, COS, TAN, ATN, EXP, LOG and ^ are series approximations written in the script, accurate to the six digits Level II printed.",
    "RND is a seeded generator of our own, so the numbers differ from a real TRS-80's; they are repeatable.",
    "Disk and cassette, LPRINT, AUTO and RENUM are not implemented. Programs are stored as typed text, not as token bytes, so PEEK of program memory does not match.",
    "The details of EDIT were written from memory of the manual, not checked against a real machine or a ROM.",
]);

pen.NewPage();
int sampleStart = pen.PageNumber;
pen.Heading("Sample programs", 1);
pen.Paragraph("Each program is listed, then the output the interpreter produced when it ran it for this document.");
foreach (var name in programs)
{
    string text = Scripts.ReadProgram(name)!.Replace("\r\n", "\n").TrimEnd('\n');
    pen.EnsureSpace(8);
    pen.Heading($"{name}.bas", 2);
    pen.Code(text, Highlighter.Basic(text), numbered: false);
    pen.Label("Output");
    pen.Plain(outputs[name].Replace("RUN\n", "", StringComparison.Ordinal).TrimEnd('\n'));
}

var starts = new Dictionary<string, int>();
bool first = true;
foreach (var f in sourceFiles)
{
    pen.NewPage();
    starts[f] = pen.PageNumber;
    if (first)
    {
        pen.Heading("The source", 1);
        pen.Paragraph("Starlark, in the order a reader would want it: the machine first, then what it uses. Colours are from the library's own lexer.");
        first = false;
    }
    pen.Heading(f, 2);
    pen.Label(roles[f]);
    pen.Code(sources[f], Highlighter.Starlark(f, sources[f]), numbered: true);
}

// Table of contents goes on the page after the cover, written last so it can show real page numbers.
pen.FillContents("Contents",
[
    ("How it works", 3),
    ("Sample programs", sampleStart),
    ..sourceFiles.Select(f => ($"{f}  -  {roles[f]}", starts[f])),
]);

pen.Finish();
int pageCount = doc.PageCount;   // a saved document can no longer be asked
doc.Save(outPath);
Console.WriteLine($"wrote {outPath}: {pageCount} pages, {new FileInfo(outPath).Length / 1024} KB");
return 0;

// ================================================================ drawing

/// <summary>Lays text out on A4 pages in a fixed-width font, with a header and page number on each page.</summary>
sealed class Printer
{
    private readonly PdfDocument _doc;
    private readonly XFont _code, _codeBold, _body, _bodyBold, _small, _h1, _h2, _title;
    private PdfPage _page = null!;
    private XGraphics _g = null!;
    private double _y;
    private readonly List<PdfPage> _pages = [];
    private string _running = "TRS-80 Level II BASIC in Starlark";

    public const double Left = 54, Right = 54, Top = 62, Bottom = 54;
    private double Width => _page.Width.Point - Left - Right;
    public int PageNumber => _pages.Count;

    public Printer(PdfDocument doc, string mono)
    {
        _doc = doc;
        var opts = new XPdfFontOptions(PdfFontEncoding.Unicode);
        _code = new XFont(mono, 7.6, XFontStyleEx.Regular, opts);
        _codeBold = new XFont(mono, 7.6, XFontStyleEx.Bold, opts);
        _body = new XFont("Calibri", 10.5, XFontStyleEx.Regular, opts);
        _bodyBold = new XFont("Calibri", 10.5, XFontStyleEx.Bold, opts);
        _small = new XFont("Calibri", 8.5, XFontStyleEx.Regular, opts);
        _h1 = new XFont("Calibri", 20, XFontStyleEx.Bold, opts);
        _h2 = new XFont("Calibri", 13.5, XFontStyleEx.Bold, opts);
        _title = new XFont("Calibri", 34, XFontStyleEx.Bold, opts);
        NewPage();
    }

    private double CodeAdvance => _g.MeasureString("M", _code).Width;
    private double CodeLine => 9.6;
    private static readonly XColor Ink = XColor.FromArgb(34, 34, 34), Grey = XColor.FromArgb(120, 120, 120), Rule = XColor.FromArgb(200, 200, 200);

    public void NewPage()
    {
        _g?.Dispose();
        _page = _doc.AddPage();
        _page.Size = PdfSharp.PageSize.A4;
        _g = XGraphics.FromPdfPage(_page);
        _pages.Add(_page);
        _y = Top;
    }

    public void EnsureSpace(double lines)
    {
        if (_y + lines * CodeLine > _page.Height.Point - Bottom) NewPage();
    }

    public void Cover(string title, string subtitle, IEnumerable<string> lines)
    {
        _g.DrawRectangle(new XSolidBrush(XColor.FromArgb(24, 32, 48)), 0, 0, _page.Width.Point, 330);
        _g.DrawString(title, _title, XBrushes.White, new XRect(Left, 110, Width, 50), XStringFormats.TopLeft);
        _g.DrawString(subtitle, new XFont("Calibri", 20, XFontStyleEx.Regular), new XSolidBrush(XColor.FromArgb(150, 190, 255)), new XRect(Left, 165, Width, 30), XStringFormats.TopLeft);
        // A little screen in the corner of the cover, like the machine's own.
        double sx = Left, sy = 220, sw = 220, sh = 70;
        _g.DrawRectangle(new XSolidBrush(XColor.FromArgb(10, 20, 10)), sx, sy, sw, sh);
        _g.DrawString("RADIO SHACK LEVEL II BASIC", _codeBold, new XSolidBrush(XColor.FromArgb(120, 255, 140)), sx + 10, sy + 18);
        _g.DrawString("READY", _codeBold, new XSolidBrush(XColor.FromArgb(120, 255, 140)), sx + 10, sy + 38);
        _g.DrawString(">_", _codeBold, new XSolidBrush(XColor.FromArgb(120, 255, 140)), sx + 10, sy + 50);
        _y = 360;
        foreach (var line in lines)
        {
            _g.DrawString(line, line.StartsWith("  ") ? _code : _body, new XSolidBrush(Ink), Left, _y);
            _y += line.StartsWith("  ") ? 11 : 15;
        }
        _running = "";
        NewPage();
        _running = "TRS-80 Level II BASIC in Starlark";
    }

    public void Heading(string text, int level)
    {
        double size = level == 1 ? 20 : 13.5;
        if (_y + size * 3 > _page.Height.Point - Bottom) NewPage();
        _y += level == 1 ? 4 : 10;
        _g.DrawString(text, level == 1 ? _h1 : _h2, new XSolidBrush(XColor.FromArgb(24, 32, 48)), Left, _y + size);
        _y += size + 8;
        if (level == 1)
        {
            _g.DrawLine(new XPen(Rule, 0.8), Left, _y - 2, Left + Width, _y - 2);
            _y += 6;
        }
    }

    public void Label(string text)
    {
        EnsureSpace(2);
        _g.DrawString(text, new XFont("Calibri", 9.5, XFontStyleEx.Italic), new XSolidBrush(Grey), Left, _y + 9);
        _y += 15;
    }

    public void Paragraph(string text)
    {
        foreach (var line in Wrap(text, _body, Width)) Body(line);
        _y += 5;
    }

    public void Bullets(IEnumerable<string> items)
    {
        foreach (var item in items)
        {
            var lines = Wrap(item, _body, Width - 14).ToList();
            for (int i = 0; i < lines.Count; i++)
            {
                if (_y + 14 > _page.Height.Point - Bottom) NewPage();
                if (i == 0) _g.DrawString("-", _bodyBold, new XSolidBrush(Grey), Left + 2, _y + 10);
                _g.DrawString(lines[i], _body, new XSolidBrush(Ink), Left + 14, _y + 10);
                _y += 14;
            }
            _y += 2;
        }
        _y += 4;
    }

    private void Body(string line)
    {
        if (_y + 14 > _page.Height.Point - Bottom) NewPage();
        _g.DrawString(line, _body, new XSolidBrush(Ink), Left, _y + 10);
        _y += 14;
    }

    private IEnumerable<string> Wrap(string text, XFont font, double width)
    {
        var line = new StringBuilder();
        foreach (var word in text.Split(' '))
        {
            string trial = line.Length == 0 ? word : line + " " + word;
            if (_g.MeasureString(trial, font).Width > width && line.Length > 0)
            {
                yield return line.ToString();
                line.Clear().Append(word);
            }
            else
            {
                line.Clear().Append(trial);
            }
        }
        if (line.Length > 0) yield return line.ToString();
    }

    /// <summary>Plain monospace text on a light panel, such as program output.</summary>
    public void Plain(string text)
    {
        var lines = text.Split('\n');
        double panelTop = _y;
        var brush = new XSolidBrush(XColor.FromArgb(244, 246, 244));
        foreach (var raw in lines)
        {
            if (_y + CodeLine > _page.Height.Point - Bottom) NewPage();
            _g.DrawRectangle(brush, Left, _y, Width, CodeLine);
            _g.DrawString(raw.TrimEnd().Replace("\t", "    "), _code, new XSolidBrush(XColor.FromArgb(20, 70, 30)), Left + 8, _y + 7.2);
            _y += CodeLine;
        }
        _y += 10;
    }

    /// <summary>Source code with colours and, optionally, line numbers. Lines too wide for the page are continued on the next line.</summary>
    public void Code(string text, IReadOnlyList<List<Span>> lines, bool numbered)
    {
        double gutter = numbered ? 30 : 0;
        int maxCols = (int)((Width - gutter - 8) / CodeAdvance);
        var panel = new XSolidBrush(XColor.FromArgb(250, 250, 252));
        for (int n = 0; n < lines.Count; n++)
        {
            var spans = lines[n];
            var rows = Split(spans, maxCols);
            for (int r = 0; r < rows.Count; r++)
            {
                if (_y + CodeLine > _page.Height.Point - Bottom) NewPage();
                _g.DrawRectangle(panel, Left, _y, Width, CodeLine);
                if (numbered)
                {
                    if (r == 0) _g.DrawString((n + 1).ToString(), _code, new XSolidBrush(XColor.FromArgb(160, 160, 170)), Left + gutter - 6 - _g.MeasureString((n + 1).ToString(), _code).Width, _y + 7.2);
                    _g.DrawLine(new XPen(Rule, 0.5), Left + gutter - 2, _y, Left + gutter - 2, _y + CodeLine);
                }
                double x = Left + gutter + 4 + (r > 0 ? 2 * CodeAdvance : 0);
                foreach (var s in rows[r])
                {
                    _g.DrawString(s.Text, s.Bold ? _codeBold : _code, new XSolidBrush(s.Colour), x, _y + 7.2);
                    x += s.Text.Length * CodeAdvance;
                }
                _y += CodeLine;
            }
        }
        _y += 8;
    }

    /// <summary>
    /// Breaks a line that is wider than the page into rows. A break happens at a space where there is one, so words stay whole; a
    /// continuation row is drawn indented by two columns, which is why its room is two columns smaller.
    /// </summary>
    private static List<List<Span>> Split(List<Span> spans, int maxCols)
    {
        string whole = string.Concat(spans.Select(s => s.Text));
        if (whole.Length <= maxCols) return [spans];

        // Where each row ends, chosen on the plain text.
        var cuts = new List<int>();
        int start = 0;
        while (whole.Length - start > (cuts.Count == 0 ? maxCols : maxCols - 2))
        {
            int room = cuts.Count == 0 ? maxCols : maxCols - 2;
            int cut = whole.LastIndexOf(' ', start + room - 1, room);
            if (cut <= start) cut = start + room;      // a single word longer than the row: break inside it
            else cut++;                                // keep the space at the end of this row
            cuts.Add(cut);
            start = cut;
        }

        // Cut the coloured spans at those places.
        var rows = new List<List<Span>> { new() };
        int position = 0, next = 0;
        foreach (var s in spans)
        {
            int from = 0;
            while (from < s.Text.Length)
            {
                int limit = next < cuts.Count ? cuts[next] : int.MaxValue;
                int take = (int)Math.Min(s.Text.Length - from, (long)limit - position);
                rows[^1].Add(s with { Text = s.Text.Substring(from, take) });
                from += take;
                position += take;
                if (next < cuts.Count && position == cuts[next]) { rows.Add([]); next++; }
            }
        }
        if (rows[^1].Count == 0 && rows.Count > 1) rows.RemoveAt(rows.Count - 1);
        return rows;
    }

    private PdfPage? _contents;

    /// <summary>Sets aside the page after the cover for the contents, which is written last so it can show real page numbers.</summary>
    public void ReserveContents()
    {
        // The cover has already started a new page; that page is the one reserved. (A second NewPage here left a blank page 2.)
        _contents = _page;
    }

    /// <summary>Writes the contents onto the reserved page.</summary>
    public void FillContents(string title, IEnumerable<(string Text, int Page)> entries)
    {
        using var g = XGraphics.FromPdfPage(_contents!, XGraphicsPdfPageOptions.Append);
        double y = Top + 4;
        g.DrawString(title, _h1, new XSolidBrush(XColor.FromArgb(24, 32, 48)), Left, y + 20);
        y += 36;
        g.DrawLine(new XPen(Rule, 0.8), Left, y, Left + Width, y);
        y += 14;
        foreach (var (text, page) in entries)
        {
            // The title in the body font, and under it, if there is a description after " - ", that in the small grey font, so nothing is cut.
            string[] parts = text.Split("  -  ", 2);
            g.DrawString(parts[0], _bodyBold, new XSolidBrush(Ink), Left, y + 10);
            string number = page.ToString();
            g.DrawString(number, _body, new XSolidBrush(Ink), Left + Width - g.MeasureString(number, _body).Width, y + 10);
            y += 16;
            if (parts.Length > 1)
            {
                g.DrawString(parts[1], _small, new XSolidBrush(Grey), Left + 12, y + 8);
                y += 14;
            }
            y += 6;
        }
    }

    /// <summary>Draws the running header and the page number on every page except the cover.</summary>
    public void Finish()
    {
        _g.Dispose();
        for (int i = 1; i < _doc.PageCount; i++)
        {
            using var g = XGraphics.FromPdfPage(_doc.Pages[i], XGraphicsPdfPageOptions.Append);
            var page = _doc.Pages[i];
            g.DrawString(_running, _small, new XSolidBrush(Grey), Left, 36);
            string n = (i + 1).ToString();
            g.DrawString(n, _small, new XSolidBrush(Grey), page.Width.Point - Right - g.MeasureString(n, _small).Width, page.Height.Point - 30);
            g.DrawLine(new XPen(Rule, 0.5), Left, 42, page.Width.Point - Right, 42);
        }
    }
}

/// <summary>
/// Finds fonts in the Windows font folder. PDFsharp 6 does not read installed fonts by itself: it asks a resolver which file holds a
/// family in a style. Only the families this document uses are listed; the files are embedded in the PDF, so it prints the same anywhere.
/// </summary>
sealed class WindowsFonts : IFontResolver
{
    private static readonly string Folder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    // family, bold, italic -> file name
    private static readonly Dictionary<(string, bool, bool), string> Files = new()
    {
        [("consolas", false, false)] = "consola.ttf", [("consolas", true, false)] = "consolab.ttf",
        [("consolas", false, true)] = "consolai.ttf", [("consolas", true, true)] = "consolaz.ttf",
        [("calibri", false, false)] = "calibri.ttf", [("calibri", true, false)] = "calibrib.ttf",
        [("calibri", false, true)] = "calibrii.ttf", [("calibri", true, true)] = "calibriz.ttf",
        [("cascadia mono", false, false)] = "CascadiaMono.ttf", [("cascadia mono", true, false)] = "CascadiaMono.ttf",
        [("courier new", false, false)] = "cour.ttf", [("courier new", true, false)] = "courbd.ttf",
        [("arial", false, false)] = "arial.ttf", [("arial", true, false)] = "arialbd.ttf",
    };

    public string DefaultFontName => "Calibri";

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        string family = familyName.ToLowerInvariant();
        foreach (var (b, i) in new[] { (isBold, isItalic), (isBold, false), (false, isItalic), (false, false) })
        {
            if (Files.TryGetValue((family, b, i), out var file) && File.Exists(Path.Combine(Folder, file)))
            {
                return new FontResolverInfo(file, mustSimulateBold: isBold && !b, mustSimulateItalic: isItalic && !i);
            }
        }
        return null;
    }

    public byte[]? GetFont(string faceName) => File.ReadAllBytes(Path.Combine(Folder, faceName));
}

/// <summary>A run of text in one colour.</summary>
readonly record struct Span(string Text, XColor Colour, bool Bold = false);

/// <summary>Splits source text into coloured runs, one list per line.</summary>
static class Highlighter
{
    private static readonly XColor Plain = XColor.FromArgb(34, 34, 34), Keyword = XColor.FromArgb(0, 92, 197), Str = XColor.FromArgb(3, 120, 70),
        Number = XColor.FromArgb(176, 80, 0), Comment = XColor.FromArgb(125, 135, 125), Func = XColor.FromArgb(120, 40, 160), Builtin = XColor.FromArgb(160, 30, 80),
        Punct = XColor.FromArgb(90, 90, 100);

    private static readonly HashSet<string> Builtins =
    [
        "len", "range", "str", "int", "float", "list", "dict", "tuple", "set", "sorted", "reversed", "enumerate", "zip", "min", "max", "abs",
        "any", "all", "type", "repr", "print", "fail", "hasattr", "getattr", "True", "False", "None", "ord", "chr", "bool", "load",
    ];

    /// <summary>Colours Starlark using the library's own lexer, so the classes are the interpreter's.</summary>
    public static List<List<Span>> Starlark(string file, string text)
    {
        var map = new CodeMap(file, text);
        var lexemes = Lexer.LexAll(map);
        var colours = new XColor?[text.Length];
        var bold = new bool[text.Length];
        string? previous = null;
        foreach (var l in lexemes)
        {
            if (l.Token is not { } t || l.End <= l.Start || l.End > text.Length) { continue; }
            XColor? c = null;
            bool b = false;
            switch (t.Kind)
            {
                case TokenKind.Comment: c = Comment; break;
                case TokenKind.String or TokenKind.Bytes or TokenKind.FStringStart or TokenKind.FStringText or TokenKind.FStringEnd: c = Str; break;
                case TokenKind.Int or TokenKind.Float: c = Number; break;
                case TokenKind.Identifier:
                    string name = text.Substring(l.Start, l.End - l.Start);
                    c = previous == "def" ? Func : Builtins.Contains(name) ? Builtin : Plain;
                    b = previous == "def";
                    break;
                case >= TokenKind.And and <= TokenKind.Return: c = Keyword; b = true; break;
                case >= TokenKind.Comma and <= TokenKind.ClosingRound: c = Punct; break;
            }
            previous = t.Kind == TokenKind.Def ? "def" : t.Kind is TokenKind.Newline or TokenKind.Indent or TokenKind.Dedent ? previous : null;
            if (c is null) continue;
            for (int i = l.Start; i < l.End; i++) { colours[i] = c; bold[i] = b; }
        }
        return Lines(text, colours, bold);
    }

    private static List<List<Span>> Lines(string text, XColor?[] colours, bool[] bold)
    {
        var result = new List<List<Span>>();
        foreach (var raw in text.Split('\n'))
        {
            result.Add([]);
            result[^1].Add(new Span("", Plain));
        }
        int line = 0, start = 0;
        var cur = new List<Span>();
        result.Clear();
        for (int i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || text[i] == '\n')
            {
                Flush(text, start, i, colours, bold, cur);
                result.Add(cur);
                cur = [];
                start = i + 1;
                line++;
            }
        }
        return result;
    }

    private static void Flush(string text, int from, int to, XColor?[] colours, bool[] bold, List<Span> into)
    {
        int i = from;
        while (i < to)
        {
            var c = colours[i] ?? Plain;
            bool b = bold[i];
            int j = i + 1;
            while (j < to && (colours[j] ?? Plain).Equals(c) && bold[j] == b) j++;
            into.Add(new Span(text[i..j].Replace("\t", "    "), c, b));
            i = j;
        }
        if (into.Count == 0) into.Add(new Span("", Plain));
    }

    private static readonly string[] BasicKeywords =
    [
        "PRINT", "FOR", "TO", "STEP", "NEXT", "IF", "THEN", "ELSE", "GOTO", "GOSUB", "RETURN", "ON", "INPUT", "DATA", "READ", "RESTORE", "DIM", "DEF",
        "FN", "LET", "REM", "END", "STOP", "RUN", "LIST", "NEW", "CLS", "SET", "RESET", "POKE", "RANDOM", "AND", "OR", "NOT", "DEFINT", "DEFSTR",
    ];

    private static readonly string[] BasicFunctions =
    [
        "SIN", "COS", "TAN", "ATN", "EXP", "LOG", "SQR", "RND", "INT", "ABS", "SGN", "LEFT$", "MID$", "RIGHT$", "LEN", "ASC", "CHR$", "STR$", "VAL",
        "TAB", "POINT", "PEEK", "INSTR", "STRING$",
    ];

    /// <summary>Colours a BASIC program: line numbers, keywords, functions, strings, numbers and REM comments.</summary>
    public static List<List<Span>> Basic(string text)
    {
        var result = new List<List<Span>>();
        foreach (var line in text.Split('\n'))
        {
            var spans = new List<Span>();
            int i = 0;
            while (i < line.Length && char.IsDigit(line[i])) i++;
            if (i > 0) spans.Add(new Span(line[..i], Number, true));
            while (i < line.Length)
            {
                char c = line[i];
                if (c == '"')
                {
                    int j = line.IndexOf('"', i + 1);
                    j = j < 0 ? line.Length : j + 1;
                    spans.Add(new Span(line[i..j], Str));
                    i = j;
                }
                else if (char.IsLetter(c))
                {
                    int j = i;
                    while (j < line.Length && (char.IsLetterOrDigit(line[j]) || line[j] == '$')) j++;
                    string word = line[i..j];
                    if (word == "REM")
                    {
                        spans.Add(new Span(line[i..], Comment));
                        i = line.Length;
                        continue;
                    }
                    spans.Add(new Span(word,
                        BasicKeywords.Contains(word) ? Keyword : BasicFunctions.Contains(word) ? Func : Plain,
                        BasicKeywords.Contains(word)));
                    i = j;
                }
                else if (char.IsDigit(c) || (c == '.' && i + 1 < line.Length && char.IsDigit(line[i + 1])))
                {
                    int j = i;
                    while (j < line.Length && (char.IsDigit(line[j]) || line[j] == '.')) j++;
                    spans.Add(new Span(line[i..j], Number));
                    i = j;
                }
                else
                {
                    int j = i + 1;
                    while (j < line.Length && !char.IsLetterOrDigit(line[j]) && line[j] != '"' && line[j] != '.') j++;
                    spans.Add(new Span(line[i..j], Punct));
                    i = j;
                }
            }
            if (spans.Count == 0) spans.Add(new Span("", Plain));
            result.Add(spans);
        }
        return result;
    }
}
