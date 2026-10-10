using Bilgames.Core.Model;
using Bilgames.Core.Planning;
using Bilgames.Core.Storage;

namespace Bilgames.Core.Audio;

public sealed class StreamChunk
{
    public int Index { get; init; }
    public string WavPath { get; init; } = "";
    /// <summary>Start of this chunk on the chapter timeline, in seconds.</summary>
    public double Start { get; init; }
    public double Duration { get; init; }
    public List<SegmentTiming> Timings { get; init; } = new();
}

/// <summary>
/// Lets a chapter be heard while it is still being generated. As segments land in the cache, they
/// are packed — a few paragraphs at a time — into small WAV chunks laid out on exactly the same
/// timeline the finished chapter file will have (same lead-in, same pauses), so the player can
/// switch to the final audio at the same position without a jump.
/// </summary>
public sealed class ChapterStream : IDisposable
{
    private readonly LibraryStore _store;
    private readonly string _bookId;
    private readonly ChapterPlan _plan;
    private readonly int _sampleRate;
    private readonly string _dir;
    private int _next;
    private double _cursor;

    public ChapterStream(LibraryStore store, string bookId, ChapterPlan plan, int sampleRate)
    {
        _store = store;
        _bookId = bookId;
        _plan = plan;
        _sampleRate = sampleRate;
        _dir = Path.Combine(store.TempDir, $"stream-{bookId}-{plan.ChapterIndex}-{Guid.NewGuid().ToString("N")[..12]}");
        Directory.CreateDirectory(_dir);
    }

    public int ChapterIndex => _plan.ChapterIndex;
    public ChapterPlan Plan => _plan;
    public List<StreamChunk> Chunks { get; } = new();
    public bool Complete => _next >= _plan.Segments.Count;
    public double BufferedSeconds => _cursor;

    /// <summary>Fraction of the chapter's segments already playable.</summary>
    public double Fraction => _plan.Segments.Count == 0 ? 1 : (double)_next / _plan.Segments.Count;

    /// <summary>
    /// Packs the next run of ready segments into a chunk. Waits until a paragraph boundary (or the
    /// chapter end) so chunks don't cut mid-paragraph, unless enough audio has piled up anyway.
    /// </summary>
    public StreamChunk? TryBuildNext(double minSeconds = 12)
    {
        if (Complete) return null;
        int end = _next;
        double estimate = 0;
        int lastBoundary = -1;
        while (end < _plan.Segments.Count && File.Exists(_store.SegmentPath(_bookId, _plan.Segments[end].Hash)))
        {
            estimate += _plan.Segments[end].Text.Length * 0.065;
            bool boundary = end + 1 >= _plan.Segments.Count || _plan.Segments[end + 1].BlockId != _plan.Segments[end].BlockId;
            if (boundary) lastBoundary = end;
            end++;
            if (estimate >= minSeconds * 3 && lastBoundary >= _next) break;
        }
        bool reachedEnd = end >= _plan.Segments.Count;
        int take;
        if (end == _next) return null;
        if (reachedEnd) take = end;
        // Early on, start with whatever is ready (segments are whole sentences) so listening begins at once.
        else if (Chunks.Count < 3) take = end;
        // Later, prefer ending a chunk on a paragraph; split a long paragraph at a sentence if needed.
        else if (lastBoundary >= _next && estimate >= minSeconds) take = lastBoundary + 1;
        else if (estimate >= minSeconds) take = end;
        else return null;

        var path = Path.Combine(_dir, $"{Chunks.Count}.wav");
        var timings = new List<SegmentTiming>();
        double start = _cursor;
        using (var cat = new WavConcatenator(path, _sampleRate))
        {
            if (_next == 0) cat.AppendSilence(250);
            for (int i = _next; i < take; i++)
            {
                var seg = _plan.Segments[i];
                double s = start + cat.PositionSeconds;
                cat.AppendFile(_store.SegmentPath(_bookId, seg.Hash));
                timings.Add(new SegmentTiming { Start = s, End = start + cat.PositionSeconds, BlockId = seg.BlockId, LineId = seg.LineId, SpeakerId = seg.SpeakerId });
                cat.AppendSilence(seg.PauseAfterMs);
            }
            if (take >= _plan.Segments.Count) cat.AppendSilence(600);
            _cursor = start + cat.PositionSeconds;
        }
        _next = take;
        var chunk = new StreamChunk { Index = Chunks.Count, WavPath = path, Start = start, Duration = _cursor - start, Timings = timings };
        Chunks.Add(chunk);
        return chunk;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
