namespace Bilgames.Core.Analysis;

public enum QuoteStyle
{
    Double,
    Single,
}

public readonly record struct Span(string Text, bool IsQuote);

public sealed class SplitParagraph
{
    public List<Span> Spans { get; } = new();
    /// <summary>The paragraph ends inside an open quotation (the speech continues in the next paragraph).</summary>
    public bool EndsOpen { get; set; }
    /// <summary>The paragraph begins with an opening quotation mark.</summary>
    public bool StartsWithQuote { get; set; }
    public bool HasQuote => Spans.Any(s => s.IsQuote);
}

/// <summary>Splits a paragraph into narration and quoted speech.</summary>
public static class QuoteSplitter
{
    /// <summary>Guesses whether a book marks dialogue with double or single quotation marks.</summary>
    public static QuoteStyle DetectStyle(IEnumerable<string> paragraphs)
    {
        int dbl = 0, sgl = 0;
        foreach (var p in paragraphs.Take(4000))
        {
            for (int i = 0; i < p.Length; i++)
            {
                char c = p[i];
                if (c is '“' or '"' && (i == 0 || !char.IsLetterOrDigit(p[i - 1]))) dbl++;
                else if (c is '‘' && (i == 0 || !char.IsLetterOrDigit(p[i - 1]))) sgl++;
                else if (c == '\'' && (i == 0 || char.IsWhiteSpace(p[i - 1])) && i + 1 < p.Length && char.IsUpper(p[i + 1])) sgl++;
            }
        }
        return sgl > dbl * 2 && sgl > 10 ? QuoteStyle.Single : QuoteStyle.Double;
    }

    public static SplitParagraph Split(string text, QuoteStyle style)
    {
        var result = new SplitParagraph();
        var sb = new System.Text.StringBuilder();
        bool inQuote = false;
        int i = 0;
        // Typographic convention: a speech that runs over several paragraphs repeats the opening mark
        // on each paragraph without closing the previous one; that is handled by the caller via EndsOpen.
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        result.StartsWithQuote = i < text.Length && IsOpen(text, i, style);

        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (!inQuote && IsOpen(text, i, style))
            {
                Flush(result, sb, false);
                inQuote = true;
                continue;
            }
            if (inQuote && IsClose(text, i, style))
            {
                Flush(result, sb, true);
                inQuote = false;
                continue;
            }
            sb.Append(c);
        }
        Flush(result, sb, inQuote);
        result.EndsOpen = inQuote;
        return result;
    }

    private static void Flush(SplitParagraph p, System.Text.StringBuilder sb, bool quote)
    {
        var t = sb.ToString().Trim();
        sb.Clear();
        if (t.Length == 0) return;
        if (!quote && t.All(ch => char.IsPunctuation(ch) || char.IsWhiteSpace(ch))) return; // ", " between two quotes
        p.Spans.Add(new Span(t, quote));
    }

    private static bool IsOpen(string s, int i, QuoteStyle style)
    {
        char c = s[i];
        if (style == QuoteStyle.Double)
        {
            if (c == '“') return true;
            if (c == '"') return i == 0 || !char.IsLetterOrDigit(s[i - 1]) || (i + 1 < s.Length && char.IsLetter(s[i + 1]) && char.IsWhiteSpace(s[i - 1]));
            return false;
        }
        if (c is not ('‘' or '\'')) return false;
        bool before = i == 0 || char.IsWhiteSpace(s[i - 1]) || s[i - 1] is '(' or '—' or '–' or '-' or '“';
        bool after = i + 1 < s.Length && !char.IsWhiteSpace(s[i + 1]);
        return before && after;
    }

    private static bool IsClose(string s, int i, QuoteStyle style)
    {
        char c = s[i];
        if (style == QuoteStyle.Double) return c is '”' or '"';
        if (c is not ('’' or '\'')) return false;
        // An apostrophe inside a word ("don’t") never closes a quotation.
        bool letterBefore = i > 0 && char.IsLetter(s[i - 1]);
        bool letterAfter = i + 1 < s.Length && char.IsLetter(s[i + 1]);
        if (letterBefore && letterAfter) return false;
        bool before = i > 0 && !char.IsWhiteSpace(s[i - 1]);
        bool after = i + 1 >= s.Length || !char.IsLetterOrDigit(s[i + 1]);
        return before && after;
    }
}
