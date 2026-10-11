using System.Buffers.Binary;
using System.Text;

namespace Bilgames.Core.Audio;

public sealed record Mp4Chapter(TimeSpan Start, string Title);

/// <summary>What goes into an audiobook file besides the audio.</summary>
public sealed record AudiobookInfo(string Title, string Author, IReadOnlyList<Mp4Chapter> Chapters, byte[]? Cover = null);

/// <summary>
/// Turns an AAC .m4a (as Windows' encoder writes it) into a proper .m4b audiobook:
/// <list type="bullet">
/// <item>file type "M4B " so players treat it as an audiobook, not a generic MP4;</item>
/// <item>a QuickTime chapter track (a text track the audio track points to with tref/chap) — what
/// Apple Books, iOS and most audiobook players read — plus the older Nero "chpl" list for the rest;</item>
/// <item>iTunes-style tags: title, album, author, album artist, genre, media kind "Audiobook", cover art.</item>
/// </list>
/// The file is rewritten as ftyp, moov, the original audio data, then the chapter titles, streaming
/// the audio rather than loading it, and every audio chunk offset is moved to match.
/// </summary>
public static class Mp4Metadata
{
    private const uint ChapterTimescale = 1000; // chapter track times in milliseconds

    public static void Write(string path, IReadOnlyList<Mp4Chapter> chapters, string title, string author) =>
        Write(path, new AudiobookInfo(title, author, chapters));

    public static void Write(string path, AudiobookInfo info)
    {
        var tmp = path + ".tmp";
        using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
        {
            var top = ReadTopLevel(src);
            var moovRef = top.FirstOrDefault(b => b.Type == "moov");
            var mdatRef = top.FirstOrDefault(b => b.Type == "mdat");
            if (moovRef.Size == 0) throw new InvalidDataException("The audio file has no moov box.");
            if (mdatRef.Size == 0) throw new InvalidDataException("The audio file has no audio data (mdat).");

            var moov = new byte[moovRef.Size];
            src.Position = moovRef.Offset;
            src.ReadExactly(moov);

            var ftyp = Box("ftyp", Concat(Ascii("M4B "), U32(0), Ascii("M4B "), Ascii("M4A "), Ascii("mp42"), Ascii("isom")));
            var chapters = Normalise(info.Chapters);
            var samples = chapters.Select(c => ChapterSample(c.Title)).ToList();
            var chapterData = Concat(samples.ToArray());

            // Build once to learn the moov size, then again with the final offsets (sizes don't change).
            var draft = BuildMoov(moov, moovRef.HeaderSize, info, chapters, samples, audioShift: 0, chapterDataOffset: 0);
            long newMdatOffset = ftyp.Length + draft.Length;
            long audioShift = newMdatOffset - mdatRef.Offset;
            long chapterBoxOffset = newMdatOffset + mdatRef.Size;
            long chapterDataOffset = chapterBoxOffset + 8;
            if (chapterDataOffset + chapterData.Length > uint.MaxValue)
                throw new InvalidDataException("The audiobook is larger than 4 GB, which this exporter can't write yet.");
            var finalMoov = BuildMoov(moov, moovRef.HeaderSize, info, chapters, samples, audioShift, chapterDataOffset);

            using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            dst.Write(ftyp);
            dst.Write(finalMoov);
            src.Position = mdatRef.Offset;
            CopyBytes(src, dst, mdatRef.Size);
            dst.Write(Box("mdat", chapterData));
        }
        File.Move(tmp, path, overwrite: true);
    }

    // ---------- moov ----------

