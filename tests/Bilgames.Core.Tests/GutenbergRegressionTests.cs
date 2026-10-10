using System.IO.Compression;
using Bilgames.Core.Analysis;
using Bilgames.Core.Epub;
using Bilgames.Core.Model;
using Bilgames.Core.Planning;

namespace Bilgames.Core.Tests;

/// <summary>
/// Regressions found by importing Project Gutenberg EPUBs. All markup and text here is synthetic,
/// shaped like Gutenberg's Ebookmaker output.
/// </summary>
public class GutenbergImportTests
{
    private static string Lorem(string seed, int sentences = 14) =>
        string.Join(" ", Enumerable.Range(0, sentences).Select(i => $"The {seed} walked along the quiet road for a while, thinking about sentence number {i}."));

    /// <summary>Writes a minimal EPUB 3 whose spine is the given XHTML documents, in order.</summary>
    private static string MakeEpub(params (string Name, string Body)[] docs)
    {
        var path = Path.Combine(Path.GetTempPath(), "bilgames-pg-" + Guid.NewGuid().ToString("N") + ".epub");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        void Add(string name, string text)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write(text);
        }
        Add("mimetype", "application/epub+zip");
        Add("META-INF/container.xml", "<?xml version=\"1.0\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
        var manifest = string.Join("", docs.Select((d, i) => $"<item id=\"d{i}\" href=\"{d.Name}\" media-type=\"application/xhtml+xml\"/>"));
        var spine = string.Join("", docs.Select((d, i) => $"<itemref idref=\"d{i}\"/>"));
        Add("OEBPS/content.opf", "<?xml version=\"1.0\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>Test Book</dc:title><dc:creator>A. Writer</dc:creator><dc:language>en</dc:language></metadata>"
            + $"<manifest>{manifest}</manifest><spine>{spine}</spine></package>");
        foreach (var (name, body) in docs)
            Add("OEBPS/" + name, $"<?xml version='1.0' encoding='utf-8'?>\n<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>x</title></head><body>{body}</body></html>");
        return path;
    }

    private static ImportedBook Import(params (string, string)[] docs)
    {
        var path = MakeEpub(docs);
        try { return EpubImporter.Import(path); }
        finally { File.Delete(path); }
    }

    private static string Paras(string seed, int n) => string.Concat(Enumerable.Range(0, n).Select(i => $"<p>{Lorem(seed + i, 3)}</p>\n"));

    [Fact]
    public void SelfClosingAnchorsDoNotSwallowTheChapter()
    {
        var book = Import(
            ("c1.xhtml", $"<div class=\"chapter\"><h2><a id=\"chap01\"/>I.<br/>\nA FIRST TALE</h2>\n{Paras("fox", 6)}</div>"),
            ("c2.xhtml", $"<div class=\"chapter\"><h2><a id=\"chap02\"/>II.<br/>\nA SECOND TALE</h2>\n{Paras("hen", 5)}</div>"),
            ("c3.xhtml", $"<div class=\"chapter\"><h2><a id=\"chap03\"/>III.<br/>\nA THIRD TALE</h2>\n{Paras("owl", 4)}</div>"));
        var body = book.Chapters.Where(c => c.Kind == SectionKind.Body).ToList();
        Assert.Equal(new[] { "I. A First Tale", "II. A Second Tale", "III. A Third Tale" }, body.Select(c => c.Title));
        Assert.Equal(6, body[0].Blocks.Count(b => b.Kind == BlockKind.Paragraph));
        Assert.Equal(5, body[1].Blocks.Count(b => b.Kind == BlockKind.Paragraph));
    }

    [Fact]
    public void ExpandsOnlyNonVoidSelfClosingTags()
    {
        Assert.Equal("<a id=\"x\"></a><br/><img src=\"a.png\"/>", Xhtml5.ExpandSelfClosing("<a id=\"x\"/><br/><img src=\"a.png\"/>"));
        Assert.Equal("<span class='s'></span>", Xhtml5.ExpandSelfClosing("<span class='s' />"));
    }

