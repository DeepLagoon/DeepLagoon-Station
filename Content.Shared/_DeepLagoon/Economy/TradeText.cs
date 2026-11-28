using System.Text;

namespace Content.Shared._DeepLagoon.Economy;

/// <summary>Player listings are plain text in both UI implementations.</summary>
public static class TradeText
{
    public static string Plain(string text)
    {
        text = text.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
            .Replace("&#39;", "'").Replace("&nbsp;", " ").Replace("&amp;", "&");
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '<' || i + 1 == text.Length) { result.Append(text[i]); continue; }
            var start = i + 1;
            if (text[start] == '/') start++;
            if (start == text.Length || !(text[start] is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '!'))
            { result.Append(text[i]); continue; }
            if (text.IndexOf("<!--", i, StringComparison.Ordinal) == i)
            {
                var commentEnd = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                if (commentEnd < 0) break;
                i = commentEnd + 2; continue;
            }
            var end = start; var quote = '\0';
            for (; end < text.Length; end++)
            {
                var c = text[end];
                if (quote != '\0') { if (c == quote) quote = '\0'; }
                else if (c is '\'' or '"') quote = c;
                else if (c == '>') break;
            }
            if (end == text.Length) { result.Append(text[i]); continue; }
            var nameEnd = start;
            while (nameEnd < end && char.IsLetterOrDigit(text[nameEnd])) nameEnd++;
            var name = text.Substring(start, nameEnd - start).ToLowerInvariant();
            if (text[i + 1] != '/' && name is "script" or "style")
            {
                var close = text.IndexOf("</" + name, end + 1, StringComparison.OrdinalIgnoreCase);
                if (close < 0) break;
                end = text.IndexOf('>', close); if (end < 0) break;
            }
            if (name is "br" or "p" or "div" or "li" && result.Length > 0 && !char.IsWhiteSpace(result[result.Length - 1])) result.Append(' ');
            i = end;
        }
        return result.ToString().Trim();
    }
}