    private static byte[] BuildMoov(byte[] moov, int headerSize, AudiobookInfo info, List<Mp4Chapter> chapters, List<byte[]> samples, long audioShift, long chapterDataOffset)
    {
        var children = ReadBoxes(moov, headerSize, moov.Length);
        var mvhd = children.FirstOrDefault(c => c.Type == "mvhd");
        // Every real file has a movie header; without one, time the last chapter as running a second.
        var (movieTimescale, movieDuration) = mvhd.Size > 0 ? ReadTimes(moov, mvhd)
            : (1000u, (ulong)(chapters.Count == 0 ? 0 : chapters[^1].Start.TotalMilliseconds + 1000));
        var audio = children.Where(c => c.Type == "trak" && !IsChapterTextTrack(Slice(moov, c))).ToList();
        uint chapterTrackId = (uint)audio.Select(t => TrackId(moov, t)).DefaultIfEmpty(0u).Max() + 1;

        using var body = new MemoryStream();
        foreach (var c in children)
        {
            if (c.Type is "udta" or "meta") continue; // replaced below
            var bytes = Slice(moov, c);
            if (c.Type == "mvhd") SetNextTrackId(bytes, c.HeaderSize, chapterTrackId + 1);
            if (c.Type == "trak")
            {
                if (IsChapterTextTrack(bytes)) continue; // from an earlier export of this file
                ShiftChunkOffsets(bytes, c.HeaderSize, bytes.Length, audioShift);
                if (chapters.Count > 0 && IsSoundTrack(bytes)) bytes = WithChapterReference(bytes, c.HeaderSize, chapterTrackId);
            }
            body.Write(bytes);
        }
        if (chapters.Count > 0)
            body.Write(ChapterTrack(chapterTrackId, chapters, samples, movieTimescale, movieDuration, chapterDataOffset));
        body.Write(BuildUdta(info, chapters));
        return Box("moov", body.ToArray());
    }

    /// <summary>A disabled text track whose samples are the chapter titles, timed to each chapter.</summary>
    private static byte[] ChapterTrack(uint trackId, List<Mp4Chapter> chapters, List<byte[]> samples, uint movieTimescale, ulong movieDuration, long dataOffset)
    {
        var totalMs = (ulong)Math.Round(movieDuration * 1000.0 / Math.Max(1, movieTimescale));
        var starts = chapters.Select(c => (ulong)Math.Round(c.Start.TotalMilliseconds)).ToList();
        var durations = starts.Select((s, i) => (i + 1 < starts.Count ? starts[i + 1] : Math.Max(totalMs, s + 1)) - s).ToList();

        var tkhd = FullBox("tkhd", 0, 0x000002 /* in movie, not enabled: never shown as a subtitle */, Concat(
            U32(0), U32(0), U32(trackId), U32(0), U32((uint)Math.Min(uint.MaxValue, movieDuration)),
            new byte[8], U16(0), U16(0), U16(0), U16(0), IdentityMatrix(), U32(0), U32(0)));
        var mdhd = FullBox("mdhd", 0, 0, Concat(U32(0), U32(0), U32(ChapterTimescale), U32((uint)Math.Min(uint.MaxValue, totalMs)), U16(0x55C4) /* und */, U16(0)));
        var hdlr = FullBox("hdlr", 0, 0, Concat(U32(0), Ascii("text"), new byte[12], Encoding.ASCII.GetBytes("Chapters\0")));

        // QuickTime generic media header with the 'text' extension, as QuickTime/ffmpeg write it.
        var gmin = FullBox("gmin", 0, 0, Concat(U16(0x40), U16(0x8000), U16(0x8000), U16(0x8000), U16(0), U16(0)));
        var textAtom = Box("text", Concat(U16(1), U32(0), U32(0), U32(0), U32(1), U32(0), U32(0), U32(0), U32(0x00004000), U16(0)));
        var gmhd = Box("gmhd", Concat(gmin, textAtom));
        var dinf = Box("dinf", FullBox("dref", 0, 0, Concat(U32(1), FullBox("url ", 0, 1, Array.Empty<byte>()))));

        var stsd = FullBox("stsd", 0, 0, Concat(U32(1), Box("text", Concat(new byte[6], U16(1), TextSampleEntry()))));
        var stts = FullBox("stts", 0, 0, Concat(U32((uint)durations.Count), Concat(durations.Select(d => Concat(U32(1), U32((uint)d))).ToArray())));
        var stsc = FullBox("stsc", 0, 0, Concat(U32(1), U32(1), U32((uint)samples.Count), U32(1)));
        var stsz = FullBox("stsz", 0, 0, Concat(U32(0), U32((uint)samples.Count), Concat(samples.Select(s => U32((uint)s.Length)).ToArray())));
        var stco = FullBox("stco", 0, 0, Concat(U32(1), U32((uint)dataOffset)));
        var stbl = Box("stbl", Concat(stsd, stts, stsc, stsz, stco));

        var minf = Box("minf", Concat(gmhd, dinf, stbl));
        var mdia = Box("mdia", Concat(mdhd, hdlr, minf));
        return Box("trak", Concat(tkhd, mdia));
    }

