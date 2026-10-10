using System.Globalization;
using System.Text;
using Bilgames.Core.Model;

namespace Bilgames.Core.Export;

public enum AnnotationFormat
{
    Markdown,
    PlainText,
    Csv,
}

/// <summary>Writes a book's bookmarks and highlights as Markdown, plain text or CSV, in reading order.</summary>
public static class AnnotationExport
{
    private sealed record Entry(int Chapter, string ChapterTitle, double Seconds, int Order, string Kind, string Text, string Note, string Color, DateTime Created);

    public static AnnotationFormat FormatFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".csv" => AnnotationFormat.Csv,
        ".txt" => AnnotationFormat.PlainText,
        _ => AnnotationFormat.Markdown,
    };

    public static string Write(BookRecord book, AnnotationSet set, AnnotationFormat format, DateTime? now = null) => format switch
    {
        AnnotationFormat.Csv => Csv(book, set),
        AnnotationFormat.PlainText => PlainText(book, set, now ?? DateTime.Now),
        _ => Markdown(book, set, now ?? DateTime.Now),
    };

    /// <summary>Bookmarks and highlights merged, sorted by chapter order, then time, then position in the text.</summary>
    private static List<Entry> Entries(BookRecord book, AnnotationSet set)
    {
        string Title(int i) => i >= 0 && i < book.Chapters.Count ? book.Chapters[i].Title : $"Chapter {i + 1}";
        var list = new List<Entry>();
        foreach (var b in set.Bookmarks)
            list.Add(new Entry(b.ChapterIndex, Title(b.ChapterIndex), b.Seconds, 0, "Bookmark", b.Excerpt, b.Note, "", b.CreatedUtc));
        foreach (var h in set.Highlights)
            list.Add(new Entry(h.ChapterIndex, Title(h.ChapterIndex), h.Seconds, 1, "Highlight", h.Text, h.Note, h.Color.ToString().ToLowerInvariant(), h.CreatedUtc));
        // Highlights without a time (not generated yet) sort by when they were made within their chapter.
        return list.OrderBy(e => e.Chapter).ThenBy(e => e.Seconds < 0 ? double.MaxValue : e.Seconds).ThenBy(e => e.Created).ThenBy(e => e.Order).ToList();
    }

    public static string Clock(double seconds)
    {
        if (seconds < 0) return "";
        var t = TimeSpan.FromSeconds(Math.Floor(seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    private static string Markdown(BookRecord book, AnnotationSet set, DateTime now)
    {
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(book.Title);
        if (book.Author.Length > 0) sb.Append("*").Append(book.Author).AppendLine("*");
        sb.AppendLine();
        sb.AppendLine($"{set.Bookmarks.Count} {(set.Bookmarks.Count == 1 ? "bookmark" : "bookmarks")} · {set.Highlights.Count} {(set.Highlights.Count == 1 ? "highlight" : "highlights")} · exported {now.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}");
        int chapter = -1;
        foreach (var e in Entries(book, set))
        {
            if (e.Chapter != chapter)
            {
                chapter = e.Chapter;
                sb.AppendLine().Append("## ").AppendLine(e.ChapterTitle).AppendLine();
            }
            var at = Clock(e.Seconds);
            if (e.Kind == "Bookmark")
            {
                sb.Append("- **Bookmark**").Append(at.Length > 0 ? $" at {at}" : "");
                if (e.Text.Length > 0) sb.Append(" — “").Append(OneLine(e.Text)).Append('”');
                sb.AppendLine();
                if (e.Note.Length > 0) sb.Append("  ").AppendLine(OneLine(e.Note));
            }
            else
            {
                foreach (var line in e.Text.Split('\n')) sb.Append("> ").AppendLine(line.TrimEnd());
                sb.AppendLine(">").Append("> — ").Append(at.Length > 0 ? at + " · " : "").Append(e.Color).AppendLine(" highlight");
                if (e.Note.Length > 0) sb.AppendLine().Append("**Note:** ").AppendLine(OneLine(e.Note));
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string PlainText(BookRecord book, AnnotationSet set, DateTime now)
    {
        var sb = new StringBuilder();
        sb.AppendLine(book.Title);
        if (book.Author.Length > 0) sb.AppendLine(book.Author);
        sb.AppendLine($"Bookmarks and highlights, exported {now.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}");
        int chapter = -1;
        foreach (var e in Entries(book, set))
        {
            if (e.Chapter != chapter)
            {
                chapter = e.Chapter;
                sb.AppendLine().AppendLine(e.ChapterTitle.ToUpperInvariant()).AppendLine();
            }
            var at = Clock(e.Seconds);
            sb.Append(e.Kind == "Bookmark" ? "[Bookmark" : $"[Highlight, {e.Color}").Append(at.Length > 0 ? $" · {at}" : "").AppendLine("]");
            if (e.Text.Length > 0) sb.Append('“').Append(e.Text).AppendLine("”");
            if (e.Note.Length > 0) sb.Append("Note: ").AppendLine(e.Note);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string Csv(BookRecord book, AnnotationSet set)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Type,Chapter,Time,Seconds,Text,Note,Color,Created");
        foreach (var e in Entries(book, set))
        {
            sb.AppendJoin(',', new[]
            {
                e.Kind, Cell(e.ChapterTitle), Clock(e.Seconds), e.Seconds < 0 ? "" : e.Seconds.ToString("0.##", CultureInfo.InvariantCulture),
                Cell(e.Text), Cell(e.Note), e.Color, e.Created.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            }).AppendLine();
        }
        return sb.ToString();
    }

    private static string OneLine(string s) => string.Join(' ', s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();

    /// <summary>RFC 4180 quoting; a leading = + - @ is prefixed so spreadsheets don't run it as a formula.</summary>
    private static string Cell(string s)
    {
        if (s.Length > 0 && "=+-@".Contains(s[0])) s = "'" + s;
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
