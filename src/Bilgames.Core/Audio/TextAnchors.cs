using Bilgames.Core.Model;

namespace Bilgames.Core.Audio;

/// <summary>A place in a chapter's text: a line, and how far through it (0 = its start, 1 = its end).</summary>
public readonly record struct TextAnchor(string LineId, double Fraction);

/// <summary>
/// Converts between a moment in a chapter's audio and a place in its text. Positions and bookmarks
/// are saved as text anchors, because any change that re-times the audio (another quality, voice
/// or device) moves the seconds but not the words; seconds are only a cache.
/// </summary>
public static class TextAnchors
{
    /// <summary>The line being read at <paramref name="seconds"/>, or null when the timings don't cover it.</summary>
    public static TextAnchor? At(IReadOnlyList<SegmentTiming> timings, double seconds)
    {
        if (timings.Count == 0) return null;
        SegmentTiming? hit = null;
        foreach (var t in timings)
        {
            if (t.Start > seconds) break;
            hit = t; // the last segment starting at or before the moment (pauses belong to the line before)
        }
        hit ??= timings[0];
        if (string.IsNullOrEmpty(hit.LineId)) return null;
        var (start, end) = Span(timings, hit.LineId);
        var fraction = end > start ? Math.Clamp((seconds - start) / (end - start), 0, 1) : 0;
        return new TextAnchor(hit.LineId, Math.Round(fraction, 4));
    }

    /// <summary>Where a text anchor falls in these timings, or null when the line isn't in them.</summary>
    public static double? SecondsOf(IReadOnlyList<SegmentTiming> timings, string? lineId, double fraction)
    {
        if (string.IsNullOrEmpty(lineId)) return null;
        var (start, end) = Span(timings, lineId);
        if (start < 0) return null;
        return start + (end - start) * Math.Clamp(fraction, 0, 1);
    }

    /// <summary>A line can be spoken in several segments; its span runs from the first start to the last end.</summary>
    private static (double Start, double End) Span(IReadOnlyList<SegmentTiming> timings, string lineId)
    {
        double start = -1, end = -1;
        foreach (var t in timings)
        {
            if (t.LineId != lineId) continue;
            if (start < 0 || t.Start < start) start = t.Start;
            if (t.End > end) end = t.End;
        }
        return (start, end);
    }
}
