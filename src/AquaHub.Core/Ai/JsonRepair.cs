using System.Text;

namespace AquaHub.Core.Ai;

/// <summary>
/// Deterministic repair for the near-miss JSON that small local models occasionally emit when the
/// server doesn't enforce a grammar: missing opening quotes on string values, unescaped quotes or raw
/// newlines inside strings, trailing commas, and outputs truncated by the token limit.
/// </summary>
public static class JsonRepair
{
    public static string Repair(string input)
    {
        var s = input.Trim();
        var sb = new StringBuilder(s.Length + 16);
        var stack = new Stack<char>();
        var inString = false;
        var expectingValue = false; // after ':' or '[' / ',' inside an array
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (inString)
            {
                if (ch == '\\' && i + 1 < s.Length)
                {
                    sb.Append(ch).Append(s[++i]);
                    continue;
                }
                if (ch == '"')
                {
                    var next = NextNonSpace(s, i + 1);
                    if (next is '\0' or ',' or '}' or ']' or ':')
                    {
                        sb.Append('"');
                        inString = false;
                        expectingValue = false;
                    }
                    else sb.Append("\\\""); // inner quote
                    continue;
                }
                if (ch == '\n') { sb.Append("\\n"); continue; }
                if (ch == '\r') continue;
                if (ch == '\t') { sb.Append("\\t"); continue; }
                sb.Append(ch);
                continue;
            }

            switch (ch)
            {
                case '"':
                    inString = true;
                    sb.Append(ch);
                    break;
                case '{':
                case '[':
                    stack.Push(ch == '{' ? '}' : ']');
                    expectingValue = ch == '[';
                    sb.Append(ch);
                    break;
                case '}':
                case ']':
                    TrimTrailingComma(sb);
                    if (stack.Count > 0) stack.Pop();
                    expectingValue = false;
                    sb.Append(ch);
                    break;
                case ':':
                    expectingValue = true;
                    sb.Append(ch);
                    break;
                case ',':
                    expectingValue = stack.Count > 0 && stack.Peek() == ']';
                    sb.Append(ch);
                    break;
                default:
                    if (expectingValue && char.IsLetter(ch) && !StartsLiteral(s, i))
                    {
                        // Unquoted string value: open it and let the string state machine close it.
                        sb.Append('"').Append(ch);
                        inString = true;
                        expectingValue = false;
                        break;
                    }
                    if (!char.IsWhiteSpace(ch)) expectingValue = false;
                    sb.Append(ch);
                    break;
            }
        }
        if (inString) sb.Append('"');
        TrimTrailingComma(sb);
        while (stack.Count > 0) sb.Append(stack.Pop());
        return sb.ToString();
    }

    private static bool StartsLiteral(string s, int i) =>
        Matches(s, i, "true") || Matches(s, i, "false") || Matches(s, i, "null");

    private static bool Matches(string s, int i, string word) =>
        i + word.Length <= s.Length && string.CompareOrdinal(s, i, word, 0, word.Length) == 0 &&
        (i + word.Length == s.Length || !char.IsLetterOrDigit(s[i + word.Length]));

    private static char NextNonSpace(string s, int from)
    {
        for (var i = from; i < s.Length; i++)
            if (!char.IsWhiteSpace(s[i])) return s[i];
        return '\0';
    }

    private static void TrimTrailingComma(StringBuilder sb)
    {
        var i = sb.Length - 1;
        while (i >= 0 && char.IsWhiteSpace(sb[i])) i--;
        if (i >= 0 && sb[i] == ',') sb.Length = i;
    }
}
