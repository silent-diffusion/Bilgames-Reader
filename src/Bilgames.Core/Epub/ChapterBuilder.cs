using System.Globalization;
using System.Text.RegularExpressions;
using Bilgames.Core.Model;

namespace Bilgames.Core.Epub;

/// <summary>
/// Groups the block stream of the whole spine into chapters. Real-world EPUBs (especially PDF
/// conversions) often have useless TOCs, chapters split across files, and paragraphs broken at
/// page boundaries, so headings in the text are the primary chapter signal and the TOC is a hint.
/// </summary>
internal static partial class ChapterBuilder
{
    public static List<ChapterText> Build(EpubPackage pkg, List<RawBlock> blocks, Func<string, string?> resolveNote, string bookTitle)
    {
        blocks = RemoveRunningHeaders(blocks, bookTitle);
        MarkBoilerplate(blocks);
        blocks = RepairParagraphs(blocks);
        PromoteHeadingParagraphs(blocks);
        var boundaries = FindBoundaries(pkg, blocks);

        var chapters = new List<ChapterText>();
        var semantics = new List<string>();
        var boiler = new List<int>();
        for (int b = 0; b < boundaries.Count; b++)
        {
            var (start, tocTitle) = boundaries[b];
            int end = b + 1 < boundaries.Count ? boundaries[b + 1].Start : blocks.Count;
            if (end <= start) continue;
            var slice = blocks.GetRange(start, end - start);
            if (!slice.Any(x => x.Kind != RawKind.SceneBreak)) continue;

            var headingTexts = slice.TakeWhile(x => x.Kind == RawKind.Heading).Select(x => x.Text).ToList();
            string title = headingTexts.Count > 0 ? FormatTitle(headingTexts) : tocTitle ?? "";
            int bp = slice[0].Boilerplate;
            if (title.Length == 0 && bp == 2) title = "Project Gutenberg License";
            if (title.Length == 0) title = chapters.Count == 0 ? "Opening pages" : $"Section {chapters.Count + 1}";

            var chapter = new ChapterText { Index = chapters.Count, Title = title };
            int n = 0;
            var noteIds = new Dictionary<string, string>();
            foreach (var raw in slice)
            {
                var block = new Block
                {
                    Id = $"c{chapter.Index}b{n++}",
                    Kind = raw.Kind switch
                    {
                        RawKind.Heading => BlockKind.Heading,
                        RawKind.SceneBreak => BlockKind.SceneBreak,
                        RawKind.Verse => BlockKind.Verse,
                        _ => BlockKind.Paragraph,
                    },
                    Text = raw.Text,
                };
                foreach (var target in raw.NoteRefs.Distinct())
                {
                    if (!noteIds.TryGetValue(target, out var nid))
                    {
                        var text = resolveNote(target);
                        if (text is null || text.Length < 3 || XhtmlExtractor.IsMarkerText(text)) continue;
                        nid = $"c{chapter.Index}n{chapter.Notes.Count}";
                        noteIds[target] = nid;
                        chapter.Notes.Add(new Footnote { Id = nid, Text = StripLeadingMarker(text) });
                    }
                    block.NoteRefs.Add(nid);
                }
                chapter.Blocks.Add(block);
            }
            chapters.Add(chapter);
            semantics.Add(slice.Select(x => x.Semantics).FirstOrDefault(s => s.Length > 0) is { } s0 ? s0 : LandmarkFor(pkg, slice[0].DocPath));
            boiler.Add(bp);
        }

        if (chapters.Count == 0) throw new EpubFormatException("No readable text was found in this EPUB.");
        Classify(chapters, semantics, boiler);
        return chapters;
    }

    // ---------- Project Gutenberg boilerplate ----------

