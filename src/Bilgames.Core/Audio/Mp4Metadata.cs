using System.Buffers.Binary;
using System.Text;

namespace Bilgames.Core.Audio;

public sealed record Mp4Chapter(TimeSpan Start, string Title);

/// <summary>
/// Adds audiobook chapter markers (Nero "chpl") and iTunes-style title/author tags to an MPEG-4
/// audio file in place of its existing moov box. If the moov box precedes the media data, every
/// chunk offset is shifted so the file stays valid.
/// </summary>
public static class Mp4Metadata
{
    public static void Write(string path, IReadOnlyList<Mp4Chapter> chapters, string title, string author)
    {
        var file = File.ReadAllBytes(path);
        var top = ReadBoxes(file, 0, file.Length);
        var moov = top.FirstOrDefault(b => b.Type == "moov");
        if (moov.Size == 0) throw new InvalidDataException("The audio file has no moov box.");
        var mdat = top.FirstOrDefault(b => b.Type == "mdat");

        // Rebuild moov: keep everything except an old udta, then append a fresh one.
        var children = ReadBoxes(file, moov.Offset + moov.HeaderSize, moov.Offset + moov.Size);
        using var body = new MemoryStream();
        foreach (var c in children.Where(c => c.Type != "udta"))
            body.Write(file, (int)c.Offset, (int)c.Size);
        body.Write(BuildUdta(chapters, title, author));
        var newMoov = Box("moov", body.ToArray());

        long delta = newMoov.Length - moov.Size;
        if (mdat.Size > 0 && moov.Offset < mdat.Offset && delta != 0)
            ShiftChunkOffsets(newMoov, 8, newMoov.Length, delta);

        using var output = new MemoryStream(file.Length + newMoov.Length);
        output.Write(file, 0, (int)moov.Offset);
        output.Write(newMoov);
        output.Write(file, (int)(moov.Offset + moov.Size), (int)(file.Length - moov.Offset - moov.Size));
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, output.ToArray());
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Reads back the chapter list (used by tests and by the export check).</summary>
    public static List<Mp4Chapter> ReadChapters(string path)
    {
        var file = File.ReadAllBytes(path);
        var result = new List<Mp4Chapter>();
        var moov = ReadBoxes(file, 0, file.Length).FirstOrDefault(b => b.Type == "moov");
        if (moov.Size == 0) return result;
        var udta = ReadBoxes(file, moov.Offset + moov.HeaderSize, moov.Offset + moov.Size).FirstOrDefault(b => b.Type == "udta");
        if (udta.Size == 0) return result;
        var chpl = ReadBoxes(file, udta.Offset + udta.HeaderSize, udta.Offset + udta.Size).FirstOrDefault(b => b.Type == "chpl");
        if (chpl.Size == 0) return result;
        long p = chpl.Offset + chpl.HeaderSize + 8;
        int count = file[p++];
        for (int i = 0; i < count; i++)
        {
            long start = BinaryPrimitives.ReadInt64BigEndian(file.AsSpan((int)p));
            p += 8;
            int len = file[p++];
            var t = Encoding.UTF8.GetString(file, (int)p, len);
            p += len;
            result.Add(new Mp4Chapter(TimeSpan.FromTicks(start), t));
        }
        return result;
    }

    private readonly record struct BoxRef(string Type, long Offset, long Size, int HeaderSize);

    private static List<BoxRef> ReadBoxes(byte[] data, long start, long end)
    {
        var list = new List<BoxRef>();
        long p = start;
        while (p + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)p));
            var type = Encoding.ASCII.GetString(data, (int)p + 4, 4);
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

    private static void ShiftChunkOffsets(byte[] moov, long start, long end, long delta)
    {
        foreach (var b in ReadBoxes(moov, start, end))
        {
            if (Containers.Contains(b.Type))
            {
                ShiftChunkOffsets(moov, b.Offset + b.HeaderSize, b.Offset + b.Size, delta);
            }
            else if (b.Type == "stco")
            {
                int p = (int)(b.Offset + b.HeaderSize + 4);
                uint n = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(p));
                p += 4;
                for (uint i = 0; i < n; i++, p += 4)
                    BinaryPrimitives.WriteUInt32BigEndian(moov.AsSpan(p), (uint)(BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(p)) + delta));
            }
            else if (b.Type == "co64")
            {
                int p = (int)(b.Offset + b.HeaderSize + 4);
                uint n = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(p));
                p += 4;
                for (uint i = 0; i < n; i++, p += 8)
                    BinaryPrimitives.WriteUInt64BigEndian(moov.AsSpan(p), (ulong)((long)BinaryPrimitives.ReadUInt64BigEndian(moov.AsSpan(p)) + delta));
            }
        }
    }

    private static byte[] BuildUdta(IReadOnlyList<Mp4Chapter> chapters, string title, string author)
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
        ilst.Write(Tag("©nam", title));
        ilst.Write(Tag("©alb", title));
        if (author.Length > 0) ilst.Write(Tag("©ART", author));
        ilst.Write(Tag("©gen", "Audiobook"));
        ilst.Write(Tag("©too", "Bilgames Reader"));

        var hdlr = Box("hdlr", Concat(new byte[8], "mdir"u8.ToArray(), "appl"u8.ToArray(), new byte[9]));
        var meta = Box("meta", Concat(new byte[4], hdlr, Box("ilst", ilst.ToArray())));

        return Box("udta", Concat(Box("chpl", chpl.ToArray()), meta));
    }

    private static byte[] Tag(string name, string value)
    {
        var data = Box("data", Concat(new byte[] { 0, 0, 0, 1, 0, 0, 0, 0 }, Encoding.UTF8.GetBytes(value)));
        return Box(name, data, latin1Type: true);
    }

    private static byte[] Truncate(string s, int maxBytes)
    {
        var b = Encoding.UTF8.GetBytes(s);
        if (b.Length <= maxBytes) return b;
        int n = maxBytes;
        while (n > 0 && (b[n] & 0xC0) == 0x80) n--; // don't cut a multi-byte character
        return b[..n];
    }

    private static byte[] Box(string type, byte[] payload, bool latin1Type = false)
    {
        var box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        var t = latin1Type ? Encoding.Latin1.GetBytes(type) : Encoding.ASCII.GetBytes(type);
        t.CopyTo(box, 4);
        payload.CopyTo(box, 8);
        return box;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var r = new byte[parts.Sum(p => p.Length)];
        int o = 0;
        foreach (var p in parts) { p.CopyTo(r, o); o += p.Length; }
        return r;
    }
}
