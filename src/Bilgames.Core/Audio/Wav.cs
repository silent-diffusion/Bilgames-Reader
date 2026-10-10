namespace Bilgames.Core.Audio;

/// <summary>Minimal 16-bit PCM mono WAV reading and writing; streamed so whole books never sit in memory.</summary>
public static class Wav
{
    public const int HeaderSize = 44;

    public static void Write(string path, ReadOnlySpan<short> samples, int sampleRate)
    {
        using var fs = File.Create(path);
        WriteHeader(fs, sampleRate, samples.Length * 2L);
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples);
        fs.Write(bytes);
    }

    public static void WriteHeader(Stream s, int sampleRate, long dataBytes)
    {
        using var w = new BinaryWriter(s, System.Text.Encoding.ASCII, leaveOpen: true);
        uint data = (uint)Math.Min(dataBytes, uint.MaxValue - 36);
        w.Write("RIFF"u8);
        w.Write(36 + data);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);          // PCM
        w.Write((short)1);          // mono
        w.Write(sampleRate);
        w.Write(sampleRate * 2);    // byte rate
        w.Write((short)2);          // block align
        w.Write((short)16);         // bits per sample
        w.Write("data"u8);
        w.Write(data);
    }

    /// <summary>Finds the PCM data chunk; returns (offset, length, sampleRate).</summary>
    public static (long Offset, long Length, int SampleRate, int Channels, int Bits) Probe(Stream s)
    {
        using var r = new BinaryReader(s, System.Text.Encoding.ASCII, leaveOpen: true);
        s.Position = 0;
        if (new string(r.ReadChars(4)) != "RIFF") throw new InvalidDataException("Not a WAV file.");
        r.ReadInt32();
        if (new string(r.ReadChars(4)) != "WAVE") throw new InvalidDataException("Not a WAV file.");
        int rate = 0, channels = 0, bits = 0;
        while (s.Position + 8 <= s.Length)
        {
            var id = new string(r.ReadChars(4));
            uint size = r.ReadUInt32();
            if (id == "fmt ")
            {
                long start = s.Position;
                r.ReadInt16();
                channels = r.ReadInt16();
                rate = r.ReadInt32();
                r.ReadInt32();
                r.ReadInt16();
                bits = r.ReadInt16();
                s.Position = start + size;
            }
            else if (id == "data")
            {
                long len = size == 0 || s.Position + size > s.Length ? s.Length - s.Position : size;
                return (s.Position, len, rate, channels, bits);
            }
            else s.Position += size + (size & 1);
        }
        throw new InvalidDataException("WAV file has no audio data.");
    }

    public static double DurationSeconds(string path)
    {
        using var fs = File.OpenRead(path);
        var (_, len, rate, ch, bits) = Probe(fs);
        return rate == 0 ? 0 : len / (double)(rate * ch * (bits / 8));
    }
}

/// <summary>Appends PCM from segment files plus silences into one WAV, fixing the header at the end.</summary>
public sealed class WavConcatenator : IDisposable
{
    private readonly FileStream _out;
    private readonly int _sampleRate;
    private long _dataBytes;

    public WavConcatenator(string path, int sampleRate)
    {
        _sampleRate = sampleRate;
        _out = File.Create(path);
        Wav.WriteHeader(_out, sampleRate, 0);
    }

    public double PositionSeconds => _dataBytes / 2.0 / _sampleRate;

    public void AppendFile(string wavPath)
    {
        using var fs = File.OpenRead(wavPath);
        var (offset, length, rate, channels, bits) = Wav.Probe(fs);
        if (rate != _sampleRate || channels != 1 || bits != 16)
            throw new InvalidDataException($"Segment {Path.GetFileName(wavPath)} has an unexpected format ({rate} Hz, {channels} ch, {bits} bit).");
        fs.Position = offset;
        var buffer = new byte[81920];
        long remaining = length & ~1L;
        while (remaining > 0)
        {
            int n = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (n <= 0) break;
            _out.Write(buffer, 0, n);
            remaining -= n;
            _dataBytes += n;
        }
    }

    public void AppendSilence(int milliseconds)
    {
        long bytes = (long)_sampleRate * milliseconds / 1000 * 2;
        var zero = new byte[Math.Min(bytes, 81920)];
        while (bytes > 0)
        {
            int n = (int)Math.Min(zero.Length, bytes);
            _out.Write(zero, 0, n);
            bytes -= n;
            _dataBytes += n;
        }
    }

    public void Dispose()
    {
        _out.Position = 0;
        Wav.WriteHeader(_out, _sampleRate, _dataBytes);
        _out.Dispose();
    }
}