    [GeneratedRegex(@"^\W*\**\s*START OF (THE|THIS) PROJECT GUTENBERG", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PgStart();

    [GeneratedRegex(@"^\W*\**\s*END OF (THE |THIS )?PROJECT GUTENBERG", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PgEnd();

    /// <summary>
    /// Marks the Project Gutenberg header (everything up to "*** START OF THE PROJECT GUTENBERG EBOOK")
    /// and the licence footer ("*** END OF …" onwards). Uses the pg-header/pg-footer containers when the
    /// EPUB has them and the START/END lines otherwise. Boilerplate is never read as part of the book.
    /// </summary>
    internal static void MarkBoilerplate(List<RawBlock> blocks)
    {
        foreach (var b in blocks)
            if (b.Semantics.Contains(XhtmlExtractor.PgBoilerplate))
                b.Boilerplate = b.Semantics.Contains("backmatter") ? 2 : 1;

        // START line: only near the beginning; everything before it is header.
        int limit = Math.Max(1, blocks.Count / 3);
        int start = blocks.FindIndex(0, Math.Min(limit, blocks.Count), b => b.Kind != RawKind.SceneBreak && PgStart().IsMatch(b.Text));
        if (start >= 0)
            for (int i = 0; i <= start; i++) if (blocks[i].Boilerplate == 0) blocks[i].Boilerplate = 1;

        // END line: only in the last part of the book; it and everything after it is footer.
        int end = -1;
        for (int i = blocks.Count - 1; i >= Math.Max(start + 1, blocks.Count / 2); i--)
            if (blocks[i].Kind != RawKind.SceneBreak && PgEnd().IsMatch(blocks[i].Text)) end = i;
        if (end < 0)
        {
            // A footer container without an END line: everything after its first block is footer too.
            int first = blocks.FindIndex(b => b.Boilerplate == 2);
            if (first >= 0) end = first;
        }
        if (end >= 0)
            for (int i = end; i < blocks.Count; i++) blocks[i].Boilerplate = 2;
    }

    // ---------- cleanup ----------

    /// <summary>
    /// Drops headings that repeat on many pages (the book title or author printed as a running
    /// header) so they aren't read aloud before every chapter or glued into chapter titles.
    /// </summary>
    internal static List<RawBlock> RemoveRunningHeaders(List<RawBlock> blocks, string bookTitle)
    {
        var counts = blocks.Where(b => b.Kind == RawKind.Heading)
            .GroupBy(b => b.Text.Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.Count());
        var title = bookTitle.Trim().ToLowerInvariant();
        bool IsRunning(RawBlock b)
        {
            if (b.Kind != RawKind.Heading) return false;
            var key = b.Text.Trim().ToLowerInvariant();
            if (IsHeadingLike(b.Text)) return false;
            return counts[key] >= 4 || (key == title && counts[key] >= 2);
        }
        // Keep the first occurrence (it is usually the real title page).
        var seen = new HashSet<string>();
        var result = new List<RawBlock>(blocks.Count);
        foreach (var b in blocks)
        {
            if (IsRunning(b) && !seen.Add(b.Text.Trim().ToLowerInvariant()))
            {
                // Preserve anchors so TOC fragments still resolve.
                if (result.Count > 0) result[^1].Anchors.AddRange(b.Anchors);
                continue;
            }
            result.Add(b);
        }
        return result;
    }

    // ---------- paragraph repair ----------

    /// <summary>Joins paragraphs that were split mid-sentence by page breaks in PDF conversions.</summary>
    internal static List<RawBlock> RepairParagraphs(List<RawBlock> blocks)
    {
        var result = new List<RawBlock>(blocks.Count);
        foreach (var b in blocks)
        {
            var prev = result.Count > 0 ? result[^1] : null;
            if (prev is not null && prev.Kind == RawKind.Paragraph && b.Kind == RawKind.Paragraph && prev.Boilerplate == b.Boilerplate && LooksContinued(prev.Text, b.Text))
            {
                prev.Text = prev.Text.EndsWith('-') && char.IsLower(b.Text[0]) && !prev.Text.EndsWith("--", StringComparison.Ordinal)
                    ? prev.Text + b.Text
                    : prev.Text + " " + b.Text;
                prev.NoteRefs.AddRange(b.NoteRefs);
                prev.Anchors.AddRange(b.Anchors);
                continue;
            }
            result.Add(b);
        }
        return result;
    }

    internal static bool LooksContinued(string prev, string next)
    {
        if (prev.Length == 0 || next.Length == 0) return false;
        char last = prev[^1];
        char first = next[0];
        if (IsTerminal(last)) return false;
        bool prevOpen = char.IsLetterOrDigit(last) || last is ',' or '-' or '–' or '(' or '“' or '‘';
        // Lowercase continuation is a strong signal; a comma or open word followed by a lowercase word, likewise.
        if (char.IsLower(first)) return prevOpen || last is ';' or ':' or '—';
        // "…the house of\nMr. Bennet" — the previous paragraph ending in a short function word.
        return prevOpen && EndsWithFunctionWord(prev);
    }

    private static bool IsTerminal(char c) => c is '.' or '!' or '?' or '…' or '”' or '’' or '"' or '\'' or ')' or ']' or '*';

    private static readonly HashSet<string> FunctionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "of", "to", "and", "or", "but", "in", "on", "at", "by", "for", "with", "from", "that", "as", "his", "her", "their", "my", "your", "was", "were", "is",
    };

    private static bool EndsWithFunctionWord(string s)
    {
        int i = s.LastIndexOf(' ');
        var w = i >= 0 ? s[(i + 1)..] : s;
        return FunctionWords.Contains(w);
    }

    // ---------- headings ----------

    [GeneratedRegex(@"^(chapter|book|part|volume|canto|letter|stave)\s+([0-9]+|[ivxlcdm]+|the\s+\w+|[a-z]+(-[a-z]+)?)\b[\s\.:\-–—]*.{0,70}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChapterLike();

    /// <summary>The same, with room for long descriptive titles; only used for real heading elements.</summary>
    [GeneratedRegex(@"^(chapter|book|part|volume|canto|letter|stave|act)\s+([0-9]+|[ivxlcdm]+|the\s+\w+)\b[\s\.:\-–—]*.{0,180}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LongChapterLike();

    /// <summary>"II. The Red-Headed League", "XIV The First Blow", "3. Departure": a numeral followed by a title.</summary>
    [GeneratedRegex(@"^(?:[IVXLC]{1,7}|[0-9]{1,3})(?:\.\s*|\s+)[\p{Lu}“""‘'(].{0,150}$")]
    private static partial Regex NumberedTitle();

    [GeneratedRegex(@"^([IVXLCDM]{1,7}|[0-9]{1,3})\.?$")]
    private static partial Regex NumeralOnly();

    [GeneratedRegex(@"^(prologue|epilogue|preface|foreword|introduction|afterword|appendix|contents|table of contents|dedication|acknowledg(e)?ments|notes|endnotes|glossary|bibliography|about the author|copyright|epigraph|interlude)\b.{0,40}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SectionWord();

    internal static bool IsHeadingLike(string text) =>
        text.Length <= 80 && (ChapterLike().IsMatch(text) || NumeralOnly().IsMatch(text) || (text.Length <= 40 && SectionWord().IsMatch(text)));

    /// <summary>
    /// Whether a real heading element (not a paragraph) announces a chapter. Looser than
    /// <see cref="IsHeadingLike"/>: long titles ("CHAPTER 57. Of Whales in Paint; in Teeth; …") and
    /// numbered titles ("II. THE RED-HEADED LEAGUE") count.
    /// </summary>
    internal static bool IsChapterHeading(string text)
    {
        text = GluedLabel().Replace(text, "$1 "); // "CHAPTERXXVII." (a typo in the source)
        return IsHeadingLike(text) || (text.Length <= 200 && (LongChapterLike().IsMatch(text) || NumberedTitle().IsMatch(text)));
    }

    [GeneratedRegex(@"^(CHAPTER|Chapter|BOOK|PART|STAVE|ACT)(?=[IVXLC0-9]+\b)")]
    private static partial Regex GluedLabel();

    private static void PromoteHeadingParagraphs(List<RawBlock> blocks)
    {
        foreach (var b in blocks)
        {
            if (b.Kind != RawKind.Paragraph || b.NoPromote || b.Boilerplate != 0) continue;
            if (!IsHeadingLike(b.Text)) continue;
            if (b.Text.EndsWith('.') && !NumeralOnly().IsMatch(b.Text) && b.Text.Split(' ').Length > 4) continue;
            b.Kind = RawKind.Heading;
            b.Level = 2;
            b.Promoted = true;
        }
    }

    private static List<(int Start, string? TocTitle)> FindBoundaries(EpubPackage pkg, List<RawBlock> blocks)
    {
        // Which heading levels mark chapters? h1–h3 normally; fall back to the shallowest level present.
        int minLevel = blocks.Where(b => b.Kind == RawKind.Heading).Select(b => b.Level).DefaultIfEmpty(0).Min();
        int boundaryMax = Math.Max(3, minLevel);

        // Books whose chapters are announced by "Chapter 3"/"IV"-style headings: other headings in the
        // middle of a file (slogans, letters, signs) are part of the text, not chapter starts.
        bool chapterStyled = blocks.Count(b => b.Kind == RawKind.Heading && IsChapterHeading(b.Text)) >= 3;
        bool IsChapterish(RawBlock b) => b.Promoted ? IsHeadingLike(b.Text) : IsChapterHeading(b.Text);

        var starts = new SortedDictionary<int, string?>();
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            // Boilerplate (Project Gutenberg header/licence) always starts and ends its own section.
            if (i > 0 && b.Boilerplate != blocks[i - 1].Boilerplate) { starts[i] = null; continue; }
            if (b.Kind != RawKind.Heading || b.Level > boundaryMax) continue;
            // A heading directly after another heading belongs to the same title group — unless the previous
            // heading ends a section that had text of its own and this one opens a new file.
            if (i > 0 && blocks[i - 1].Kind == RawKind.Heading && starts.Count > 0
                && (blocks[i - 1].DocPath == b.DocPath || CountTextBlocks(blocks, starts.Keys.Last(), i) == 0)) continue;
            bool midFile = i > 0 && blocks[i - 1].DocPath == b.DocPath;
            // A page-split file starting with a slogan or sign that interrupts an unfinished sentence.
            if (!midFile && chapterStyled && !IsChapterish(b) && i > 0 && blocks[i - 1].Kind == RawKind.Paragraph
                && blocks[i - 1].Text is { Length: > 0 } prevText && !IsTerminal(prevText[^1]))
                continue;
            if (midFile && !IsChapterish(b))
            {
                // Fragments ("in"), and non-chapter headings when the book uses chapter-style headings, stay inside the chapter.
                if (char.IsLower(b.Text[0]) || chapterStyled) continue;
                if (b.Level == boundaryMax && boundaryMax > minLevel) continue;
            }
            starts[i] = null;
        }

        // TOC entries mark chapters that have no heading in the text (title only in the navigation).
        // When the text already has chapter headings, the TOC is redundant at best and wrong at worst.
        var toc = starts.Count >= 3 ? new List<TocEntry>() : pkg.Toc;
        foreach (var entry in toc)
        {
            int idx = entry.Fragment is { Length: > 0 } frag
                ? blocks.FindIndex(b => b.DocPath == entry.Path && b.Anchors.Contains(frag))
                : blocks.FindIndex(b => b.DocPath == entry.Path);
            if (idx < 0) continue;
            if (!TitleLike(entry.Label)) continue;
            bool nearExisting = starts.Keys.Any(s => s <= idx && CountTextBlocks(blocks, s, idx) == 0)
                                || starts.Keys.Any(s => s > idx && CountTextBlocks(blocks, idx, s) == 0);
            if (nearExisting) continue;
            // Only trust a TOC entry that starts a file (or targets an anchor); mid-file entries without headings are noise.
            bool startsDoc = idx == 0 || blocks[idx - 1].DocPath != blocks[idx].DocPath || entry.Fragment is { Length: > 0 };
            if (startsDoc) starts[idx] = entry.Label;
        }

        // Structural semantics (front matter → body) always split.
        for (int i = 1; i < blocks.Count; i++)
        {
            if (blocks[i].DocPath != blocks[i - 1].DocPath && SemanticClass(blocks[i].Semantics, LandmarkFor(pkg, blocks[i].DocPath)) is { } cls
                && cls != SemanticClass(blocks[i - 1].Semantics, LandmarkFor(pkg, blocks[i - 1].DocPath)))
                starts.TryAdd(i, null);
        }

        var list = starts.Select(kv => (kv.Key, kv.Value)).ToList();
        if (list.Count == 0 || list[0].Key != 0) list.Insert(0, (0, null));
        return list;
    }

    private static int CountTextBlocks(List<RawBlock> blocks, int from, int to)
    {
        int n = 0;
        for (int i = from; i < to && i < blocks.Count; i++)
            if (blocks[i].Kind is RawKind.Paragraph or RawKind.Verse) n++;
        return n;
    }

    private static bool TitleLike(string label) =>
        label.Length is > 0 and <= 60 && !char.IsLower(label[0]) && !label.EndsWith(',');

    internal static string FormatTitle(List<string> headings)
    {
        var parts = new List<string>();
        foreach (var raw in headings.Take(3))
        {
            var h = GluedLabel().Replace(raw.Trim().TrimEnd('.', ':'), "$1 ");
            if (h.Length == 0) continue;
            if (NumeralOnly().IsMatch(h)) h = "Chapter " + h.TrimEnd('.');
            else if (h.Length > 3 && h.Where(char.IsLetter).All(char.IsUpper))
            {
                h = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(h.ToLowerInvariant());
                // "II. THE RED-HEADED LEAGUE" → "II. The Red-Headed League", not "Ii. …"
                h = LeadingRoman().Replace(h, m => m.Value.ToUpperInvariant());
                h = h.Replace("’S ", "’s ").Replace("'S ", "'s ");
                if (h.EndsWith("’S", StringComparison.Ordinal) || h.EndsWith("'S", StringComparison.Ordinal)) h = h[..^1] + "s";
            }
            h = FixRomanCase(h);
            if (h.Length > 90) h = h[..87] + "…";
            parts.Add(h);
        }
        return string.Join(" · ", parts);
    }

    /// <summary>ToTitleCase turns "PART II" into "Part Ii"; restore numerals.</summary>
    private static string FixRomanCase(string s) =>
        RomanAfterLabel().Replace(s, m => m.Groups[1].Value + m.Groups[2].Value.ToUpperInvariant());

    [GeneratedRegex(@"\b((?:Part|Book|Chapter|Volume|Canto|Stave|Act|Scene)\s+)([ivxlcdm]{1,7})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RomanAfterLabel();

    [GeneratedRegex(@"^[IVXLC][ivxlc]{0,6}(?=\.)")]
    private static partial Regex LeadingRoman();

    private static string StripLeadingMarker(string text)
    {
        var m = LeadingMarker().Match(text);
        return m.Success ? text[m.Length..] : text;
    }

    [GeneratedRegex(@"^[\[\(]?(\d{1,4}|[*†‡§])[\]\)\.]?\s+")]
    private static partial Regex LeadingMarker();

    // ---------- classification ----------

    private static string LandmarkFor(EpubPackage pkg, string path) => pkg.Landmarks.TryGetValue(path, out var t) ? t : "";

    private static SectionKind? SemanticClass(string semantics, string landmark)
    {
        var s = (semantics + " " + landmark).ToLowerInvariant();
        // "text" is the EPUB 2 guide type for the start of the main text.
        if (s.Contains("bodymatter") || s.Contains("chapter") || Regex.IsMatch(s, @"(^|\s)text(\s|$)")) return SectionKind.Body;
        if (s.Contains("preface") || s.Contains("foreword") || s.Contains("introduction")) return SectionKind.Preface;
        if (s.Contains("backmatter") || s.Contains("appendix") || s.Contains("afterword") || s.Contains("notes") || s.Contains("glossary") || s.Contains("index") || s.Contains("colophon")) return SectionKind.BackMatter;
        if (s.Contains("frontmatter") || s.Contains("cover") || s.Contains("title") || s.Contains("copyright") || s.Contains("toc") || s.Contains("dedication") || s.Contains("epigraph")) return SectionKind.FrontMatter;
        return null;
    }

    [GeneratedRegex(@"\b(cover|title page|half title|copyright|contents|dedication|epigraph|also by|praise for|frontispiece|map|opening pages|by the same author)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FrontWords();

    [GeneratedRegex(@"\b(preface|foreword|introduction|note on the text|a note on|chronology|editor'?s note|translator'?s|author'?s note|acknowledg(e)?ments)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrefaceWords();

    [GeneratedRegex(@"\b(afterword|appendix|appendices|notes|endnotes|glossary|bibliography|index|about the author|transcriber|further reading|reading group|discussion questions|enriched|excerpt|preview|colophon|credits|explanatory|suggestions for)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BackWords();

    /// <summary>A chapter-style title, or a title group with one ("Test Book · Chapter I").</summary>
    private static bool IsBodyTitle(string title) => title.Split(" · ").Any(IsBodyTitlePart);

    private static bool IsBodyTitlePart(string title) =>
        ChapterLike().IsMatch(title) || title.StartsWith("Chapter ", StringComparison.OrdinalIgnoreCase)
        || Regex.IsMatch(title, @"^(Part|Book|Act|Scene)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        || Regex.IsMatch(title, @"^(the\s+)?(prologue|epilogue)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        || NumberedTitle().IsMatch(title);

    /// <summary>
    /// The main text is the run of chapter-titled sections with the most words. Annotated editions
    /// also have "Chapter 1" headings in their endnotes; those form a smaller run and become back matter.
    /// </summary>
    private static (int First, int Last) MainRun(List<ChapterText> chapters, List<int> boiler)
    {
        int bestFirst = -1, bestLast = -1, bestWords = 0;
        int i = 0;
        bool Titled(int k) => boiler[k] == 0 && IsBodyTitle(chapters[k].Title);
        while (i < chapters.Count)
        {
            if (!Titled(i)) { i++; continue; }
            int first = i, last = i, words = 0, gap = 0;
            for (int j = i; j < chapters.Count; j++)
            {
                if (boiler[j] != 0) break;
                if (Titled(j)) { last = j; gap = 0; }
                else if (++gap > 2 || BackWords().IsMatch(chapters[j].Title)) break;
            }
            for (int j = first; j <= last; j++) words += chapters[j].WordCount;
            if (words > bestWords) (bestFirst, bestLast, bestWords) = (first, last, words);
            i = last + 1;
        }
        if (bestFirst < 0) return (-1, -1);
        // Trim tiny edge entries (e.g. a contents-page line that happens to say "Chapter the Last").
        while (bestFirst < bestLast && chapters[bestFirst].WordCount < 100) bestFirst++;
        while (bestLast > bestFirst && chapters[bestLast].WordCount < 40) bestLast--;
        return (bestFirst, bestLast);
    }

    private static void Classify(List<ChapterText> chapters, List<string> semantics, List<int> boiler)
    {
        var (firstBody, lastBody) = MainRun(chapters, boiler);
        if (firstBody < 0)
        {
            // No recognizable chapter titles: everything that isn't obviously front/back matter is body.
            firstBody = Enumerable.Range(0, chapters.Count).FirstOrDefault(i => boiler[i] == 0 && !FrontWords().IsMatch(chapters[i].Title)
                && !PrefaceWords().IsMatch(chapters[i].Title) && chapters[i].WordCount > 300, -1);
            if (firstBody < 0) firstBody = 0;
            lastBody = chapters.Count - 1;
            while (lastBody > firstBody && boiler[lastBody] != 0) lastBody--;
        }

        for (int i = 0; i < chapters.Count; i++)
        {
            var c = chapters[i];
            // Project Gutenberg header and licence: never part of the book.
            if (boiler[i] != 0) { c.Kind = boiler[i] == 1 ? SectionKind.FrontMatter : SectionKind.BackMatter; continue; }
            var sem = SemanticClass(semantics[i], "");
            if (IsBodyTitle(c.Title) && i >= firstBody && i <= lastBody) { c.Kind = SectionKind.Body; continue; }

            if (i < firstBody)
            {
                c.Kind = PrefaceWords().IsMatch(c.Title) || sem == SectionKind.Preface ? SectionKind.Preface
                    : FrontWords().IsMatch(c.Title) || c.WordCount < 400 || sem == SectionKind.FrontMatter ? SectionKind.FrontMatter
                    : SectionKind.Preface;
            }
            else if (i > lastBody)
            {
                c.Kind = SectionKind.BackMatter;
            }
            else
            {
                c.Kind = BackWords().IsMatch(c.Title) && c.WordCount < 2000 ? SectionKind.BackMatter : SectionKind.Body;
            }
        }
    }
}
