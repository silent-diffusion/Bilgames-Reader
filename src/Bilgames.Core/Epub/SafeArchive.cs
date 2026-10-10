using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace Bilgames.Core.Epub;

public class EpubFormatException : Exception
{
    public EpubFormatException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class DrmProtectedException : EpubFormatException
{
    public DrmProtectedException() : base("This book is DRM-protected. Bilgames Reader can only read DRM-free EPUBs.") { }
}

/// <summary>
/// Read-only view over an EPUB zip that treats the archive as untrusted: entries are only ever
/// read into memory (never extracted to disk), sizes are capped while decompressing, and XML is
/// parsed with DTDs ignored and no external resolver.
/// </summary>
public sealed class SafeArchive : IDisposable
{
    public const long MaxEntryBytes = 64L * 1024 * 1024;
    public const long MaxTotalBytes = 1024L * 1024 * 1024;
    public const int MaxEntries = 20_000;

    private readonly ZipArchive _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _entries;
    private long _totalRead;

    public SafeArchive(Stream stream)
    {
        try
        {
            _zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException ex)
        {
            stream.Dispose();
            throw new EpubFormatException("The file isn't a valid EPUB (it is not a zip archive).", ex);
        }

        if (_zip.Entries.Count > MaxEntries)
        {
            _zip.Dispose();
            throw new EpubFormatException("The EPUB contains too many files to be a normal book.");
        }

        _entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _zip.Entries)
        {
            var key = Normalize(e.FullName);
            if (key is null) continue; // path escapes the archive root: ignore it entirely
            _entries.TryAdd(key, e);
        }
    }

    public static SafeArchive Open(string path) => new(File.OpenRead(path));

    public IEnumerable<string> EntryNames => _entries.Keys;

    public bool Contains(string path) => Normalize(path) is { } k && _entries.ContainsKey(k);

    /// <summary>Normalizes a zip path; returns null for paths that try to climb above the root.</summary>
    public static string? Normalize(string path)
    {
        path = Uri.UnescapeDataString(path.Replace('\\', '/'));
        var stack = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part.Length == 0 || part == ".") continue;
            if (part == "..")
            {
                if (stack.Count == 0) return null;
                stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(part);
        }
        return string.Join('/', stack);
    }

    /// <summary>Resolves an href relative to the directory of <paramref name="baseFile"/>.</summary>
    public static string? Resolve(string baseFile, string href)
    {
        int hash = href.IndexOf('#');
        if (hash >= 0) href = href[..hash];
        if (href.Length == 0) return Normalize(baseFile);
        if (href.Contains("://", StringComparison.Ordinal)) return null; // remote resource: never followed
        int slash = baseFile.LastIndexOf('/');
        var dir = slash >= 0 ? baseFile[..(slash + 1)] : "";
        return Normalize(dir + href);
    }

    public byte[] ReadBytes(string path)
    {
        var key = Normalize(path) ?? throw new EpubFormatException($"Invalid path in EPUB: {path}");
        if (!_entries.TryGetValue(key, out var entry))
            throw new EpubFormatException($"The EPUB is missing a required file: {path}");
        if (entry.Length > MaxEntryBytes)
            throw new EpubFormatException($"A file inside the EPUB is unreasonably large ({entry.Length / 1048576} MB).");

        using var s = entry.Open();
        using var ms = new MemoryStream((int)Math.Min(entry.Length, MaxEntryBytes));
        var buffer = new byte[81920];
        int n;
        long readThis = 0;
        while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
        {
            readThis += n;
            _totalRead += n;
            // The header's declared length can lie; enforce limits on the real decompressed size.
            if (readThis > MaxEntryBytes || _totalRead > MaxTotalBytes)
                throw new EpubFormatException("The EPUB expands to an unsafe size (possible decompression bomb).");
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }

    public string ReadText(string path)
    {
        var bytes = ReadBytes(path);
        using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public XDocument ReadXml(string path)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = 64L * 1024 * 1024,
            IgnoreComments = true,
        };
        try
        {
            using var ms = new MemoryStream(ReadBytes(path));
            using var reader = XmlReader.Create(ms, settings);
            return XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new EpubFormatException($"The EPUB has a malformed file: {path}", ex);
        }
    }

    public void Dispose() => _zip.Dispose();
}