    /// <summary>QuickTime/3GPP text sample description, as ffmpeg writes it for chapter tracks.</summary>
    private static byte[] TextSampleEntry() => new byte[]
    {
        0x00, 0x00, 0x00, 0x01,             // display flags
        0x00, 0x00,                         // horizontal + vertical justification
        0x00, 0x00, 0x00, 0x00,             // background colour
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // default text box
        0x00, 0x00, 0x00, 0x00,             // style: start/end char
        0x00, 0x01,                         // font id
        0x00, 0x00,                         // face style + size
        0x00, 0x00, 0x00, 0x00,             // foreground colour
        0x00, 0x00, 0x00, 0x0D, (byte)'f', (byte)'t', (byte)'a', (byte)'b', 0x00, 0x01, 0x00, 0x01, 0x00, // font table
    };

    /// <summary>A chapter title sample: 16-bit length, UTF-8 text, and an 'encd' box declaring UTF-8.</summary>
    private static byte[] ChapterSample(string title)
    {
        var text = Truncate(title, 1000);
        return Concat(U16((ushort)text.Length), text, new byte[] { 0, 0, 0, 12, (byte)'e', (byte)'n', (byte)'c', (byte)'d', 0, 0, 1, 0 });
    }

    /// <summary>Sorted, starting at zero (players expect the first chapter to begin the book), no duplicates.</summary>
    private static List<Mp4Chapter> Normalise(IReadOnlyList<Mp4Chapter> chapters)
    {
        var list = chapters.OrderBy(c => c.Start).GroupBy(c => (long)Math.Round(c.Start.TotalMilliseconds)).Select(g => g.First()).ToList();
        if (list.Count > 0 && list[0].Start > TimeSpan.Zero) list[0] = list[0] with { Start = TimeSpan.Zero };
        return list;
    }

    private static byte[] WithChapterReference(byte[] trak, int headerSize, uint chapterTrackId)
    {
        var parts = ReadBoxes(trak, headerSize, trak.Length);
        using var body = new MemoryStream();
        foreach (var p in parts)
        {
            if (p.Type == "tref") continue;
            body.Write(trak, (int)p.Offset, (int)p.Size);
            if (p.Type == "tkhd") body.Write(Box("tref", Box("chap", U32(chapterTrackId))));
        }
        return Box("trak", body.ToArray());
    }

    private static bool IsSoundTrack(byte[] trak) => HandlerOf(trak) == "soun";
    private static bool IsChapterTextTrack(byte[] trak) => HandlerOf(trak) == "text";

    private static string? HandlerOf(byte[] trak)
    {
        var mdia = ReadBoxes(trak, 8, trak.Length).FirstOrDefault(b => b.Type == "mdia");
        if (mdia.Size == 0) return null;
        var hdlr = ReadBoxes(trak, mdia.Offset + mdia.HeaderSize, mdia.Offset + mdia.Size).FirstOrDefault(b => b.Type == "hdlr");
        return hdlr.Size == 0 ? null : Encoding.ASCII.GetString(trak, (int)hdlr.Offset + hdlr.HeaderSize + 8, 4);
    }

