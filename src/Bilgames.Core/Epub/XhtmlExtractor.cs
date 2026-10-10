using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace Bilgames.Core.Epub;

internal static class Xhtml5
{
    // AngleSharp's HTML parser is tolerant of the malformed XHTML found in real EPUBs and never
    // fetches resources or runs scripts unless explicitly configured to.
    private static readonly HtmlParser Parser = new(new HtmlParserOptions { IsScripting = false });

    public static IHtmlDocument Parse(string html) => Parser.ParseDocument(html);
}

internal enum RawKind { Heading, Paragraph, SceneBreak, Verse }

internal sealed class RawBlock
{
    public RawKind Kind;
    public int Level; // heading level 1–6; 0 otherwise
    public string Text = "";
    public string DocPath = "";
    /// <summary>Element ids at or inside this block (TOC fragment targets).</summary>
    public List<string> Anchors = new();
    /// <summary>Footnote references as "path#id".</summary>
    public List<string> NoteRefs = new();
    /// <summary>EPUB 3 structural semantics inherited from ancestors (frontmatter/bodymatter/backmatter...).</summary>
    public string Semantics = "";
    /// <summary>True when this paragraph was promoted to a heading because it looks like one.</summary>
    public bool Promoted;
}

/// <summary>Turns one XHTML spine document into an ordered list of text blocks.</summary>
internal sealed partial class XhtmlExtractor
{
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "head", "title", "svg", "math", "img", "image", "object", "video", "audio", "iframe", "noscript", "template", "figure-img",
    };

    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "section", "article", "blockquote", "li", "ul", "ol", "dl", "dt", "dd", "pre", "table", "tr", "td", "th",
        "h1", "h2", "h3", "h4", "h5", "h6", "header", "footer", "aside", "figure", "figcaption", "hr", "nav", "body", "main", "center", "tbody", "thead",
    };

    private readonly string _docPath;
    private readonly List<RawBlock> _blocks = new();
    private readonly List<string> _pendingAnchors = new();

    private XhtmlExtractor(string docPath) => _docPath = docPath;

    public static List<RawBlock> Extract(string docPath, string html)
    {
        var doc = Xhtml5.Parse(html);
        var x = new XhtmlExtractor(docPath);
        if (doc.Body is { } body) x.Walk(body, Semantic(body));
        return x._blocks;
    }

    /// <summary>Text of the element with the given id, used to resolve footnote bodies.</summary>
    public static string? TextOfId(IHtmlDocument doc, string id)
    {
        var el = doc.GetElementById(id);
        if (el is null) return null;
        // An anchor inside a paragraph identifies the paragraph; climb to the nearest block.
        while (el is not null && !BlockTags.Contains(el.LocalName) && el.ParentElement is not null) el = el.ParentElement;
        if (el is null) return null;
        var text = Collapse(el.TextContent);
        return text.Length is > 0 and < 4000 ? text : null;
    }

    private static string Semantic(IElement el) => el.GetAttribute("epub:type") ?? el.GetAttribute("role") ?? "";

    private void Walk(IElement el, string semantics)
    {
        foreach (var child in el.Children) WalkOne(child, semantics);
    }

    private void WalkOne(IElement child, string semantics)
    {
        {
            var name = child.LocalName;
            if (Skip.Contains(name)) { CollectAnchors(child); return; }
            if (IsFootnoteBody(child)) return; // bodies are resolved through their references

            var sem = Semantic(child);
            var inherited = sem.Length > 0 ? sem : semantics;
            if (child.Id is { Length: > 0 } id) _pendingAnchors.Add(id);

            if (name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
            {
                Emit(child, RawKind.Heading, name[1] - '0', inherited);
            }
            else if (name == "hr")
            {
                Add(new RawBlock { Kind = RawKind.SceneBreak, Semantics = inherited });
            }
            else if (name is "p" or "dt" or "dd" or "pre" or "figcaption" or "td" or "th" || (BlockTags.Contains(name) && !HasBlockChildren(child)))
            {
                var kind = name == "pre" || HasClass(child, "poem", "poetry", "verse", "stanza") ? RawKind.Verse : RawKind.Paragraph;
                Emit(child, kind, 0, inherited);
            }
            else if (BlockTags.Contains(name))
            {
                // A container of blocks. If it also holds loose inline text, emit that text as its own paragraph.
                if (HasLooseText(child)) EmitMixed(child, inherited);
                else Walk(child, inherited);
            }
            else
            {
                // Inline element directly inside a container (e.g. <span> in <body>): treat as a paragraph.
                Emit(child, RawKind.Paragraph, 0, inherited);
            }
        }
    }

    private void EmitMixed(IElement container, string semantics)
    {
        var inline = new StringBuilder();
        var refs = new List<string>();
        foreach (var node in container.ChildNodes)
        {
            if (node is IElement e && BlockTags.Contains(e.LocalName))
            {
                FlushInline(inline, refs, semantics);
                WalkOne(e, semantics);
            }
            else
            {
                AppendInline(node, inline, refs);
            }
        }
        FlushInline(inline, refs, semantics);
    }

    private void FlushInline(StringBuilder sb, List<string> refs, string semantics)
    {
        var text = Collapse(sb.ToString());
        sb.Clear();
        if (text.Length == 0) { refs.Clear(); return; }
        Add(new RawBlock { Kind = RawKind.Paragraph, Text = text, NoteRefs = new List<string>(refs), Semantics = semantics });
        refs.Clear();
    }

    private void Emit(IElement el, RawKind kind, int level, string semantics)
    {
        var sb = new StringBuilder();
        var refs = new List<string>();
        CollectAnchors(el);
        foreach (var node in el.ChildNodes) AppendInline(node, sb, refs, verse: kind == RawKind.Verse);
        var text = kind == RawKind.Verse ? CollapseVerse(sb.ToString()) : Collapse(sb.ToString());
        // Unlinked bracketed note markers ("Newspeak[1]") are never meant to be read aloud.
        if (text.Contains('[')) text = BracketMarker().Replace(text, "");
        text = SpaceBeforePunctuation().Replace(text, "$1");

        if (text.Length == 0) return;
        if (kind != RawKind.Heading && IsSceneBreakText(text)) { Add(new RawBlock { Kind = RawKind.SceneBreak, Semantics = semantics }); return; }
        if (kind == RawKind.Paragraph && PageNumber().IsMatch(text)) return; // stray page numbers from PDF conversions

        Add(new RawBlock { Kind = kind, Level = level, Text = text, NoteRefs = refs, Semantics = semantics });
    }

    private void Add(RawBlock b)
    {
        b.DocPath = _docPath;
        b.Anchors.AddRange(_pendingAnchors);
        _pendingAnchors.Clear();
        _blocks.Add(b);
    }

    private void CollectAnchors(IElement el)
    {
        foreach (var d in el.QuerySelectorAll("[id]"))
            if (d.Id is { Length: > 0 } id) _pendingAnchors.Add(id);
    }

    private void AppendInline(INode node, StringBuilder sb, List<string> refs, bool verse = false)
    {
        switch (node)
        {
            case IText t:
                sb.Append(t.Data);
                break;
            case IElement e:
                var name = e.LocalName;
                if (Skip.Contains(name)) return;
                if (name == "br") { sb.Append(verse ? '\n' : ' '); return; }
                if (IsNoteRef(e, out var target))
                {
                    if (target is not null) refs.Add(target);
                    return; // drop the marker ("12", "*") from the spoken text
                }
                if (name == "a" && LinksElsewhere(e) && IsMarkerText(e.TextContent))
                {
                    refs.Add(ResolveHref(e)); // the whole link is a note marker ("12", "e1")
                    return;
                }
                if (name == "a" && LinksElsewhere(e))
                {
                    // PDF-reflow books wrap ordinary words in links whose only extra content is a note number.
                    foreach (var child in e.ChildNodes)
                    {
                        if (child is IElement ce && IsMarkerText(ce.TextContent)) { refs.Add(ResolveHref(e)); continue; }
                        AppendInline(child, sb, refs, verse);
                    }
                    return;
                }
                if ((name == "sup" || HasClass(e, "footnote-ref", "noteref", "fnref")) && IsMarkerText(e.TextContent)) return;
                foreach (var child in e.ChildNodes) AppendInline(child, sb, refs, verse);
                if (BlockTags.Contains(name)) sb.Append(' ');
                break;
        }
    }

    private bool IsNoteRef(IElement e, out string? target)
    {
        target = null;
        var type = e.GetAttribute("epub:type") ?? "";
        var role = e.GetAttribute("role") ?? "";
        bool marked = type.Contains("noteref") || role.Contains("doc-noteref") || HasClass(e, "noteref", "footnote-ref", "fnref");
        if (!marked && e.LocalName == "a" && e.ParentElement?.LocalName == "sup" && IsMarkerText(e.TextContent)) marked = true;
        if (!marked && e.LocalName == "sup" && e.QuerySelector("a[href]") is { } inner && IsMarkerText(e.TextContent))
        {
            target = ResolveHref(inner);
            return true;
        }
        if (!marked) return false;
        var a = e.LocalName == "a" ? e : e.QuerySelector("a[href]");
        if (a is not null) target = ResolveHref(a);
        return true;
    }

    private string ResolveHref(IElement a)
    {
        var href = a.GetAttribute("href") ?? "";
        var path = SafeArchive.Resolve(_docPath, href) ?? _docPath;
        var frag = href.Contains('#') ? href[(href.IndexOf('#') + 1)..] : "";
        return path + "#" + frag;
    }

    private static bool LinksElsewhere(IElement a) => (a.GetAttribute("href") ?? "").Length > 0;

    private static bool IsFootnoteBody(IElement e)
    {
        var type = e.GetAttribute("epub:type") ?? "";
        var role = e.GetAttribute("role") ?? "";
        return type.Contains("footnote") || type.Contains("endnote") || type.Contains("rearnote")
               || role.Contains("doc-footnote") || role.Contains("doc-endnote")
               || (e.LocalName == "aside" && (type.Contains("note") || HasClass(e, "footnote", "endnote")));
    }

    private static bool HasBlockChildren(IElement e) => e.Children.Any(c => BlockTags.Contains(c.LocalName) && c.LocalName != "br");

    private static bool HasLooseText(IElement e) =>
        e.ChildNodes.Any(n => (n is IText t && !string.IsNullOrWhiteSpace(t.Data)) || (n is IElement c && !BlockTags.Contains(c.LocalName) && !Skip.Contains(c.LocalName) && !string.IsNullOrWhiteSpace(c.TextContent)));

    private static bool HasClass(IElement e, params string[] names)
    {
        var cls = e.ClassName;
        if (string.IsNullOrEmpty(cls)) return false;
        foreach (var n in names)
            if (cls.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal static bool IsMarkerText(string s)
    {
        s = s.Trim();
        if (s.Length is 0 or > 5) return false;
        // Annotated editions use letter-prefixed markers such as "e12".
        if (s.Length >= 2 && char.IsLetter(s[0]) && s.Skip(1).All(char.IsDigit)) return true;
        return s.All(c => char.IsDigit(c) || c is '*' or '†' or '‡' or '§' or '[' or ']' or '(' or ')');
    }

    internal static bool IsSceneBreakText(string text)
    {
        if (text.Length > 12) return false;
        return text.All(c => c is '*' or '•' or '·' or '#' or '~' or '-' or '—' or '⁂' or '❧' or '◆' or '◇' or 'x' or 'X' || char.IsWhiteSpace(c))
               && text.Any(c => !char.IsWhiteSpace(c));
    }

    internal static string Collapse(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c) || c == ' ' || c == '​' || c == '﻿')
            {
                space = sb.Length > 0;
                continue;
            }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string CollapseVerse(string s) =>
        string.Join('\n', s.Split('\n').Select(Collapse).Where(l => l.Length > 0));

    [GeneratedRegex(@"^[\[\(]?\s*(page\s*)?\d{1,4}\s*[\]\)]?$", RegexOptions.IgnoreCase)]
    private static partial Regex PageNumber();

    [GeneratedRegex(@"(?<=\S)\[\d{1,3}\]")]
    private static partial Regex BracketMarker();

    [GeneratedRegex(@"(?<=\w) +([,.;:!?](?:\s|$))")]
    private static partial Regex SpaceBeforePunctuation();
}
