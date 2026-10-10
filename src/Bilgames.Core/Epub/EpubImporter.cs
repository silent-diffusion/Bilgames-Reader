using AngleSharp.Html.Dom;
using Bilgames.Core.Model;

namespace Bilgames.Core.Epub;

public sealed class ImportedBook
{
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public string Language { get; init; } = "en";
    public byte[]? Cover { get; init; }
    public string CoverExtension { get; init; } = ".jpg";
    public List<ChapterText> Chapters { get; init; } = new();
}

/// <summary>EPUB file → chapters of clean, ordered text. Pure and offline; never writes to disk.</summary>
public static class EpubImporter
{
    public static ImportedBook Import(string path, CancellationToken ct = default)
    {
        using var zip = SafeArchive.Open(path);
        var pkg = EpubPackage.Load(zip);

        var blocks = new List<RawBlock>();
        var docs = new Dictionary<string, IHtmlDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in pkg.Spine)
        {
            ct.ThrowIfCancellationRequested();
            var html = zip.ReadText(item.Path);
            blocks.AddRange(XhtmlExtractor.Extract(item.Path, html));
        }

        string? ResolveNote(string target)
        {
            int hash = target.IndexOf('#');
            if (hash < 0 || hash == target.Length - 1) return null;
            var docPath = target[..hash];
            var id = target[(hash + 1)..];
            if (!docs.TryGetValue(docPath, out var doc))
            {
                if (!zip.Contains(docPath)) return null;
                doc = Xhtml5.Parse(zip.ReadText(docPath));
                docs[docPath] = doc;
            }
            var text = XhtmlExtractor.TextOfId(doc, id);
            // Page anchors from PDF conversions point at arbitrary text; real notes start with their marker or are short.
            if (text is null) return null;
            bool marked = char.IsDigit(text[0]) || text[0] is '*' or '†' or '‡' or '[';
            return marked || text.Length < 600 ? text : null;
        }

        var (title, author) = ResolveTitleAndAuthor(pkg, Path.GetFileNameWithoutExtension(path));
        var chapters = ChapterBuilder.Build(pkg, blocks, ResolveNote, title);

        byte[]? cover = null;
        if (pkg.CoverImagePath is { } cp)
        {
            try { cover = zip.ReadBytes(cp); } catch (EpubFormatException) { }
        }

        var lang = pkg.Language.ToLowerInvariant();
        if (lang is "und" or "" or "unknown") lang = "en";
        return new ImportedBook
        {
            Title = title,
            Author = author,
            Language = lang,
            Cover = cover,
            CoverExtension = Path.GetExtension(pkg.CoverImagePath ?? ".jpg").ToLowerInvariant() is { Length: > 0 } ext ? ext : ".jpg",
            Chapters = chapters,
        };
    }

    /// <summary>
    /// Converted EPUBs often carry junk metadata (a GUID title, "Unknown" author, "Last, First").
    /// Fall back to a "Title (Author)" file name, which is how most libraries name their files.
    /// </summary>
    internal static (string Title, string Author) ResolveTitleAndAuthor(EpubPackage pkg, string fileName)
    {
        string? fileTitle = null, fileAuthor = null;
        var m = System.Text.RegularExpressions.Regex.Match(fileName, @"^(?<t>.+?)\s*\((?<a>[^()]+)\)\s*$");
        if (m.Success) { fileTitle = m.Groups["t"].Value.Trim(); fileAuthor = m.Groups["a"].Value.Trim(); }

        var title = pkg.Title;
        if (title.Length == 0 || Guid.TryParse(title, out _) || title.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            title = fileTitle ?? fileName;

        var author = pkg.Author;
        if (author.Length == 0 || author.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            author = fileAuthor ?? "";
        else if (author.Count(c => c == ',') == 1)
        {
            // "Hemingway, Ernest" → "Ernest Hemingway"
            var parts = author.Split(',', 2, StringSplitOptions.TrimEntries);
            if (parts[1].Length > 0 && !parts[1].Contains(' ')) author = parts[1] + " " + parts[0];
        }
        return (title, author);
    }
}
