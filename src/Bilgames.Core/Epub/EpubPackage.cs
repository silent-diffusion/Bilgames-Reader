using System.Xml.Linq;

namespace Bilgames.Core.Epub;

public sealed record ManifestItem(string Id, string Path, string MediaType, string Properties);

public sealed record TocEntry(string Label, string Path, string? Fragment);

/// <summary>Parsed container.xml + OPF package document + navigation.</summary>
public sealed class EpubPackage
{
    public string Title { get; private set; } = "";
    public string Author { get; private set; } = "";
    public string Language { get; private set; } = "en";
    public string OpfPath { get; private set; } = "";
    public Dictionary<string, ManifestItem> Manifest { get; } = new();
    public List<ManifestItem> Spine { get; } = new();
    public List<TocEntry> Toc { get; } = new();
    /// <summary>EPUB 2 guide / EPUB 3 landmarks: path → semantic type (e.g. "toc", "bodymatter").</summary>
    public Dictionary<string, string> Landmarks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? CoverImagePath { get; private set; }

    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Ncx = "http://www.daisy.org/z3986/2005/ncx/";
    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace Ops = "http://www.idpf.org/2007/ops";
    private static readonly XNamespace Container = "urn:oasis:names:tc:opendocument:xmlns:container";
    private static readonly XNamespace Enc = "http://www.w3.org/2001/04/xmlenc#";

    // Font obfuscation is not DRM; anything else in encryption.xml means the text is encrypted.
    private static readonly HashSet<string> FontObfuscation = new(StringComparer.OrdinalIgnoreCase)
    {
        "http://www.idpf.org/2008/embedding",
        "http://ns.adobe.com/pdf/enc#RC",
    };

    public static EpubPackage Load(SafeArchive zip)
    {
        var pkg = new EpubPackage();
        pkg.CheckDrm(zip);

        if (!zip.Contains("META-INF/container.xml"))
            throw new EpubFormatException("This file is missing META-INF/container.xml, so it isn't a valid EPUB.");
        var container = zip.ReadXml("META-INF/container.xml");
        var rootfile = container.Descendants(Container + "rootfile")
            .FirstOrDefault(r => ((string?)r.Attribute("media-type") ?? "application/oebps-package+xml") == "application/oebps-package+xml")
            ?? container.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile");
        var opfPath = SafeArchive.Normalize((string?)rootfile?.Attribute("full-path") ?? "");
        if (string.IsNullOrEmpty(opfPath) || !zip.Contains(opfPath))
            throw new EpubFormatException("The EPUB's package document (OPF) could not be found.");
        pkg.OpfPath = opfPath;

        var opf = zip.ReadXml(opfPath);
        pkg.ReadMetadata(opf);
        pkg.ReadManifestAndSpine(zip, opf);
        pkg.ReadNavigation(zip, opf);
        pkg.FindCover(zip, opf);
        return pkg;
    }

    private void CheckDrm(SafeArchive zip)
    {
        if (zip.Contains("META-INF/rights.xml")) throw new DrmProtectedException();
        if (!zip.Contains("META-INF/encryption.xml")) return;
        var enc = zip.ReadXml("META-INF/encryption.xml");
        foreach (var method in enc.Descendants(Enc + "EncryptionMethod"))
        {
            var alg = (string?)method.Attribute("Algorithm") ?? "";
            if (!FontObfuscation.Contains(alg)) throw new DrmProtectedException();
        }
    }

