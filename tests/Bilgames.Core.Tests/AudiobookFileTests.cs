using System.Buffers.Binary;
using System.Text;
using Bilgames.Core.Audio;

namespace Bilgames.Core.Tests;

public sealed class AudiobookFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bilgames-m4b-" + Guid.NewGuid().ToString("N"));
    public AudiobookFileTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static readonly byte[] Audio = Encoding.ASCII.GetBytes("AUDIO-FRAME-0001AUDIO-FRAME-0002");
    private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 0xFF, 0xD9 };

    /// <summary>A minimal .m4a laid out like Windows' encoder writes it: ftyp, uuid, mdat, then moov.</summary>
    private string MinimalM4a()
    {
        var mdat = Box("mdat", Audio);
        long mdatOffset = 24 + 16;
        var stbl = Box("stbl", Concat(
            Full("stsd", U32(0)), Full("stts", Concat(U32(1), U32(2), U32(1024))), Full("stsc", Concat(U32(1), U32(1), U32(2), U32(1))),
            Full("stsz", Concat(U32(0), U32(2), U32(16), U32(16))), Full("stco", Concat(U32(1), U32((uint)(mdatOffset + 8))))));
        var minf = Box("minf", Concat(Full("smhd", new byte[4]), stbl));
        var mdia = Box("mdia", Concat(Full("mdhd", Concat(U32(0), U32(0), U32(44100), U32(441000), U32(0))),
            Full("hdlr", Concat(U32(0), Encoding.ASCII.GetBytes("soun"), new byte[13])), minf));
        var tkhd = Full("tkhd", Concat(U32(0), U32(0), U32(1), U32(0), U32(441000), new byte[60]));
        var mvhd = Full("mvhd", Concat(U32(0), U32(0), U32(44100), U32(441000), new byte[76], U32(2)));
        var moov = Box("moov", Concat(mvhd, Box("trak", Concat(tkhd, mdia)), Box("udta", Box("Xtra", Array.Empty<byte>()))));
        var file = Concat(Box("ftyp", Encoding.ASCII.GetBytes("mp42\0\0\0\0mp41isom")), Box("uuid", new byte[8]), mdat, moov);
        var path = Path.Combine(_dir, "book.m4a");
        File.WriteAllBytes(path, file);
        return path;
    }

    [Fact]
    public void WritesAProperAudiobook()
    {
        var path = MinimalM4a();
        var chapters = new[] { new Mp4Chapter(TimeSpan.Zero, "Prologue"), new Mp4Chapter(TimeSpan.FromSeconds(4), "Chapter 1 — Ünïcode") };
        Mp4Metadata.Write(path, new AudiobookInfo("A Book", "An Author", chapters, Jpeg));

        Assert.Equal("M4B ", Mp4Metadata.ReadBrand(path));
        Assert.Equal(chapters, Mp4Metadata.ReadChapterTrack(path));
        Assert.Equal(chapters, Mp4Metadata.ReadChapters(path));
        var tags = Mp4Metadata.ReadTags(path);
        Assert.Equal("A Book", tags["©nam"]);
        Assert.Equal("A Book", tags["©alb"]);
        Assert.Equal("An Author", tags["©ART"]);
        Assert.Equal("An Author", tags["aART"]);
        Assert.Equal("02", tags["stik"]);
        Assert.Equal($"image/jpeg ({Jpeg.Length} bytes)", tags["covr"]);
        // The audio's chunk offset still points at the audio after the file was rearranged.
        Assert.Equal(Audio, AudioAtFirstChunk(path));
    }

    [Fact]
    public void RetaggingReplacesRatherThanStacks()
    {
        var path = MinimalM4a();
        var chapters = new[] { new Mp4Chapter(TimeSpan.Zero, "One"), new Mp4Chapter(TimeSpan.FromSeconds(5), "Two") };
        Mp4Metadata.Write(path, new AudiobookInfo("A Book", "An Author", chapters));
        var size = new FileInfo(path).Length;
        Mp4Metadata.Write(path, new AudiobookInfo("A Book", "An Author", chapters));

        Assert.Equal(chapters, Mp4Metadata.ReadChapterTrack(path));
        Assert.Equal(Audio, AudioAtFirstChunk(path));
        Assert.True(new FileInfo(path).Length <= size + 8); // one chapter track, not two
    }

    private static byte[] AudioAtFirstChunk(string path)
    {
        var file = File.ReadAllBytes(path);
        int stco = IndexOf(file, Encoding.ASCII.GetBytes("stco")); // the audio track comes first
        uint offset = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(stco + 12));
        return file[(int)offset..((int)offset + Audio.Length)];
    }

    private static int IndexOf(byte[] data, byte[] pattern)
    {
        for (int i = 0; i <= data.Length - pattern.Length; i++)
            if (data.AsSpan(i, pattern.Length).SequenceEqual(pattern)) return i;
        return -1;
    }

    private static byte[] Box(string type, byte[] payload)
    {
        var box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        payload.CopyTo(box, 8);
        return box;
    }

    private static byte[] Full(string type, byte[] payload) => Box(type, Concat(new byte[4], payload));
    private static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
