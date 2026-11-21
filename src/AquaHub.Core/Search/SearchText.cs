using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace AquaHub.Core.Search;

internal static partial class SearchText
{
    internal const int Version = 1, InputLimit = 8 << 20, CharacterLimit = 32 * 1024;
    private static readonly string[] Text = [".txt", ".md", ".markdown", ".csv", ".tsv"];
    private static readonly string[] Office = [".docx", ".pptx", ".xlsx", ".odt", ".odp", ".ods"];
    public static bool Supports(string path) => Text.Concat(Office).Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static List<string> Extract(string path, Stream stream, CancellationToken ct)
    {
        if (stream.Length > InputLimit || !Supports(path)) return [];
        var watch = Stopwatch.StartNew();
        void Check()
        {
            ct.ThrowIfCancellationRequested();
            if (watch.Elapsed > TimeSpan.FromSeconds(3)) throw new InvalidDataException("Extraction time limit reached.");
        }
        var buffer = new StringBuilder();
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (Text.Contains(ext))
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, leaveOpen: true);
            var chars = new char[CharacterLimit];
            var count = reader.ReadBlock(chars, 0, chars.Length);
            if (Array.IndexOf(chars, '\0', 0, count) >= 0) return [];
            buffer.Append(chars, 0, count);
        }
        else
        {
            CheckDirectorySize(stream);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, true);
            if (zip.Entries.Count > 2048 || zip.Entries.Sum(e => (double)e.Length) > 16 << 20)
                throw new InvalidDataException("Document exceeds extraction limits.");
            var strings = new List<string>();
            foreach (var entry in zip.Entries.OrderBy(e => e.FullName == "xl/sharedStrings.xml" ? 0 : 1)
                .ThenBy(e => e.FullName, StringComparer.Ordinal))
            {
                Check();
                var name = entry.FullName;
                if (!(ext switch
                {
                    ".docx" => name == "word/document.xml",
                    ".pptx" => Slide().IsMatch(name),
                    ".xlsx" => name == "xl/sharedStrings.xml" || Sheet().IsMatch(name),
                    _ => name == "content.xml",
                })) continue;
                using var part = entry.Open();
                using var xml = XmlReader.Create(part, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 4 << 20,
                });
                var item = new StringBuilder();
                bool sharedCell = false;
                int paragraphDepth = -1;
                string element = "";
                while (xml.Read() && buffer.Length < CharacterLimit)
                {
                    Check();
                    if (xml.NodeType == XmlNodeType.Element)
                    {
                        element = xml.LocalName;
                        if (name == "content.xml" && element is "p" or "h" && !xml.IsEmptyElement &&
                            xml.NamespaceURI == "urn:oasis:names:tc:opendocument:xmlns:text:1.0") paragraphDepth = xml.Depth;
                        if (element == "c") sharedCell = xml.GetAttribute("t") == "s";
                        if (element == "si") item.Clear();
                    }
                    if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName == "si" && name == "xl/sharedStrings.xml")
                    {
                        if (strings.Count >= 16384) throw new InvalidDataException("Too many spreadsheet strings.");
                        strings.Add(item.ToString());
                    }
                    if (xml.NodeType == XmlNodeType.EndElement && xml.Depth == paragraphDepth) paragraphDepth = -1;
                    if (xml.NodeType is not (XmlNodeType.Text or XmlNodeType.CDATA) || name == "content.xml" && paragraphDepth < 0) continue;
                    var value = xml.Value;
                    if (name == "xl/sharedStrings.xml")
                    {
                        if (element == "t") item.Append(value.AsSpan(0, Math.Min(value.Length, Math.Max(0, 2048 - item.Length))));
                        continue;
                    }
                    if (name != "content.xml" && element is not ("t" or "v")) continue;
                    if (sharedCell && element == "v")
                        value = int.TryParse(value, out int n) && n >= 0 && n < strings.Count ? strings[n] : "";
                    buffer.Append(value.AsSpan(0, Math.Min(value.Length, CharacterLimit - buffer.Length))).Append(' ');
                }
                if (buffer.Length >= CharacterLimit) break;
            }
        }
        Check();
        var result = new List<string>();
        int retainedBytes = 0;
        var chunk = new StringBuilder();
        foreach (var word in Words().Matches(buffer.ToString()).Cast<Match>())
        {
            retainedBytes += Encoding.UTF8.GetByteCount(word.Value) + 1;
            if (retainedBytes > 64 * 1024) break;
            if (chunk.Length + word.Length > 640 && chunk.Length > 0) { result.Add(chunk.ToString()); chunk.Clear(); }
            if (chunk.Length > 0) chunk.Append(' ');
            chunk.Append(word.Value);
        }
        if (chunk.Length > 0) result.Add(chunk.ToString());
        return result;
    }

    // Check the central directory budget before ZipArchive allocates an object for every member.
    private static void CheckDirectorySize(Stream stream)
    {
        int size = (int)Math.Min(stream.Length, 65535 + 22);
        var tail = new byte[size];
        stream.Seek(-size, SeekOrigin.End);
        stream.ReadExactly(tail);
        stream.Position = 0;
        for (int i = size - 22; i >= 0; i--)
        {
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) != 0x06054b50) continue;
            ushort Field(int offset) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + offset));
            if (i + 22 + Field(20) != size) continue;
            if (Field(4) != 0 || Field(6) != 0 || Field(8) != Field(10) || Field(10) > 2048 ||
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 12)) > 512 * 1024)
                throw new InvalidDataException("Document directory exceeds extraction limits.");
            return;
        }
        throw new InvalidDataException("Incomplete document package.");
    }

    [GeneratedRegex(@"\S{1,480}")] private static partial Regex Words();
    [GeneratedRegex(@"^ppt/slides/slide[0-9]+\.xml$")] private static partial Regex Slide();
    [GeneratedRegex(@"^xl/worksheets/sheet[0-9]+\.xml$")] private static partial Regex Sheet();
}