    private void ReadMetadata(XDocument opf)
    {
        var meta = opf.Root?.Element(Opf + "metadata") ?? opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "metadata");
        if (meta is null) return;
        Title = Clean(meta.Elements(Dc + "title").Select(e => e.Value).FirstOrDefault()) ?? "";
        var creators = meta.Elements(Dc + "creator").ToList();
        var author = creators.FirstOrDefault(c => ((string?)c.Attribute(Opf + "role") ?? "aut") == "aut") ?? creators.FirstOrDefault();
        Author = Clean(author?.Value) ?? "";
        Language = Clean(meta.Elements(Dc + "language").Select(e => e.Value).FirstOrDefault()) ?? "en";
    }

    private void ReadManifestAndSpine(SafeArchive zip, XDocument opf)
    {
        foreach (var item in opf.Descendants(Opf + "item"))
        {
            var id = (string?)item.Attribute("id");
            var href = (string?)item.Attribute("href");
            if (id is null || href is null) continue;
            var path = SafeArchive.Resolve(OpfPath, href);
            if (path is null) continue;
            Manifest[id] = new ManifestItem(id, path, (string?)item.Attribute("media-type") ?? "", (string?)item.Attribute("properties") ?? "");
        }

        foreach (var itemref in opf.Descendants(Opf + "itemref"))
        {
            var idref = (string?)itemref.Attribute("idref");
            if (idref is null || !Manifest.TryGetValue(idref, out var item)) continue;
            if (!item.MediaType.Contains("html", StringComparison.OrdinalIgnoreCase)) continue;
            if (!zip.Contains(item.Path)) continue;
            Spine.Add(item);
        }

        if (Spine.Count == 0) throw new EpubFormatException("The EPUB has no readable chapters in its reading order.");

        foreach (var reference in opf.Descendants(Opf + "reference"))
        {
            var href = (string?)reference.Attribute("href");
            var type = (string?)reference.Attribute("type");
            if (href is null || type is null) continue;
            var path = SafeArchive.Resolve(OpfPath, href);
            if (path is not null) Landmarks.TryAdd(path, type);
        }
    }

    private void ReadNavigation(SafeArchive zip, XDocument opf)
    {
        // EPUB 3 nav document first; fall back to the EPUB 2 NCX.
        var nav = Manifest.Values.FirstOrDefault(m => m.Properties.Split(' ').Contains("nav"));
        if (nav is not null && zip.Contains(nav.Path))
        {
            try
            {
                var doc = Xhtml5.Parse(zip.ReadText(nav.Path));
                foreach (var navEl in doc.QuerySelectorAll("nav"))
                {
                    var type = navEl.GetAttribute("epub:type") ?? "";
                    var target = type.Contains("landmarks") ? null : Toc;
                    foreach (var a in navEl.QuerySelectorAll("a[href]"))
                    {
                        var href = a.GetAttribute("href")!;
                        var path = SafeArchive.Resolve(nav.Path, href);
                        if (path is null) continue;
                        var frag = href.Contains('#') ? href[(href.IndexOf('#') + 1)..] : null;
                        if (target is null)
                            Landmarks.TryAdd(path, a.GetAttribute("epub:type") ?? "");
                        else if (type.Contains("toc") || type.Length == 0)
                            target.Add(new TocEntry(Clean(a.TextContent) ?? "", path, frag));
                    }
                }
            }
            catch (Exception) { /* a broken nav document is not fatal: headings still drive chapters */ }
        }

        if (Toc.Count > 0) return;
        var spineEl = opf.Descendants(Opf + "spine").FirstOrDefault();
        var ncxId = (string?)spineEl?.Attribute("toc");
        var ncx = ncxId is not null && Manifest.TryGetValue(ncxId, out var n) ? n
            : Manifest.Values.FirstOrDefault(m => m.MediaType == "application/x-dtbncx+xml");
        if (ncx is null || !zip.Contains(ncx.Path)) return;
        try
        {
            var doc = zip.ReadXml(ncx.Path);
            foreach (var point in doc.Descendants(Ncx + "navPoint"))
            {
                var label = point.Element(Ncx + "navLabel")?.Element(Ncx + "text")?.Value;
                var src = (string?)point.Element(Ncx + "content")?.Attribute("src");
                if (src is null) continue;
                var path = SafeArchive.Resolve(ncx.Path, src);
                if (path is null) continue;
                var frag = src.Contains('#') ? src[(src.IndexOf('#') + 1)..] : null;
                Toc.Add(new TocEntry(Clean(label) ?? "", path, frag));
            }
        }
        catch (EpubFormatException) { }
    }

    private void FindCover(SafeArchive zip, XDocument opf)
    {
        static bool IsImage(ManifestItem m) => m.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

        var byProperty = Manifest.Values.FirstOrDefault(m => m.Properties.Split(' ').Contains("cover-image") && IsImage(m));
        var metaCover = opf.Descendants(Opf + "meta").FirstOrDefault(m => (string?)m.Attribute("name") == "cover");
        var metaId = (string?)metaCover?.Attribute("content");
        var byMeta = metaId is not null && Manifest.TryGetValue(metaId, out var mc) && IsImage(mc) ? mc : null;
        var byName = Manifest.Values.FirstOrDefault(m => IsImage(m) && m.Path.Contains("cover", StringComparison.OrdinalIgnoreCase));

        var cover = byProperty ?? byMeta ?? byName;
        if (cover is null)
        {
            // Many converters put the cover image on the first spine page.
            try
            {
                var first = Spine[0];
                var doc = Xhtml5.Parse(zip.ReadText(first.Path));
                var text = doc.Body?.TextContent.Trim() ?? "";
                var img = doc.QuerySelector("img[src]")?.GetAttribute("src")
                          ?? doc.QuerySelector("image")?.GetAttribute("xlink:href")
                          ?? doc.QuerySelector("image")?.GetAttribute("href");
                if (img is not null && text.Length < 200)
                {
                    var p = SafeArchive.Resolve(first.Path, img);
                    if (p is not null && zip.Contains(p)) CoverImagePath = p;
                }
            }
            catch (Exception) { }
            return;
        }
        if (zip.Contains(cover.Path)) CoverImagePath = cover.Path;
    }

    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
