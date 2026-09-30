using System.Globalization;
using System.Text;

namespace AquaHub.Core.Analysis;

public static partial class TextTools
{
    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "a","about","above","after","again","against","all","also","am","an","and","any","are","as","at","be","because","been",
        "before","being","below","between","both","but","by","can","could","did","do","does","doing","down","during","each","few",
        "for","from","further","had","has","have","having","he","her","here","hers","herself","him","himself","his","how","i","if",
        "in","into","is","it","its","itself","just","me","more","most","my","myself","no","nor","not","now","of","off","on","once",
        "only","or","other","our","ours","ourselves","out","over","own","same","she","should","so","some","such","than","that","the",
        "their","theirs","them","themselves","then","there","these","they","this","those","through","to","too","under","until","up",
        "very","was","we","were","what","when","where","which","while","who","whom","why","will","with","would","you","your","yours",
        "yourself","yourselves","says","said","say","new","news","live","latest","update","updates","report","reports","reported",
        "video","watch","breaking","exclusive","opinion","analysis","explainer","how","why","what","amid","after","over","year","years",
        "day","days","week","weeks","first","last","one","two","get","gets","got","make","makes","made","may","might","must","us",
        "could","within","via","per","vs","like","back","set","still","take","takes","according","including","since","despite",
        "yet","around","across","another","many","much","way","ways","call","calls","called","time","times","look","looks","here's",
        "don't","it's","i'm","you're","isn't","aren't","won't","can't","didn't","doesn't","s","t","re","ll","ve","d","m",
        // social filler & URL fragments
        "https","http","www","com","org","net","html","amp","anyone","someone","everyone","people","thing","things","really",
        "think","going","want","know","need","good","great","best","lol","yes","yeah","today","tonight","tomorrow","post",
        "posted","thread","comments","comment","link","reddit","submitted","image","video","gets","got","also","even","well",
    };

    public static bool IsStopword(string token) => Stop.Contains(token);

    /// <summary>Lower-cased word tokens (letters/digits, inner apostrophes kept), diacritics folded.</summary>
    public static List<string> Tokenize(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(text)) return list;
        var s = Fold(text);
        var sb = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (char.IsLetterOrDigit(ch) || (ch is '\'' or '’' && sb.Length > 0 && i + 1 < s.Length && char.IsLetter(s[i + 1])))
            {
                sb.Append(char.ToLowerInvariant(ch == '’' ? '\'' : ch));
            }
            else if (sb.Length > 0)
            {
                list.Add(sb.ToString());
                sb.Clear();
            }
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }

    /// <summary>Removes diacritics (Dáil → Dail) so matching is robust.</summary>
    public static string Fold(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Light stemmer: strips possessives and simple plurals so "rates"/"rate" and "Ireland's"/"Ireland" match.</summary>
    public static string Stem(string token)
    {
        if (token.EndsWith("'s", StringComparison.Ordinal)) token = token[..^2];
        if (token.Length > 5 && token.EndsWith("ies", StringComparison.Ordinal)) return token[..^3] + "y";
        if (token.Length > 4 && token.EndsWith('s') && !token.EndsWith("ss", StringComparison.Ordinal) && !token.EndsWith("us", StringComparison.Ordinal))
            return token[..^1];
        return token;
    }

    /// <summary>Content-bearing, stemmed tokens used for similarity.</summary>
    public static HashSet<string> Signature(string? text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in Tokenize(text))
        {
            if (Stop.Contains(t)) continue;
            if (t.Length < 3 && !t.All(char.IsDigit)) continue;
            set.Add(Stem(t));
        }
        return set;
    }

    public static double Jaccard(IReadOnlySet<string> a, IReadOnlySet<string> b, out int overlap)
    {
        overlap = 0;
        if (a.Count == 0 || b.Count == 0) return 0;
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        foreach (var t in small) if (large.Contains(t)) overlap++;
        return overlap / (double)(a.Count + b.Count - overlap);
    }

    public static bool ContainsPhrase(string haystackFolded, string phrase)
    {
        var p = Fold(phrase).ToLowerInvariant().Trim();
        if (p.Length == 0) return false;
        var idx = 0;
        while ((idx = haystackFolded.IndexOf(p, idx, StringComparison.Ordinal)) >= 0)
        {
            var before = idx == 0 || !char.IsLetterOrDigit(haystackFolded[idx - 1]);
            var afterIdx = idx + p.Length;
            var after = afterIdx >= haystackFolded.Length || !char.IsLetterOrDigit(haystackFolded[afterIdx]);
            if (before && after) return true;
            idx = afterIdx;
        }
        return false;
    }

    public static List<string> Sentences(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        var sb = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            sb.Append(ch);
            var end = ch is '.' or '!' or '?' or '…';
            if (end && (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1])))
            {
                // Avoid splitting on common abbreviations and initials ("U.S.", "Mr.", "No. 10").
                var s = sb.ToString().TrimEnd();
                var lastWord = s.Split(' ').LastOrDefault() ?? "";
                if (lastWord.Length <= 3 && lastWord.Count(c => c == '.') >= 1 && char.IsUpper(lastWord.FirstOrDefault())) continue;
                if (lastWord is "Mr." or "Mrs." or "Ms." or "Dr." or "St." or "No." or "vs.") continue;
                var sentence = s.Trim();
                if (sentence.Length > 0) list.Add(sentence);
                sb.Clear();
            }
        }
        var rest = sb.ToString().Trim();
        if (rest.Length > 0) list.Add(rest);
        return list;
    }

    /// <summary>
    /// Frequency-based extractive summary used when no local model is available: returns up to
    /// <paramref name="count"/> diverse, informative sentences in rank order.
    /// </summary>
    /// <summary>Feed furniture that isn't part of the story ("Read more: …", "Related: …", "Sign up for …").</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^\W*(?:(?:read more|read next|also read|see also|more on this|sign up|subscribe|click here|follow us|advertisement)\b|(?:related|recommended|watch|listen)\s*[:|\-–—])", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex Boilerplate();

    public static List<string> Extract(IEnumerable<string> documents, IEnumerable<string> emphasis, int count = 3, int maxChars = 220)
    {
        var docs = documents.Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
        var freq = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var t in emphasis.SelectMany(Signature)) freq[t] = freq.GetValueOrDefault(t) + 2;
        foreach (var t in docs.SelectMany(Signature)) freq[t] = freq.GetValueOrDefault(t) + 1;

        var candidates = docs.SelectMany(Sentences)
            .Select(s => s.Trim())
            .Where(s => s.Length is >= 40 and <= 400 && !s.Contains("Continue reading", StringComparison.OrdinalIgnoreCase)
                        && !s.StartsWith("Photo", StringComparison.OrdinalIgnoreCase) && !Boilerplate().IsMatch(s))
            .Distinct()
            .Select(s =>
            {
                var sig = Signature(s);
                var score = sig.Sum(t => freq.GetValueOrDefault(t)) / Math.Sqrt(Math.Max(6, sig.Count));
                return (Sentence: s, Sig: sig, Score: score);
            })
            .OrderByDescending(x => x.Score)
            .ToList();

        var picked = new List<(string Sentence, HashSet<string> Sig)>();
        foreach (var c in candidates)
        {
            if (picked.Any(p => Jaccard(p.Sig, c.Sig, out _) > 0.45)) continue;
            picked.Add((c.Sentence, c.Sig));
            if (picked.Count >= count) break;
        }
        return picked.Select(p => Util.HtmlText.Truncate(p.Sentence, maxChars)).ToList();
    }

    /// <summary>Most characteristic unigrams/bigrams across short texts (used for the fallback social pulse).</summary>
    public static List<(string Term, int Count)> TopTerms(IEnumerable<string> texts, int take = 8, IEnumerable<string>? exclude = null)
    {
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in exclude ?? Enumerable.Empty<string>())
            foreach (var t in Tokenize(e)) excluded.Add(t);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var text in texts)
        {
            var toks = Tokenize(text).Where(t => !Stop.Contains(t) && !excluded.Contains(t) && t.Length > 2 && !t.All(char.IsDigit)).ToList();
            var seen = new HashSet<string>();
            for (var i = 0; i < toks.Count; i++)
            {
                if (seen.Add(toks[i])) counts[toks[i]] = counts.GetValueOrDefault(toks[i]) + 1;
                if (i + 1 < toks.Count)
                {
                    var bi = toks[i] + " " + toks[i + 1];
                    if (seen.Add(bi)) counts[bi] = counts.GetValueOrDefault(bi) + 2;
                }
            }
        }
        return counts.Where(kv => kv.Value >= 2)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => (kv.Key, kv.Value))
            .Aggregate(new List<(string, int)>(), (acc, kv) =>
            {
                // Drop unigrams already covered by a chosen bigram.
                if (!acc.Any(a => a.Item1.Contains(kv.Key, StringComparison.Ordinal) || kv.Key.Contains(a.Item1, StringComparison.Ordinal)))
                    acc.Add(kv);
                return acc;
            })
            .Take(take)
            .ToList();
    }
}
