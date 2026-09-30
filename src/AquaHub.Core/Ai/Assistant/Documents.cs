using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using AquaHub.Core.Util;

namespace AquaHub.Core.Ai.Assistant;

/// <summary>
/// Text out of the documents people attach or Ask finds on the PC: plain-text formats (notes, CSV, JSON, code, logs),
/// HTML, Word/PowerPoint/Excel (.docx/.pptx/.xlsx) and OpenDocument files — read directly from their XML, nothing is
/// opened in another program and no macros run. PDFs and images are read by the platform (OCR); see <see cref="IAskPlatform"/>.
/// </summary>
public static partial class Documents
{
    public const int MaxChars = 120_000;
    private const long MaxEntryBytes = 40 * 1024 * 1024;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".jsonc", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".conf", ".toml", ".log",
        ".cs", ".js", ".ts", ".tsx", ".jsx", ".py", ".java", ".kt", ".c", ".h", ".cpp", ".hpp", ".go", ".rs", ".rb", ".php", ".sql",
        ".ps1", ".psm1", ".sh", ".bat", ".cmd", ".css", ".scss", ".less", ".tex", ".srt", ".vtt", ".xaml", ".csproj", ".props", ".gitignore",
        ".r", ".m", ".swift", ".lua", ".dart", ".vue", ".svelte", ".env.example",
    };

    private static readonly HashSet<string> HtmlExtensions = new(StringComparer.OrdinalIgnoreCase) { ".html", ".htm", ".xhtml", ".mhtml" };
    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".heic" };
    private static readonly HashSet<string> OfficeExtensions = new(StringComparer.OrdinalIgnoreCase) { ".docx", ".docm", ".pptx", ".xlsx", ".xlsm", ".odt", ".ods", ".odp", ".rtf" };

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));
    public static bool IsPdf(string path) => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Formats read here (PDFs and images need the platform).</summary>
    public static bool CanRead(string path)
    {
        var ext = Path.GetExtension(path);
        return TextExtensions.Contains(ext) || HtmlExtensions.Contains(ext) || OfficeExtensions.Contains(ext);
    }

    public static bool IsSupported(string path) => CanRead(path) || IsPdf(path) || IsImage(path);

    /// <summary>A short description of what can be attached, for file dialogs and errors.</summary>
    public const string SupportedSummary = "text, Markdown, CSV, JSON, code, HTML, Word, Excel, PowerPoint, OpenDocument, PDF and images";

    /// <summary>Extracts readable text (capped at <see cref="MaxChars"/>). Throws <see cref="NotSupportedException"/> for other formats.</summary>
    public static string ReadText(string path)
    {
        var ext = Path.GetExtension(path);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("File not found", path);
        if (TextExtensions.Contains(ext) || (ext.Length == 0 && info.Length < 2_000_000 && LooksLikeText(path)))
        {
            if (info.Length > 20_000_000) throw new NotSupportedException("That file is too large to read (over 20 MB)");
            return Cap(ReadAllTextShared(path));
        }
        if (HtmlExtensions.Contains(ext)) return Cap(HtmlText.ToPlain(ReadAllTextShared(path), MaxChars, keepLines: true));
        return ext.ToLowerInvariant() switch
        {
            ".docx" or ".docm" => Cap(Docx(path)),
            ".pptx" => Cap(Pptx(path)),
            ".xlsx" or ".xlsm" => Cap(Xlsx(path)),
            ".odt" or ".ods" or ".odp" => Cap(OpenDocument(path)),
            ".rtf" => Cap(Rtf(ReadAllTextShared(path))),
            _ => throw new NotSupportedException($"Aqua can't read {(ext.Length > 0 ? ext : "that kind of")} files yet"),
        };
    }

    private static string Cap(string text) => text.Length <= MaxChars ? text.Trim() : text[..MaxChars].TrimEnd() + "\n…";

    /// <summary>Reads a file another program may have open (Word, Excel) without locking it.</summary>
    private static string ReadAllTextShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[MaxChars + 1];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, read);
    }

    private static bool LooksLikeText(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buf = new byte[4096];
        var n = fs.Read(buf, 0, buf.Length);
        return n > 0 && !buf.Take(n).Any(b => b == 0);
    }

    private static ZipArchive OpenZip(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), ZipArchiveMode.Read);

    private static XDocument LoadEntry(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxEntryBytes) throw new NotSupportedException("That document is too large to read");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxEntryBytes });
        return XDocument.Load(reader);
    }

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    internal static string Docx(string path)
    {
        using var zip = OpenZip(path);
        var entry = zip.GetEntry("word/document.xml") ?? throw new NotSupportedException("That Word file has no document body");
        var doc = LoadEntry(entry);
        var sb = new StringBuilder();
        foreach (var p in doc.Descendants(W + "p"))
        {
            var line = new StringBuilder();
            foreach (var node in p.Descendants())
            {
                if (node.Name == W + "t") line.Append(node.Value);
                else if (node.Name == W + "tab") line.Append('\t');
                else if (node.Name == W + "br" || node.Name == W + "cr") line.Append('\n');
            }
            var style = p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val")?.Value ?? "";
            var text = line.ToString().TrimEnd();
            if (text.Length == 0) continue;
            if (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) || style.Equals("Title", StringComparison.OrdinalIgnoreCase)) sb.Append("## ");
            else if (p.Element(W + "pPr")?.Element(W + "numPr") is not null) sb.Append("• ");
            sb.Append(text).Append("\n\n");
            if (sb.Length > MaxChars) break;
        }
        return sb.ToString();
    }

    internal static string Pptx(string path)
    {
        using var zip = OpenZip(path);
        var slides = zip.Entries.Where(e => SlidePattern().IsMatch(e.FullName))
            .OrderBy(e => int.Parse(SlidePattern().Match(e.FullName).Groups[1].Value)).ToList();
        var sb = new StringBuilder();
        var n = 0;
        foreach (var entry in slides)
        {
            n++;
            var doc = LoadEntry(entry);
            var paragraphs = doc.Descendants(A + "p").Select(p => string.Concat(p.Descendants(A + "t").Select(t => t.Value)).Trim()).Where(t => t.Length > 0).ToList();
            if (paragraphs.Count == 0) continue;
            sb.Append("## Slide ").Append(n).Append('\n');
            foreach (var p in paragraphs) sb.Append(p).Append('\n');
            sb.Append('\n');
            if (sb.Length > MaxChars) break;
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"^ppt/slides/slide(\d+)\.xml$")]
    private static partial Regex SlidePattern();

    internal static string Xlsx(string path, int maxRowsPerSheet = 400)
    {
        using var zip = OpenZip(path);
        var shared = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is { } sst)
            foreach (var si in LoadEntry(sst).Root!.Elements(S + "si"))
                shared.Add(string.Concat(si.Descendants(S + "t").Select(t => t.Value)));

        // Sheet names in workbook order, mapped to their parts through the relationships file.
        var names = new List<(string Name, string Part)>();
        if (zip.GetEntry("xl/workbook.xml") is { } wb && zip.GetEntry("xl/_rels/workbook.xml.rels") is { } rels)
        {
            var targets = LoadEntry(rels).Root!.Elements().ToDictionary(e => (string?)e.Attribute("Id") ?? "", e => (string?)e.Attribute("Target") ?? "");
            foreach (var sheet in LoadEntry(wb).Descendants(S + "sheet"))
                if (targets.TryGetValue((string?)sheet.Attribute(R + "id") ?? "", out var target))
                    names.Add(((string?)sheet.Attribute("name") ?? "Sheet", "xl/" + target.TrimStart('/').Replace("xl/", "")));
        }
        if (names.Count == 0)
            names = zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal)).Select(e => (Path.GetFileNameWithoutExtension(e.Name), e.FullName)).ToList();

        var sb = new StringBuilder();
        foreach (var (name, part) in names)
        {
            if (zip.GetEntry(part) is not { } entry) continue;
            sb.Append("## ").Append(name).Append('\n');
            var rows = 0;
            foreach (var row in LoadEntry(entry).Descendants(S + "row"))
            {
                var cells = row.Elements(S + "c").Select(c =>
                {
                    var type = (string?)c.Attribute("t");
                    var v = c.Element(S + "v")?.Value ?? "";
                    return type switch
                    {
                        "s" when int.TryParse(v, out var i) && i >= 0 && i < shared.Count => shared[i],
                        "inlineStr" => string.Concat(c.Descendants(S + "t").Select(t => t.Value)),
                        _ => v,
                    };
                }).ToList();
                if (cells.All(c => c.Length == 0)) continue;
                sb.Append(string.Join('\t', cells)).Append('\n');
                if (++rows >= maxRowsPerSheet) { sb.Append("… (more rows)\n"); break; }
            }
            sb.Append('\n');
            if (sb.Length > MaxChars) break;
        }
        return sb.ToString();
    }

    internal static string OpenDocument(string path)
    {
        using var zip = OpenZip(path);
        var entry = zip.GetEntry("content.xml") ?? throw new NotSupportedException("That OpenDocument file has no content");
        XNamespace text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
        XNamespace table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
        var doc = LoadEntry(entry);
        var sb = new StringBuilder();
        foreach (var el in doc.Descendants().Where(e => e.Name == text + "h" || e.Name == text + "p" || e.Name == table + "table-row"))
        {
            if (el.Name == table + "table-row")
            {
                var cells = el.Elements(table + "table-cell").Select(c => string.Join(" ", c.Elements(text + "p").Select(p => p.Value))).ToList();
                if (cells.Any(c => c.Length > 0)) sb.Append(string.Join('\t', cells).TrimEnd('\t')).Append('\n');
                continue;
            }
            if (el.Ancestors(table + "table-row").Any()) continue;
            var value = el.Value.Trim();
            if (value.Length == 0) continue;
            if (el.Name == text + "h") sb.Append("## ");
            sb.Append(value).Append("\n\n");
            if (sb.Length > MaxChars) break;
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"\\'([0-9a-fA-F]{2})")]
    private static partial Regex RtfHex();

    [GeneratedRegex(@"\\[a-zA-Z]+-?\d* ?")]
    private static partial Regex RtfControl();

    /// <summary>Plain text out of RTF: groups for fonts, colours and pictures dropped, paragraphs kept.</summary>
    internal static string Rtf(string rtf)
    {
        var sb = new StringBuilder(rtf.Length / 2);
        var depth = 0;
        var skipDepth = -1;
        for (var i = 0; i < rtf.Length; i++)
        {
            var ch = rtf[i];
            if (ch == '{') { depth++; continue; }
            if (ch == '}') { if (depth == skipDepth) skipDepth = -1; depth--; continue; }
            if (skipDepth >= 0) continue;
            if (ch == '\\' && i + 1 < rtf.Length)
            {
                var rest = rtf.AsSpan(i + 1);
                if (rest.StartsWith("fonttbl") || rest.StartsWith("colortbl") || rest.StartsWith("stylesheet") || rest.StartsWith("pict") ||
                    rest.StartsWith("info") || rest.StartsWith("*"))
                { skipDepth = depth; continue; }
                if (rest.StartsWith("par") || rest.StartsWith("line")) sb.Append('\n');
                else if (rest.StartsWith("tab")) sb.Append('\t');
                var m = RtfHex().Match(rtf, i);
                if (m.Success && m.Index == i) { sb.Append((char)Convert.ToInt32(m.Groups[1].Value, 16)); i += m.Length - 1; continue; }
                var c = RtfControl().Match(rtf, i);
                if (c.Success && c.Index == i) { i += c.Length - 1; continue; }
                if (i + 1 < rtf.Length && rtf[i + 1] is '\\' or '{' or '}') { sb.Append(rtf[i + 1]); i++; }
                continue;
            }
            if (ch is '\r' or '\n') continue;
            sb.Append(ch);
        }
        return Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n").Trim();
    }
}