    private static uint TrackId(byte[] moov, BoxRef trak)
    {
        var tkhd = ReadBoxes(moov, trak.Offset + trak.HeaderSize, trak.Offset + trak.Size).FirstOrDefault(b => b.Type == "tkhd");
        if (tkhd.Size == 0) return 0;
        int p = (int)tkhd.Offset + tkhd.HeaderSize;
        return BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(p + (moov[p] == 1 ? 20 : 12)));
    }

    private static (uint Timescale, ulong Duration) ReadTimes(byte[] moov, BoxRef mvhd)
    {
        int p = (int)mvhd.Offset + mvhd.HeaderSize;
        return moov[p] == 1
            ? (BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(p + 20)), BinaryPrimitives.ReadUInt64BigEndian(moov.AsSpan(p + 24)))
            : (BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(p + 12)), BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(p + 16)));
    }

    private static void SetNextTrackId(byte[] mvhd, int headerSize, uint next)
    {
        // version 0: 96 bytes in; version 1: 108 (64-bit times).
        int at = headerSize + (mvhd[headerSize] == 1 ? 108 : 96);
        BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(at), next);
    }

    // ---------- udta / tags ----------

    private static byte[] BuildUdta(AudiobookInfo info, List<Mp4Chapter> chapters)
    {
        // Nero chapter list: version 1, then (start in 100 ns units, pascal-string title) per chapter.
        using var chpl = new MemoryStream();
        chpl.Write(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 });
        var list = chapters.Take(255).ToList();
        chpl.WriteByte((byte)list.Count);
        Span<byte> buf = stackalloc byte[8];
        foreach (var c in list)
        {
            BinaryPrimitives.WriteInt64BigEndian(buf, c.Start.Ticks);
            chpl.Write(buf);
            var t = Truncate(c.Title, 255);
            chpl.WriteByte((byte)t.Length);
            chpl.Write(t);
        }

        // iTunes metadata: meta (full box) → hdlr 'mdir' → ilst.
        using var ilst = new MemoryStream();
        ilst.Write(TextTag("©nam", info.Title));
        ilst.Write(TextTag("©alb", info.Title));
        if (info.Author.Length > 0)
        {
            ilst.Write(TextTag("©ART", info.Author));
            ilst.Write(TextTag("aART", info.Author));
        }
        ilst.Write(TextTag("©gen", "Audiobook"));
        ilst.Write(TextTag("©too", "Bilgames Reader"));
        ilst.Write(Tag("stik", 21, new byte[] { 2 })); // media kind: Audiobook
        if (info.Cover is { Length: > 0 } cover && CoverType(cover) is { } type) ilst.Write(Tag("covr", type, cover));

        var hdlr = Box("hdlr", Concat(new byte[8], Ascii("mdir"), Ascii("appl"), new byte[9]));
        var meta = Box("meta", Concat(new byte[4], hdlr, Box("ilst", ilst.ToArray())));
        return Box("udta", Concat(Box("chpl", chpl.ToArray()), meta));
    }

    /// <summary>iTunes data type for a cover: 13 JPEG, 14 PNG; null for anything else.</summary>
    private static int? CoverType(byte[] image) =>
        image.Length > 3 && image[0] == 0xFF && image[1] == 0xD8 ? 13
        : image.Length > 8 && image[0] == 0x89 && image[1] == (byte)'P' && image[2] == (byte)'N' && image[3] == (byte)'G' ? 14
        : null;

    private static byte[] TextTag(string name, string value) => Tag(name, 1, Encoding.UTF8.GetBytes(value));

    private static byte[] Tag(string name, int type, byte[] value)
    {
        var data = Box("data", Concat(U32((uint)type), U32(0), value));
        return Box(name, data, latin1Type: true);
    }

    // ---------- reading back (tests, checks) ----------

    /// <summary>Reads back the Nero chapter list.</summary>
    public static List<Mp4Chapter> ReadChapters(string path)
    {
        var moov = ReadMoov(path);
        var result = new List<Mp4Chapter>();
        if (moov is null) return result;
        var udta = ReadBoxes(moov, 8, moov.Length).FirstOrDefault(b => b.Type == "udta");
        if (udta.Size == 0) return result;
        var chpl = ReadBoxes(moov, udta.Offset + udta.HeaderSize, udta.Offset + udta.Size).FirstOrDefault(b => b.Type == "chpl");
        if (chpl.Size == 0) return result;
        long p = chpl.Offset + chpl.HeaderSize + 8;
        int count = moov[p++];
        for (int i = 0; i < count; i++)
        {
            long start = BinaryPrimitives.ReadInt64BigEndian(moov.AsSpan((int)p));
            p += 8;
            int len = moov[p++];
            result.Add(new Mp4Chapter(TimeSpan.FromTicks(start), Encoding.UTF8.GetString(moov, (int)p, len)));
            p += len;
        }
        return result;
    }

    /// <summary>Reads back the QuickTime chapter track (what Apple Books and most players use).</summary>
    public static List<Mp4Chapter> ReadChapterTrack(string path)
    {
        var result = new List<Mp4Chapter>();
        var moov = ReadMoov(path);
        if (moov is null) return result;
        var traks = ReadBoxes(moov, 8, moov.Length).Where(b => b.Type == "trak").Select(t => Slice(moov, t)).ToList();
        var text = traks.FirstOrDefault(IsChapterTextTrack);
        if (text is null) return result;
        var stbl = Find(text, "mdia", "minf", "stbl");
        if (stbl is null) return result;
        var (s, start, end) = stbl.Value;
        var boxes = ReadBoxes(s, start, end);
        byte[] Body(string type) { var b = boxes.First(x => x.Type == type); return s[(int)(b.Offset + b.HeaderSize)..(int)(b.Offset + b.Size)]; }
        var stts = Body("stts"); var stsz = Body("stsz"); var stco = Body("stco");
        int count = (int)BinaryPrimitives.ReadUInt32BigEndian(stsz.AsSpan(8));
        long offset = BinaryPrimitives.ReadUInt32BigEndian(stco.AsSpan(8));
        using var file = File.OpenRead(path);
        long t = 0;
        int sttsEntries = (int)BinaryPrimitives.ReadUInt32BigEndian(stts.AsSpan(4));
        var deltas = new List<uint>();
        for (int i = 0; i < sttsEntries; i++)
            for (uint n = BinaryPrimitives.ReadUInt32BigEndian(stts.AsSpan(8 + i * 8)); n > 0; n--)
                deltas.Add(BinaryPrimitives.ReadUInt32BigEndian(stts.AsSpan(12 + i * 8)));
        for (int i = 0; i < count; i++)
        {
            int size = (int)BinaryPrimitives.ReadUInt32BigEndian(stsz.AsSpan(12 + i * 4));
            var sample = new byte[size];
            file.Position = offset;
            file.ReadExactly(sample);
            offset += size;
            int len = BinaryPrimitives.ReadUInt16BigEndian(sample);
            result.Add(new Mp4Chapter(TimeSpan.FromMilliseconds(t), Encoding.UTF8.GetString(sample, 2, len)));
            t += i < deltas.Count ? deltas[i] : 0;
        }
        return result;
    }

    /// <summary>The iTunes tags (name → text, or "image/jpeg"/"image/png (n bytes)" for covers), for tests and checks.</summary>
    public static Dictionary<string, string> ReadTags(string path)
    {
        var tags = new Dictionary<string, string>();
        var moov = ReadMoov(path);
        if (moov is null) return tags;
        var ilst = Find(moov, "udta", "meta", "ilst");
        if (ilst is null) return tags;
        var (s, start, end) = ilst.Value;
        foreach (var tag in ReadBoxes(s, start, end))
        {
            var data = ReadBoxes(s, tag.Offset + 8, tag.Offset + tag.Size).FirstOrDefault(b => b.Type == "data");
            if (data.Size == 0) continue;
            int type = (int)BinaryPrimitives.ReadUInt32BigEndian(s.AsSpan((int)data.Offset + 8));
            var value = s[(int)(data.Offset + 16)..(int)(data.Offset + data.Size)];
            var name = Encoding.Latin1.GetString(s, (int)tag.Offset + 4, 4);
            tags[name] = type switch { 1 => Encoding.UTF8.GetString(value), 13 => $"image/jpeg ({value.Length} bytes)", 14 => $"image/png ({value.Length} bytes)", _ => Convert.ToHexString(value) };
        }
        return tags;
    }

    /// <summary>The file's major brand, e.g. "M4B ".</summary>
    public static string ReadBrand(string path)
    {
        using var f = File.OpenRead(path);
        var head = new byte[12];
        f.ReadExactly(head);
        return Encoding.ASCII.GetString(head, 8, 4);
    }

    private static byte[]? ReadMoov(string path)
    {
        using var f = File.OpenRead(path);
        var moov = ReadTopLevel(f).FirstOrDefault(b => b.Type == "moov");
        if (moov.Size == 0) return null;
        var bytes = new byte[moov.Size];
        f.Position = moov.Offset;
        f.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>Walks down nested boxes (meta is a full box: its children start 4 bytes later).</summary>
    private static (byte[] Data, long Start, long End)? Find(byte[] data, params string[] pathTypes)
    {
        long start = 8, end = data.Length;
        foreach (var type in pathTypes)
        {
            var b = ReadBoxes(data, start, end).FirstOrDefault(x => x.Type == type);
            if (b.Size == 0) return null;
            start = b.Offset + b.HeaderSize + (type == "meta" ? 4 : 0);
            end = b.Offset + b.Size;
        }
        return (data, start, end);
    }

    // ---------- box plumbing ----------

    private readonly record struct BoxRef(string Type, long Offset, long Size, int HeaderSize);

    private static List<BoxRef> ReadTopLevel(Stream s)
    {
        var list = new List<BoxRef>();
        var head = new byte[16];
        long p = 0, end = s.Length;
        while (p + 8 <= end)
        {
            s.Position = p;
            s.ReadExactly(head, 0, 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(head);
            var type = Encoding.Latin1.GetString(head, 4, 4);
            int header = 8;
            if (size == 1)
            {
                s.ReadExactly(head, 8, 8);
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(head.AsSpan(8));
                header = 16;
            }
            else if (size == 0) size = end - p;
            if (size < header || p + size > end) break;
            list.Add(new BoxRef(type, p, size, header));
            p += size;
        }
        return list;
    }

    private static List<BoxRef> ReadBoxes(byte[] data, long start, long end)
    {
        var list = new List<BoxRef>();
        long p = start;
        while (p + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)p));
            var type = Encoding.Latin1.GetString(data, (int)p + 4, 4);
            int header = 8;
            if (size == 1)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan((int)p + 8));
                header = 16;
            }
            else if (size == 0) size = end - p;
            if (size < header || p + size > end) break;
            list.Add(new BoxRef(type, p, size, header));
            p += size;
        }
        return list;
    }

    private static readonly HashSet<string> Containers = new() { "moov", "trak", "mdia", "minf", "stbl", "edts", "dinf" };

    private static void ShiftChunkOffsets(byte[] box, long start, long end, long delta)
    {
        if (delta == 0) return;
        foreach (var b in ReadBoxes(box, start, end))
        {
            if (Containers.Contains(b.Type))
            {
                ShiftChunkOffsets(box, b.Offset + b.HeaderSize, b.Offset + b.Size, delta);
            }
            else if (b.Type == "stco")
            {
                int p = (int)(b.Offset + b.HeaderSize + 4);
                uint n = BinaryPrimitives.ReadUInt32BigEndian(box.AsSpan(p));
                p += 4;
                for (uint i = 0; i < n; i++, p += 4)
                {
                    long moved = BinaryPrimitives.ReadUInt32BigEndian(box.AsSpan(p)) + delta;
                    if (moved is < 0 or > uint.MaxValue) throw new InvalidDataException("The audiobook is larger than 4 GB, which this exporter can't write yet.");
                    BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(p), (uint)moved);
                }
            }
            else if (b.Type == "co64")
            {
                int p = (int)(b.Offset + b.HeaderSize + 4);
                uint n = BinaryPrimitives.ReadUInt32BigEndian(box.AsSpan(p));
                p += 4;
                for (uint i = 0; i < n; i++, p += 8)
                    BinaryPrimitives.WriteUInt64BigEndian(box.AsSpan(p), (ulong)((long)BinaryPrimitives.ReadUInt64BigEndian(box.AsSpan(p)) + delta));
            }
        }
    }

    private static void CopyBytes(Stream from, Stream to, long count)
    {
        var buffer = new byte[1 << 20];
        while (count > 0)
        {
            int n = from.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (n <= 0) throw new EndOfStreamException("The audio file ended early.");
            to.Write(buffer, 0, n);
            count -= n;
        }
    }

    private static byte[] Slice(byte[] data, BoxRef b) => data[(int)b.Offset..(int)(b.Offset + b.Size)];

    private static byte[] Truncate(string s, int maxBytes)
    {
        var b = Encoding.UTF8.GetBytes(s);
        if (b.Length <= maxBytes) return b;
        int n = maxBytes;
        while (n > 0 && (b[n] & 0xC0) == 0x80) n--; // don't cut a multi-byte character
        return b[..n];
    }

    private static byte[] IdentityMatrix() => Concat(U32(0x00010000), U32(0), U32(0), U32(0), U32(0x00010000), U32(0), U32(0), U32(0), U32(0x40000000));

    private static byte[] Box(string type, byte[] payload, bool latin1Type = false)
    {
        var box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        var t = latin1Type ? Encoding.Latin1.GetBytes(type) : Encoding.ASCII.GetBytes(type);
        t.CopyTo(box, 4);
        payload.CopyTo(box, 8);
        return box;
    }

    private static byte[] FullBox(string type, byte version, uint flags, byte[] payload) =>
        Box(type, Concat(new[] { version, (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags }, payload));

    private static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
    private static byte[] U16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); return b; }
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static byte[] Concat(params byte[][] parts)
    {
        var r = new byte[parts.Sum(p => p.Length)];
        int o = 0;
        foreach (var p in parts) { p.CopyTo(r, o); o += p.Length; }
        return r;
    }
}
