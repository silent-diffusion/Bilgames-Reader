using System.Diagnostics;
using System.Threading.Channels;
using Bilgames.Core.Audio;
using Bilgames.Core.Model;
using Bilgames.Core.Planning;
using Bilgames.Core.Storage;
using Bilgames.Core.Tts;

namespace Bilgames.Core.Jobs;

public enum ChapterStatus
{
    Queued,
    Generating,
    Ready,
    /// <summary>Audio exists but speakers or voices changed since; only changed segments regenerate.</summary>
    Changed,
    Failed,
}

public sealed class ChapterProgress
{
    public int Index { get; init; }
    public string Title { get; init; } = "";
    public ChapterStatus Status { get; set; }
    public double Fraction { get; set; }
}

public sealed class GenerationProgress
{
    public string BookId { get; init; } = "";
    public JobState State { get; set; }
    public int DoneSegments { get; set; }
    public int TotalSegments { get; set; }
    public List<ChapterProgress> Chapters { get; init; } = new();
    public TimeSpan? Remaining { get; set; }
    public bool Throttled { get; set; }
    public string? Error { get; set; }
    public long AudioBytes { get; set; }

    public double Fraction => TotalSegments == 0 ? 0 : (double)DoneSegments / TotalSegments;
    public int ReadyChapters => Chapters.Count(c => c.Status == ChapterStatus.Ready);
    public ChapterProgress? Current => Chapters.FirstOrDefault(c => c.Status == ChapterStatus.Generating);
}

/// <summary>
/// Runs one book's synthesis at a time on a background thread. Work is idempotent: every segment
/// is content-addressed and written atomically, so closing the app mid-way loses at most the
/// segment in flight, and a restart picks up where it left off.
/// </summary>
public sealed class GenerationCoordinator : IDisposable
{
    private readonly LibraryStore _store;
    private readonly ITtsEngine _engine;
    private readonly IAudioEncoder _encoder;
    private readonly IPowerMonitor _power;
    private readonly Func<AppSettings> _settings;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _current;
    private string? _currentBook;
    private readonly HashSet<string> _queued = new();
    private readonly Task _worker;

    // Rolling synthesis speed, seconds of compute per character, for time-left estimates.
    private double _secPerChar = 0.012;

    public event Action<GenerationProgress>? ProgressChanged;

    public GenerationCoordinator(LibraryStore store, ITtsEngine engine, IAudioEncoder encoder, IPowerMonitor power, Func<AppSettings> settings)
    {
        _store = store;
        _engine = engine;
        _encoder = encoder;
        _power = power;
        _settings = settings;
        _worker = Task.Run(WorkLoop);
    }

    public string? ActiveBookId => _currentBook;

    /// <summary>Re-queues jobs that were running or queued when the app last closed.</summary>
    public void ResumeInterrupted()
    {
        foreach (var b in _store.LoadAll().Where(b => b.Generation.State is JobState.Running or JobState.Queued).OrderBy(b => b.Generation.StartedUtc))
            Enqueue(b.Id);
    }

    public void Start(string bookId)
    {
        var book = _store.LoadBook(bookId);
        if (book is null) return;
        book.Generation = new GenerationState { State = JobState.Queued, StartedUtc = DateTime.UtcNow };
        _store.SaveBook(book);
        Enqueue(bookId);
        Report(Snapshot(book, JobState.Queued));
    }

    public void Pause(string bookId)
    {
        lock (_gate)
        {
            _queued.Remove(bookId);
            if (_currentBook == bookId) _current?.Cancel();
        }
        var book = _store.LoadBook(bookId);
        if (book is null) return;
        book.Generation.State = JobState.Paused;
        _store.SaveBook(book);
        Report(Snapshot(book, JobState.Paused));
    }

    private void Enqueue(string bookId)
    {
        lock (_gate)
        {
            if (!_queued.Add(bookId)) return;
        }
        _queue.Writer.TryWrite(bookId);
    }