    [Fact]
    public void InlineElementWrappingParagraphsKeepsThem()
    {
        var blocks = XhtmlExtractor.Extract("a.xhtml", "<html><body><span><p>First one.</p><p>Second one.</p></span></body></html>");
        Assert.Equal(new[] { "First one.", "Second one." }, blocks.Select(b => b.Text));
    }

    [Fact]
    public void GutenbergHeaderAndLicenceAreNeverBody()
    {
        var book = Import(
            ("c0.xhtml", "<header class=\"pg-boilerplate pgheader\" id=\"pg-header\"><h2>The Project Gutenberg eBook of Test Book</h2><div>This eBook is for the use of anyone.</div>"
                         + "<div>*** START OF THE PROJECT GUTENBERG EBOOK TEST BOOK ***</div></header><h1>Test Book</h1>"),
            ("c1.xhtml", $"<h2>CHAPTER I.</h2>{Paras("fox", 4)}"),
            ("c2.xhtml", $"<h2>CHAPTER II.</h2>{Paras("hen", 4)}"),
            ("c3.xhtml", $"<h2>CHAPTER III.</h2>{Paras("owl", 4)}<p>The end of the tale.</p>"
                         + "<footer class=\"pg-boilerplate pgheader\" id=\"pg-footer\"><div>*** END OF THE PROJECT GUTENBERG EBOOK TEST BOOK ***</div>"
                         + "<h2>THE FULL PROJECT GUTENBERG LICENSE</h2><p>Section 1. General Terms of Use and Redistributing Project Gutenberg electronic works.</p></footer>"));
        var body = book.Chapters.Where(c => c.Kind == SectionKind.Body).ToList();
        Assert.Equal(3, body.Count);
        Assert.DoesNotContain(body.SelectMany(c => c.Blocks), b => b.Text.Contains("Gutenberg", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("The end of the tale.", body[^1].Blocks[^1].Text);
        Assert.Equal(SectionKind.BackMatter, book.Chapters[^1].Kind);
        Assert.Equal(SectionKind.FrontMatter, book.Chapters[0].Kind);
    }

    [Fact]
    public void StartAndEndLinesMarkBoilerplateWithoutContainers()
    {
        var book = Import(
            ("c0.xhtml", "<p>This eBook is for the use of anyone anywhere.</p><p>*** START OF THE PROJECT GUTENBERG EBOOK TEST BOOK ***</p>"),
            ("c1.xhtml", $"<h2>Chapter 1</h2>{Paras("fox", 4)}"),
            ("c2.xhtml", $"<h2>Chapter 2</h2>{Paras("hen", 4)}"),
            ("c3.xhtml", $"<h2>Chapter 3</h2>{Paras("owl", 4)}<p>*** END OF THE PROJECT GUTENBERG EBOOK TEST BOOK ***</p><p>Updated editions will replace the previous one.</p>"));
        Assert.All(book.Chapters.Where(c => c.Kind == SectionKind.Body), c => Assert.DoesNotContain(c.Blocks, b => b.Text.Contains("GUTENBERG")));
        Assert.Equal(SectionKind.BackMatter, book.Chapters[^1].Kind);
    }

    [Fact]
    public void LongChapterTitlesAndCaptionedHeadingsStartChapters()
    {
        var book = Import(("c1.xhtml",
            $"<h2>CHAPTER 1. The Start.</h2>{Paras("fox", 3)}"
            + $"<h2>CHAPTER 2. Of Things in Paint; in Teeth; in Wood; in Sheet-Iron; in Stone; in Mountains; in Stars.</h2>{Paras("hen", 3)}"
            + $"<h2><a id=\"c3\"/><img alt=\"\" src=\"i.jpg\"/><span class=\"caption\">A pull quote under the picture.</span><br/><br/>CHAPTER 3.</h2>{Paras("owl", 3)}"));
        var titles = book.Chapters.Where(c => c.Kind == SectionKind.Body).Select(c => c.Title).ToList();
        Assert.Equal(3, titles.Count);
        Assert.StartsWith("CHAPTER 2. Of Things in Paint", titles[1]);
        Assert.Equal("Chapter 3", titles[2]);
    }

    [Fact]
    public void ContentsLinksDoNotSwallowTheFirstLetter()
    {
        var book = Import(
            ("toc.xhtml", "<h2>CONTENTS</h2><p><a href=\"l1.xhtml#l1\">Letter 1</a></p><p><a href=\"l2.xhtml#l2\">Letter 2</a></p><p><a href=\"c1.xhtml#c1\">Chapter 1</a></p>"),
            ("l1.xhtml", $"<h2><a id=\"l1\"/>Letter 1</h2>{Paras("fox", 3)}"),
            ("l2.xhtml", $"<h2><a id=\"l2\"/>Letter 2</h2>{Paras("hen", 3)}"),
            ("c1.xhtml", $"<h2><a id=\"c1\"/>Chapter 1</h2>{Paras("owl", 3)}"));
        var body = book.Chapters.Where(c => c.Kind == SectionKind.Body).Select(c => c.Title).ToList();
        Assert.Equal(new[] { "Letter 1", "Letter 2", "Chapter 1" }, body);
        Assert.True(book.Chapters[0].WordCount < 20, "the contents page holds only its links");
    }

    [Fact]
    public void DropCapImagesKeepTheirLetterAndCaptionsAreDropped()
    {
        var blocks = XhtmlExtractor.Extract("a.xhtml", "<html><body>"
            + "<p><span class=\"letra\"><img alt=\"W\" src=\"w.png\"/></span>HEN it rained, they stayed in.</p>"
            + "<p><span class=\"letra\"><img alt=\"“I\" src=\"i.png\"/></span>\nhope so,” she said.</p>"
            + "<p><img alt=\"A map of the island\" src=\"m.png\"/>The map was old.</p>"
            + "<div class=\"figcenter\"><img alt=\"\" src=\"f.jpg\"/><div class=\"caption\"><p>Two people at a table</p><p>[Copyright 1894 by Someone.]</p></div></div>"
            + "<p>[Illustration: A ship]</p>"
            + "</body></html>");
        Assert.Equal(new[] { "WHEN it rained, they stayed in.", "“I hope so,” she said.", "The map was old." }, blocks.Select(b => b.Text));
    }

    [Theory]
    [InlineData("II. THE RED-HEADED LEAGUE", "II. The Red-Headed League")]
    [InlineData("ACT II", "Act II")]
    [InlineData("THE ENGINEER’S THUMB", "The Engineer’s Thumb")]
    public void TitlesKeepRomanNumerals(string heading, string title) => Assert.Equal(title, ChapterBuilder.FormatTitle(new List<string> { heading }));

    [Theory]
    [InlineData("II. THE RED-HEADED LEAGUE", true)]
    [InlineData("XIV The First Blow", true)]
    [InlineData("CHAPTER 57. Of Whales in Paint; in Teeth; in Wood; in Sheet-Iron; in Stone; in Mountains; in Stars.", true)]
    [InlineData("I hope Mr. Bingley will like it.", false)]
    public void RecognizesChapterHeadingElements(string text, bool expected) => Assert.Equal(expected, ChapterBuilder.IsChapterHeading(text));
}

public class GutenbergAnalysisTests
{
    private static AnalysisResult Analyze(params string[] paragraphs)
    {
        var ch = new ChapterText { Index = 0, Title = "One", Kind = SectionKind.Body };
        for (int i = 0; i < paragraphs.Length; i++) ch.Blocks.Add(new Block { Id = $"c0b{i}", Kind = BlockKind.Paragraph, Text = paragraphs[i] });
        return DialogueAnalyzer.Analyze(new[] { ch });
    }

    private static Line LineWith(AnalysisResult r, string text) => r.Chapters[0].Lines.First(l => l.IsDialogue && l.Text.Contains(text));

    [Fact]
    public void RelativeDescriptorIsNotAConfidentNarrator()
    {
        var r = Analyze(
            "“Good morning, Tom,” said Mary.",
            "“Good morning,” said Tom.",
            "“Where is the cake?” cried her mother.");
        var line = LineWith(r, "Where is the cake");
        Assert.False(line.SpeakerId == CastMember.NarratorId && !line.Unsure, "“cried her mother” was read by the narrator with confidence");
    }

    [Fact]
    public void ExplicitPronounBeatsAlternation()
    {
        var r = Analyze(
            "“First,” said Tom.",
            "“Second,” said Jim.",
            "“Third,” said Tom.",
            "“What a fine day it is,” said she.");
        var line = LineWith(r, "What a fine day");
        var speaker = r.Cast.Find(line.SpeakerId)?.Name;
        Assert.NotEqual("Jim", speaker);
        Assert.NotEqual("Tom", speaker);
    }

    [Fact]
    public void BareSurnameDoesNotMergeDifferentlyTitledPeople()
    {
        var r = Analyze(
            "“Hello,” said Miss Bingley.", "“Hello,” said Miss Bingley.", "“Good day,” said Mr. Bingley.", "“Indeed,” said Bingley.",
            "“Merry Christmas,” said Bob Cratchit.", "“And to you,” said Mrs. Cratchit.", "“Thank you,” said Bob Cratchit.");
        var missB = r.Cast.Members.Single(m => m.Name == "Miss Bingley");
        Assert.DoesNotContain("Mr. Bingley", missB.Aliases);
        var bob = r.Cast.Members.Single(m => m.Name == "Bob Cratchit");
        Assert.DoesNotContain("Mrs. Cratchit", bob.Aliases);
    }

    [Fact]
    public void UniqueSurnameJoinsTheFullName()
    {
        var r = Analyze("“Come, Watson,” said Sherlock Holmes.", "“Coming,” I said.", "“Quick,” said Holmes.", "“Yes,” said Sherlock Holmes.");
        Assert.Single(r.Cast.Members, m => m.Name.Contains("Holmes"));
    }

    [Theory]
    [InlineData("said the old Gay-Head Indian once.")]
    [InlineData("said the dead.")]
    public void AdjectivesAreNotCharacters(string narration)
    {
        var tag = SpeechTags.FindAfter(narration);
        Assert.True(tag is null || tag.Kind != SubjectKind.Descriptor, $"got descriptor {tag?.Subject}");
    }

    [Theory]
    [InlineData("the duke he says.", SubjectKind.Descriptor, "The Duke")]
    [InlineData("said M. Krempe.", SubjectKind.Name, "M. Krempe")]
    [InlineData("said Lord St. Simon.", SubjectKind.Name, "Lord St. Simon")]
    public void DialectAndAbbreviatedNames(string narration, SubjectKind kind, string subject)
    {
        var tag = SpeechTags.FindAfter(narration);
        Assert.NotNull(tag);
        Assert.Equal(kind, tag!.Kind);
        Assert.Equal(subject, tag.Subject);
    }

    [Fact]
    public void SentenceAdverbIsNotAName() => Assert.Null(SpeechTags.FindAfter("Upon entering, said."));
}

public class DialectApostropheTests
{
    [Theory]
    [InlineData("the rest o’ the night", "the rest o' the night")]
    [InlineData("goin’ to see ’em, doan’ you", "goin' to see 'em, doan' you")]
    [InlineData("“’Tis true,” said he.", "“'Tis true,” said he.")]
    [InlineData("’Tain’t so, ’kase I said ’stead", "'Tain't so, 'kase I said 'stead")]
    [InlineData("a man-o’-war", "a man-o'-war")]
    [InlineData("she said ‘no’ to him", "she said ‘no’ to him")]
    [InlineData("‘Hello,’ he said, goin’ home.", "‘Hello,’ he said, goin' home.")]
    public void ElisionsBecomePlainApostrophes(string input, string expected) => Assert.Equal(expected, SynthesisPlanner.Apostrophes(input));
}