    /// <summary>Computes per-chapter status without generating anything (for the Generate tab).</summary>
    public GenerationProgress Snapshot(BookRecord book, JobState? state = null)
    {
        var cast = _store.LoadCast(book.Id);
        var modelId = _engine.ModelIdFor(book.Quality);
        var progress = new GenerationProgress { BookId = book.Id, State = state ?? book.Generation.State, Error = book.Generation.Error };
        foreach (var info in book.IncludedChapters)
        {
            var plan = PlanFor(book, info.Index, cast, modelId);
            var audio = _store.LoadChapterAudio(book.Id, info.Index);
            int cached = plan.Segments.Count(s => File.Exists(_store.SegmentPath(book.Id, s.Hash)));
            var status = audio is null ? ChapterStatus.Queued : audio.PlanHash == plan.Hash ? ChapterStatus.Ready : ChapterStatus.Changed;
            progress.TotalSegments += plan.Segments.Count;
            progress.DoneSegments += status == ChapterStatus.Ready ? plan.Segments.Count : cached;
            progress.Chapters.Add(new ChapterProgress
            {
                Index = info.Index,
                Title = info.Title,
                Status = status,
                Fraction = plan.Segments.Count == 0 ? 1 : status == ChapterStatus.Ready ? 1 : (double)cached / plan.Segments.Count,
            });
        }
        progress.Remaining = TimeSpan.FromSeconds(EstimateRemainingChars(progress) * _secPerChar);
        progress.AudioBytes = _store.SizeOf(book.Id, "audio") + _store.SizeOf(book.Id, "segments");
        return progress;
    }

    public ChapterPlan PlanFor(BookRecord book, int chapterIndex, CastSheet cast, string? modelId = null)
    {
        var text = _store.LoadChapterText(book.Id, chapterIndex);
        var analysis = _store.LoadAnalysis(book.Id, chapterIndex);
        return SynthesisPlanner.Plan(book, text, analysis, cast, modelId ?? _engine.ModelIdFor(book.Quality));
    }

    private async Task WorkLoop()
    {
        var ct = _shutdown.Token;
        try
        {
            while (await _queue.Reader.WaitToReadAsync(ct))
            {
                if (!_queue.Reader.TryRead(out var bookId)) continue;
                lock (_gate)
                {
                    if (!_queued.Contains(bookId)) continue; // paused while waiting
                    _currentBook = bookId;
                    _current = CancellationTokenSource.CreateLinkedTokenSource(ct);
                }
                try
                {
                    await RunBook(bookId, _current.Token);
                }
                catch (OperationCanceledException) { /* paused or shutting down; state already saved */ }
                finally
                {
                    lock (_gate)
                    {
                        _queued.Remove(bookId);
                        _currentBook = null;
                        _current?.Dispose();
                        _current = null;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunBook(string bookId, CancellationToken ct)
    {
        var book = _store.LoadBook(bookId);
        if (book is null) return;

        if (!_engine.IsInstalled(book.Quality))
        {
            Fail(book, "The voice model for this quality isn't installed yet.");
            return;
        }

        book.Generation.State = JobState.Running;
        book.Generation.Error = null;
        book.Generation.StartedUtc ??= DateTime.UtcNow;
        _store.SaveBook(book);

        var cast = _store.LoadCast(bookId);
        var modelId = _engine.ModelIdFor(book.Quality);
        var progress = Snapshot(book, JobState.Running);
        Report(progress);
        var segDir = Path.Combine(_store.BookDir(bookId), "segments");
        Directory.CreateDirectory(segDir);
        Directory.CreateDirectory(Path.Combine(_store.BookDir(bookId), "audio"));

        foreach (var chProgress in progress.Chapters)
        {
            if (chProgress.Status == ChapterStatus.Ready) continue;
            ct.ThrowIfCancellationRequested();
            var plan = PlanFor(book, chProgress.Index, cast, modelId);
            chProgress.Status = ChapterStatus.Generating;
            Report(progress);

            try
            {
                for (int i = 0; i < plan.Segments.Count; i++)
                {
                    var seg = plan.Segments[i];
                    var path = _store.SegmentPath(bookId, seg.Hash);
                    if (!File.Exists(path))
                    {
                        await SynthesizeSegment(seg, path, book.Quality, progress, ct);
                        progress.DoneSegments++;
                    }
                    chProgress.Fraction = (i + 1.0) / plan.Segments.Count;
                    if (i % 2 == 0 || i == plan.Segments.Count - 1) Report(progress);
                }

                await AssembleChapter(book, plan, ct);
                chProgress.Status = ChapterStatus.Ready;
                chProgress.Fraction = 1;
                if (!_settings().KeepSegmentCache) DeleteSegments(bookId, plan, progress);
                progress.AudioBytes = _store.SizeOf(bookId, "audio") + _store.SizeOf(bookId, "segments");
                Report(progress);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                chProgress.Status = ChapterStatus.Failed;
                progress.Error = $"Chapter “{chProgress.Title}” failed: {ex.Message}";
                Report(progress);
                // Keep going with the rest of the book; the failed chapter can be retried.
            }
        }

        book = _store.LoadBook(bookId) ?? book;
        bool anyFailed = progress.Chapters.Any(c => c.Status == ChapterStatus.Failed);
        book.Generation.State = anyFailed ? JobState.Failed : JobState.Done;
        book.Generation.Error = anyFailed ? progress.Error : null;
        _store.SaveBook(book);
        progress.State = book.Generation.State;
        progress.Remaining = TimeSpan.Zero;
        Report(progress);
    }

    private async Task SynthesizeSegment(PlannedSegment seg, string path, QualityProfile profile, GenerationProgress progress, CancellationToken ct)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var sw = Stopwatch.StartNew();
                var pcm = await _engine.SynthesizeAsync(seg.Text, seg.VoiceId, seg.Speed, profile, ct);
                if (pcm.Length == 0) throw new InvalidOperationException("The voice model returned no audio.");
                var tmp = path + ".part";
                Wav.Write(tmp, pcm, _engine.SampleRate);
                File.Move(tmp, path, overwrite: true);
                sw.Stop();

                _secPerChar = 0.85 * _secPerChar + 0.15 * (sw.Elapsed.TotalSeconds / Math.Max(20, seg.Text.Length));
                progress.Remaining = TimeSpan.FromSeconds(_secPerChar * EstimateRemainingChars(progress));

                // On battery (or energy saver), work at half duty so the device stays cool and lasts longer.
                progress.Throttled = _power.OnBattery || _power.EnergySaver;
                if (progress.Throttled) await Task.Delay(sw.Elapsed, ct);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(500 * (attempt + 1), ct);
            }
        }
        throw new InvalidOperationException($"Couldn't synthesize a segment after 3 tries ({last?.Message}).", last);
    }

    private static double EstimateRemainingChars(GenerationProgress p)
    {
        // ~140 characters per segment on average.
        return Math.Max(0, p.TotalSegments - p.DoneSegments) * 140.0;
    }

    private async Task AssembleChapter(BookRecord book, ChapterPlan plan, CancellationToken ct)
    {
        var tmpWav = Path.Combine(_store.TempDir, $"{book.Id}-{plan.ChapterIndex}.wav");
        var final = _store.ChapterAudioPath(book.Id, plan.ChapterIndex);
        var tmpOut = final + ".part.m4a";
        var timings = new List<SegmentTiming>();
        try
        {
            using (var cat = new WavConcatenator(tmpWav, _engine.SampleRate))
            {
                cat.AppendSilence(250);
                foreach (var seg in plan.Segments)
                {
                    ct.ThrowIfCancellationRequested();
                    double start = cat.PositionSeconds;
                    cat.AppendFile(_store.SegmentPath(book.Id, seg.Hash));
                    timings.Add(new SegmentTiming { Start = start, End = cat.PositionSeconds, BlockId = seg.BlockId, LineId = seg.LineId, SpeakerId = seg.SpeakerId });
                    cat.AppendSilence(seg.PauseAfterMs);
                }
                cat.AppendSilence(600);
            }
            double duration = Wav.DurationSeconds(tmpWav);
            await _encoder.EncodeAacAsync(tmpWav, tmpOut, ct);
            File.Move(tmpOut, final, overwrite: true);
            _store.SaveChapterAudio(book.Id, new ChapterAudio
            {
                ChapterIndex = plan.ChapterIndex,
                PlanHash = plan.Hash,
                FileName = Path.GetFileName(final),
                DurationSeconds = duration,
                Timings = timings,
                CreatedUtc = DateTime.UtcNow,
            });
        }
        finally
        {
            TryDelete(tmpWav);
            TryDelete(tmpOut);
        }
    }

    private void DeleteSegments(string bookId, ChapterPlan plan, GenerationProgress progress)
    {
        foreach (var s in plan.Segments) TryDelete(_store.SegmentPath(bookId, s.Hash));
    }

    private void Fail(BookRecord book, string message)
    {
        book.Generation.State = JobState.Failed;
        book.Generation.Error = message;
        _store.SaveBook(book);
        var p = Snapshot(book, JobState.Failed);
        p.Error = message;
        Report(p);
    }

    private void Report(GenerationProgress p)
    {
        try { ProgressChanged?.Invoke(p); } catch { /* UI handlers must not kill the worker */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _queue.Writer.TryComplete();
        try { _worker.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        _shutdown.Dispose();
    }
}
