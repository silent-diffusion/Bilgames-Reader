using System.Collections.ObjectModel;
using Bilgames.App.Controls;
using Bilgames.App.Services;
using Bilgames.Core.Audio;
using Bilgames.Core.Model;
using Bilgames.Core.Planning;
using Bilgames.Core.Tts;
using Bilgames.Tts.Kokoro;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Bilgames.App.ViewModels;

public sealed class PlayerChapterViewModel
{
    public int Index { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public bool IsReady { get; init; }
    public bool IsCurrent { get; init; }
}

/// <summary>One paragraph of the scrollable transcript.</summary>
public sealed partial class TranscriptParagraph : ObservableObject
{
    public string BlockId { get; init; } = "";
    [ObservableProperty] private IReadOnlyList<IReadOnlyList<ContextRun>> _runs = Array.Empty<IReadOnlyList<ContextRun>>();
    [ObservableProperty] private bool _isCurrent;
    /// <summary>False while this paragraph hasn't been generated yet (streaming).</summary>
    [ObservableProperty] private bool _isAvailable = true;
}

/// <summary>
/// Plays a book chapter by chapter. A finished chapter plays from its AAC file; a chapter that is
/// still generating plays as a stream of small chunks (see <see cref="ChapterStream"/>) so listening
/// can start right away, and switches to the finished file at the same spot once it is saved.
/// </summary>
public sealed partial class PlayerViewModel : ObservableObject
{
    private static readonly double[] Speeds = { 0.8, 1.0, 1.25, 1.5, 2.0 };
    private static readonly string[] SpeedLabels = { "0.8×", "1.0×", "1.25×", "1.5×", "2.0×" };

    private readonly ShellViewModel _shell;
    private readonly MediaPlayer _player = new() { AudioCategory = MediaPlayerAudioCategory.Media };
    private readonly MediaPlayer _preview = new() { AudioCategory = MediaPlayerAudioCategory.Media };
    private readonly DispatcherQueueTimer _tick;
    private BookRecord? _book;
    private CastSheet _cast = new();
    private List<int> _ready = new();
    private int _chapter = -1;
    private DateTime _lastSave;
    private bool _sleepAtChapterEnd;
    private DateTime? _sleepUntil;
    private double _pendingSeek = -1;
    private bool _autoplayOnOpen;
    private bool _restoringSpeed;
    private bool _dragging;

    // What is playing: the finished chapter, or a live stream of it.
    private ChapterAudio? _audio;
    private ChapterStream? _stream;
    private MediaPlaybackList? _list;
    private int _streamItem;
    private bool _starved;

    // Transcript state shared by both modes.
    private List<SegmentTiming> _timings = new();
    private readonly Dictionary<string, double> _lineStart = new();
    private readonly Dictionary<string, int> _paragraphIndex = new();
    private int _timingIndex = -1;

    public PlayerViewModel(ShellViewModel shell)
    {
        _shell = shell;
        _tick = AppServices.UI.CreateTimer();
        _tick.Interval = TimeSpan.FromMilliseconds(250);
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();

        _player.MediaEnded += (_, _) => AppServices.RunOnUI(OnEnded);
        _player.MediaOpened += (_, _) => AppServices.RunOnUI(OnOpened);
        _player.PlaybackSession.PlaybackStateChanged += (_, _) => AppServices.RunOnUI(SyncPlaying);
        _player.CommandManager.NextBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        _player.CommandManager.PreviousBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        _player.CommandManager.NextReceived += (_, e) => { e.Handled = true; AppServices.RunOnUI(() => Step(+1)); };
        _player.CommandManager.PreviousReceived += (_, e) => { e.Handled = true; AppServices.RunOnUI(() => Step(-1)); };
    }

    public string? BookId => _book?.Id;
    public ObservableCollection<PlayerChapterViewModel> Chapters { get; } = new();
    public ObservableCollection<TranscriptParagraph> Transcript { get; } = new();

    [ObservableProperty] private bool _hasBook;
    [ObservableProperty] private bool _hasAudio;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _author = "";
    [ObservableProperty] private string _coverColor = "#2B4A5E";
    [ObservableProperty] private string? _coverPath;
    [ObservableProperty] private string _chapterTitle = "";
    [ObservableProperty] private string _headerLabel = "NOW PLAYING";
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _position;
    [ObservableProperty] private double _duration = 1;
    [ObservableProperty] private string _elapsedText = "00:00";
    [ObservableProperty] private string _remainingText = "−00:00";
    [ObservableProperty] private int _speedIndex = 1;
    [ObservableProperty] private string _sleepLabel = "Sleep";
    [ObservableProperty] private int _currentParagraph = -1;
    [ObservableProperty] private string _speakerName = "Narrator";
    [ObservableProperty] private string _speakerLabel = "";
    [ObservableProperty] private string _speakerInitials = "N";
    [ObservableProperty] private string _speakerColor = "#C9CED6";
    [ObservableProperty] private string _miniSubtitle = "";
    [ObservableProperty] private string _emptyText = "Pick a book from your library to start listening.";
    [ObservableProperty] private bool _canStart;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private string _streamStatus = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ExportName))] private bool _isExporting;
    [ObservableProperty] private string _exportLabel = "Export";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ExportName))] private double _exportPercent;
    public string ExportName => IsExporting ? $"Exporting audiobook, {ExportPercent:0}% done. Select to stop." : "Export audiobook";
    private CancellationTokenSource? _exportCts;

    public string SpeedLabel => SpeedLabels[SpeedIndex];
    public string PlayLabel => IsPlaying ? "Pause" : "Play";
    public string PlayIcon => IsPlaying ? Icons.Pause : Icons.Play;
    public string SpeedName => $"Playback speed {Speeds[SpeedIndex]:0.0#} times. Change speed";

    partial void OnIsPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(PlayLabel));
        OnPropertyChanged(nameof(PlayIcon));
    }

    partial void OnSpeedIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SpeedLabel));
        OnPropertyChanged(nameof(SpeedName));
        _player.PlaybackSession.PlaybackRate = Speeds[value];
        if (_book is not null && !_restoringSpeed)
        {
            var b = AppServices.Store.LoadBook(_book.Id);
            if (b is not null) { b.PlaybackRate = Speeds[value]; AppServices.Store.SaveBook(b); }
        }
    }

    // ---------- loading ----------

    /// <summary>Loads a book. With <paramref name="autoplay"/>, starts listening even if nothing is generated yet.</summary>
    public void Load(string bookId, bool autoplay = false, (int Chapter, double Seconds)? start = null)
    {
        SavePosition();
        StopStream();
        _book = AppServices.Store.LoadBook(bookId);
        HasBook = _book is not null;
        if (_book is null) return;
        Title = _book.Title;
        Author = _book.Author;
        CoverColor = _book.CoverColor;
        CoverPath = AppServices.Store.CoverPath(_book);
        _coverRef = null;
        _ = LoadCoverAsync(_book);
        _cast = AppServices.Store.LoadCast(bookId);
        LoadNotes();
        int idx = Array.FindIndex(Speeds, s => Math.Abs(s - _book.PlaybackRate) < 0.01);
        _restoringSpeed = true; // restoring the saved speed shouldn't write it back
        SpeedIndex = idx < 0 ? 1 : idx;
        _restoringSpeed = false;
        RefreshReady();

        var pos = _book.Position;
        var included = _book.IncludedChapters.Select(c => c.Index).ToList();
        int chapter = _ready.Contains(pos.ChapterIndex) ? pos.ChapterIndex : _ready.FirstOrDefault(-1);
        // Opened from a bookmark or highlight: go straight there (plays as it's generated if not ready).
        if (start is { } at && _book.Chapters.Any(c => c.Index == at.Chapter)) { _ = OpenChapterAsync(at.Chapter, at.Seconds, autoplay); _shell.RaiseLayout(); return; }
        if (chapter < 0 && autoplay)
        {
            // Nothing finished yet: listen to the chapter being generated (or the first one).
            int generating = AppServices.Jobs.ActiveBookId == bookId && included.Contains(pos.ChapterIndex) ? pos.ChapterIndex : included.FirstOrDefault(-1);
            if (generating >= 0) { _ = OpenChapterAsync(generating, 0, true); _shell.RaiseLayout(); return; }
        }
        if (chapter < 0)
        {
            Clear();
            return;
        }
        _ = OpenChapterAsync(chapter, chapter == pos.ChapterIndex ? pos.Seconds : 0, autoplay);
        _shell.RaiseLayout();
    }

    public void Unload()
    {
        _player.Pause();
        StopStream();
        _player.Source = null;
        _book = null;
        HasBook = false;
        ShowNotes = false;
        LoadNotes();
        Clear();
        _shell.RaiseLayout();
    }

    private void Clear()
    {
        _player.Source = null;
        _audio = null;
        _chapter = -1;
        HasAudio = false;
        IsPlaying = false;
        IsStreaming = false;
        ChapterTitle = "";
        HeaderLabel = "NOW PLAYING";
        Transcript.Clear();
        CanStart = _book is not null && _book.IncludedChapters.Any();
        EmptyText = _book is null
            ? "Pick a book from your library to start listening."
            : "Nothing generated yet. Start listening and the first chapter plays as it's being made.";
        MiniSubtitle = _book is null ? "" : "No audio yet";
        Position = 0;
        Duration = 1;
        BuildChapterList();
    }

    private void RefreshReady()
    {
        _ready = _book is null ? new List<int>()
            : _book.IncludedChapters.Where(c => AppServices.Store.LoadChapterAudio(_book.Id, c.Index) is not null).Select(c => c.Index).ToList();
    }

    /// <summary>Chapters finished generating, or speakers/voices changed.</summary>
    public void OnChaptersChanged()
    {
        if (_book is null) return;
        _book = AppServices.Store.LoadBook(_book.Id) ?? _book;
        _cast = AppServices.Store.LoadCast(_book.Id);
        RefreshReady();
        if (_stream is not null && _ready.Contains(_stream.ChapterIndex))
        {
            // The chapter we're streaming has been saved: carry on from the same spot in the final file.
            bool playing = IsPlaying || _starved;
            _ = OpenChapterAsync(_stream.ChapterIndex, Position, playing);
        }
        else if (_audio is not null && _chapter >= 0)
        {
            // The current chapter may have been regenerated with corrected speakers.
            var fresh = AppServices.Store.LoadChapterAudio(_book.Id, _chapter);
            if (fresh is not null && fresh.PlanHash != _audio.PlanHash) _ = OpenChapterAsync(_chapter, Position, IsPlaying);
        }
        else if (!HasAudio && _ready.Count > 0 && _stream is null)
        {
            _ = OpenChapterAsync(_ready[0], 0, false);
        }
        BuildChapterList();
    }

    /// <summary>"Start listening" when nothing is generated yet.</summary>
    [RelayCommand]
    private async Task StartListeningAsync()
    {
        if (_book is null) return;
        int first = _book.IncludedChapters.Select(c => c.Index).FirstOrDefault(-1);
        if (first >= 0) await OpenChapterAsync(first, 0, true);
    }

    private async Task OpenChapterAsync(int index, double seconds, bool play)
    {
        if (_book is null) return;
        var audio = AppServices.Store.LoadChapterAudio(_book.Id, index);
        if (audio is not null) await OpenFinal(index, audio, seconds, play);
        else await OpenStream(index, play);
    }

    private async Task OpenFinal(int index, ChapterAudio audio, double seconds, bool play)
    {
        StopStream();
        _audio = audio;
        _chapter = index;
        HasAudio = true;
        IsStreaming = false;
        StreamStatus = "";
        SetChapterLabels(index);
        Duration = Math.Max(1, audio.DurationSeconds);
        BuildTranscript(index, audio.Timings.Select(t => t.BlockId).Distinct().ToList());
        SetTimings(audio.Timings);

        var item = new MediaPlaybackItem(MediaSource.CreateFromUri(new Uri(AppServices.Store.ChapterAudioPath(_book!.Id, audio))));
        ApplyDisplayProperties(item, index);
        _pendingSeek = seconds;
        _autoplayOnOpen = play;
        _player.Source = item;
        _player.PlaybackSession.PlaybackRate = Speeds[SpeedIndex];
        Position = seconds;
        UpdateClock();
        UpdateTranscript(force: true);
        BuildChapterList();
        SavePosition(force: true);
    }

    /// <summary>Plays a chapter that isn't finished: generation is pointed at it and chunks play as they land.</summary>
    private async Task OpenStream(int index, bool play)
    {
        if (_book is null) return;
        if (!AppServices.Models.IsInstalled(_book.Quality) && !await CastViewModel.EnsureModel(KokoroModels.For(_book.Quality))) return;

        StopStream();
        _audio = null;
        _chapter = index;
        var cast = AppServices.Store.LoadCast(_book.Id);
        var plan = AppServices.Jobs.PlanFor(_book, index, cast);
        AppServices.Jobs.RequestChapter(_book.Id, index);

        _stream = new ChapterStream(AppServices.Store, _book.Id, plan, AppServices.Engine.SampleRate);
        _list = new MediaPlaybackList();
        _list.CurrentItemChanged += (l, e) => AppServices.RunOnUI(() =>
        {
            if (_list != l) return;
            _streamItem = (int)l.CurrentItemIndex;
            ApplyPendingStreamSeek();
        });
        _streamItem = 0;
        _starved = play;
        _timings = new List<SegmentTiming>();
        _lineStart.Clear();

        HasAudio = true;
        IsStreaming = true;
        CanStart = false;
        StreamStatus = "Starting…";
        SetChapterLabels(index);
        Duration = 1;
        Position = 0;
        BuildTranscript(index, plan.Segments.Select(s => s.BlockId).Distinct().ToList());
        foreach (var p in Transcript) p.IsAvailable = false;
        _player.Source = _list;
        _player.PlaybackSession.PlaybackRate = Speeds[SpeedIndex];
        PumpStream();
        UpdateClock();
        BuildChapterList();
    }

    /// <summary>Adds any newly generated paragraphs to the playlist; resumes if playback was waiting.</summary>
    private void PumpStream()
    {
        if (_stream is null || _list is null) return;
        bool added = false;
        while (_stream.TryBuildNext() is { } chunk)
        {
            var part = new MediaPlaybackItem(MediaSource.CreateFromUri(new Uri(chunk.WavPath)));
            ApplyDisplayProperties(part, _chapter); // so Windows' media overlay names the book while it streams too
            _list.Items.Add(part);
            _timings.AddRange(chunk.Timings);
            foreach (var t in chunk.Timings)
            {
                _lineStart.TryAdd(t.LineId, t.Start);
                if (_paragraphIndex.TryGetValue(t.BlockId, out var pi)) Transcript[pi].IsAvailable = true;
            }
            added = true;
        }
        if (!added) return;
        Duration = Math.Max(1, _stream.BufferedSeconds);
        if (_starved)
        {
            // Either the very first part just arrived, or playback had caught up: continue with the new part.
            _starved = false;
            int next = Position <= 0.01 ? 0 : Math.Min(_streamItem + 1, _list.Items.Count - 1);
            _list.MoveTo((uint)next);
            _player.Play();
        }
        if (_timingIndex < 0) UpdateTranscript(force: true);
    }

    private void StopStream()
    {
        _list = null;
        _stream?.Dispose();
        _stream = null;
        _starved = false;
        IsStreaming = false;
    }

    private void SetChapterLabels(int index)
    {
        var info = _book!.Chapters[index];
        ChapterTitle = info.Title;
        HeaderLabel = $"NOW PLAYING · {info.Title.ToUpperInvariant()}";
    }

    /// <summary>The cover for Windows' media overlay, loaded once per book.</summary>
    private RandomAccessStreamReference? _coverRef;

    private async Task LoadCoverAsync(BookRecord book)
    {
        if (AppServices.Store.CoverPath(book) is not { } cover || !File.Exists(cover)) return;
        try
        {
            var reference = RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(cover));
            if (_book?.Id != book.Id) return;
            _coverRef = reference;
            // Items already playing were labelled before the cover arrived.
            if (_player.Source is MediaPlaybackItem single) ApplyDisplayProperties(single, _chapter);
            else if (_list is not null) foreach (var part in _list.Items) ApplyDisplayProperties(part, _chapter);
        }
        catch { }
    }

    /// <summary>Title, book, author and cover shown by Windows' media overlay, lock screen and Bluetooth devices.</summary>
    private void ApplyDisplayProperties(MediaPlaybackItem item, int index)
    {
        if (_book is null || index < 0 || index >= _book.Chapters.Count) return;
        var info = _book.Chapters[index];
        var props = item.GetDisplayProperties();
        props.Type = MediaPlaybackType.Music;
        props.MusicProperties.Title = info.Title;
        props.MusicProperties.Artist = _book.Author;
        props.MusicProperties.AlbumTitle = _book.Title;
        props.MusicProperties.TrackNumber = (uint)Math.Max(1, _book.IncludedChapters.ToList().FindIndex(c => c.Index == index) + 1);
        if (_coverRef is not null) props.Thumbnail = _coverRef;
        item.ApplyDisplayProperties(props);
    }

    private void OnOpened()
    {
        if (_stream is not null) { ApplyPendingStreamSeek(); return; }
        if (_pendingSeek > 0) _player.PlaybackSession.Position = TimeSpan.FromSeconds(_pendingSeek);
        _pendingSeek = -1;
        _player.PlaybackSession.PlaybackRate = Speeds[SpeedIndex];
        if (_autoplayOnOpen) _player.Play();
    }

    private void ApplyPendingStreamSeek()
    {
        if (_stream is null || _pendingSeek < 0) return;
        if (_streamItem >= _stream.Chunks.Count) return;
        var chunk = _stream.Chunks[_streamItem];
        _player.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Max(0, _pendingSeek - chunk.Start));
        _pendingSeek = -1;
    }

    private void BuildChapterList()
    {
        Chapters.Clear();
        if (_book is null) return;
        foreach (var c in _book.IncludedChapters)
        {
            bool ready = _ready.Contains(c.Index);
            Chapters.Add(new PlayerChapterViewModel
            {
                Index = c.Index,
                Title = c.Title,
                IsReady = ready,
                IsCurrent = c.Index == _chapter,
                Detail = c.Index == _chapter ? (IsStreaming ? "Playing as it's generated" : "Playing") : ready ? "Ready" : "Plays as it's generated",
            });
        }
    }

    // ---------- transcript ----------

    /// <summary>The whole chapter as paragraphs of lines, in spoken order.</summary>
    private void BuildTranscript(int index, List<string> blockOrder)
    {
        Transcript.Clear();
        _paragraphIndex.Clear();
        _timingIndex = -1;
        CurrentParagraph = -1;
        var text = AppServices.Store.LoadChapterText(_book!.Id, index);
        var analysis = AppServices.Store.LoadAnalysis(_book.Id, index);
        var linesByBlock = analysis?.Lines.GroupBy(l => l.BlockId).ToDictionary(g => g.Key, g => g.ToList());
        var blocks = text.Blocks.ToDictionary(b => b.Id);
        var notes = text.Notes.ToDictionary(n => n.Id);

        foreach (var blockId in blockOrder)
        {
            List<ContextRun> runs;
            if (blockId == "notes")
            {
                // End-of-chapter notes: one paragraph per note, keyed by note id.
                var noteRuns = _audioOrPlanLines(blockId).Select(id => notes.TryGetValue(id, out var n)
                    ? new ContextRun { LineId = id, Text = "Note: " + n.Text } : new ContextRun { LineId = id, Text = "Notes." }).ToList();
                runs = noteRuns;
            }
            else if (linesByBlock is not null && linesByBlock.TryGetValue(blockId, out var lines))
                runs = lines.Select(l => new ContextRun { LineId = l.Id, Text = l.Text }).ToList();
            else if (blocks.TryGetValue(blockId, out var b))
                runs = new List<ContextRun> { new() { LineId = blockId + "l0", Text = b.Text } };
            else continue;
            _paragraphIndex[blockId] = Transcript.Count;
            Transcript.Add(new TranscriptParagraph { BlockId = blockId, Runs = new[] { (IReadOnlyList<ContextRun>)WithMarks(index, runs) } });
        }
    }

    private IEnumerable<string> _audioOrPlanLines(string blockId) =>
        (_audio?.Timings ?? (IEnumerable<SegmentTiming>)(_stream?.Plan.Segments.Select(s => new SegmentTiming { BlockId = s.BlockId, LineId = s.LineId }) ?? Array.Empty<SegmentTiming>()))
            .Where(t => t.BlockId == blockId).Select(t => t.LineId).Distinct();

    private void SetTimings(List<SegmentTiming> timings)
    {
        _timings = timings;
        _lineStart.Clear();
        foreach (var t in timings) _lineStart.TryAdd(t.LineId, t.Start);
    }

    /// <summary>Tapping a line in the transcript jumps playback to it.</summary>
    public void JumpToLine(string lineId)
    {
        if (_lineStart.TryGetValue(lineId, out var start)) Seek(start + 0.01);
        else if (IsStreaming) StreamStatus = "That part is still being generated.";
    }

    // ---------- playback ----------

    private void OnTick()
    {
        if (NoteStatus.Length > 0 && DateTime.UtcNow - _noteStatusAt > TimeSpan.FromSeconds(4)) NoteStatus = "";
        if (_stream is not null)
        {
            PumpStream();
            UpdateStreamStatus();
        }
        if (!HasAudio) return;
        if (!_dragging)
        {
            double pos;
            if (_stream is not null)
            {
                var offset = _streamItem < _stream.Chunks.Count ? _stream.Chunks[_streamItem].Start : 0;
                pos = _pendingSeek >= 0 ? _pendingSeek : offset + _player.PlaybackSession.Position.TotalSeconds;
            }
            else pos = _pendingSeek >= 0 ? _pendingSeek : _player.PlaybackSession.Position.TotalSeconds;
            Position = pos;
        }
        UpdateClock();
        UpdateTranscript();
        if (_sleepUntil is { } until)
        {
            var left = until - DateTime.Now;
            if (left <= TimeSpan.Zero) { _player.Pause(); _sleepUntil = null; SleepLabel = "Sleep"; }
            else SleepLabel = $"Sleep {Math.Ceiling(left.TotalMinutes)}m";
        }
        if (IsPlaying && (DateTime.UtcNow - _lastSave).TotalSeconds > 5) SavePosition();
    }

    private void UpdateStreamStatus()
    {
        if (_stream is null) return;
        var device = AppServices.Engine.DeviceLabel.StartsWith("GPU") ? "GPU" : "CPU";
        StreamStatus = _starved
            ? $"Generating the next part on the {device}…"
            : $"Playing as it's generated · {Math.Round(_stream.Fraction * 100)}% of the chapter ready";
    }

    /// <summary>Called by the scrubber while the user drags (no seeking until release).</summary>
    public void BeginScrub() => _dragging = true;

    public void EndScrub(double seconds)
    {
        _dragging = false;
        Seek(seconds);
    }

    private void UpdateClock()
    {
        ElapsedText = Ui.Clock(Position);
        RemainingText = IsStreaming && _stream is { Complete: false } ? "generating…" : "−" + Ui.Clock(Duration - Position);
    }

    private void UpdateTranscript(bool force = false)
    {
        if (_timings.Count == 0) return;
        var t = Position;
        int i = _timings.FindLastIndex(x => x.Start <= t + 0.05);
        if (i < 0) i = 0;
        if (i == _timingIndex && !force) return;
        _timingIndex = i;
        var timing = _timings[i];
        if (_paragraphIndex.TryGetValue(timing.BlockId, out var pi) && pi != CurrentParagraph)
        {
            if (CurrentParagraph >= 0 && CurrentParagraph < Transcript.Count) Transcript[CurrentParagraph].IsCurrent = false;
            Transcript[pi].IsCurrent = true;
            CurrentParagraph = pi;
        }

        var member = _cast.Find(timing.SpeakerId) ?? _cast.Members.FirstOrDefault(m => m.IsNarrator);
        SpeakerName = member?.Name ?? "Narrator";
        SpeakerInitials = member?.Initials ?? "N";
        SpeakerColor = member?.Color ?? VoiceCatalog.NarratorColor;
        SpeakerLabel = $"{SpeakerName} · voice {VoiceCatalog.Name(member?.VoiceId ?? VoiceCatalog.DefaultNarrator)}";
        MiniSubtitle = $"{ChapterTitle} · {(member?.IsNarrator != false ? "Narrator" : SpeakerName)} speaking";
    }

    private void SyncPlaying()
    {
        IsPlaying = _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
        if (!IsPlaying) SavePosition();
        else if (_book is not null)
        {
            var b = AppServices.Store.LoadBook(_book.Id);
            if (b is not null && (b.LastPlayedUtc is null || (DateTime.UtcNow - b.LastPlayedUtc.Value).TotalMinutes > 1))
            {
                b.LastPlayedUtc = DateTime.UtcNow;
                AppServices.Store.SaveBook(b);
                _shell.Library.Refresh(b.Id);
            }
        }
    }

    private void OnEnded()
    {
        if (_stream is not null && !_stream.Complete)
        {
            // Caught up with generation: wait for the next part, then carry on.
            _starved = true;
            UpdateStreamStatus();
            return;
        }
        if (_sleepAtChapterEnd)
        {
            _sleepAtChapterEnd = false;
            SleepLabel = "Sleep";
            IsPlaying = false;
            return;
        }
        int at = _book?.IncludedChapters.ToList().FindIndex(c => c.Index == _chapter) ?? -1;
        var next = _book?.IncludedChapters.Skip(at + 1).FirstOrDefault();
        if (next is not null) _ = OpenChapterAsync(next.Index, 0, true);
        else { IsPlaying = false; SavePosition(force: true); }
    }

    public void SavePosition(bool force = false)
    {
        if (_book is null || _chapter < 0) return;
        if (!force && (DateTime.UtcNow - _lastSave).TotalSeconds < 1) return;
        _lastSave = DateTime.UtcNow;
        var b = AppServices.Store.LoadBook(_book.Id);
        if (b is null) return;
        b.Position = new PlaybackPosition { ChapterIndex = _chapter, Seconds = Position, UpdatedUtc = DateTime.UtcNow };
        AppServices.Store.SaveBook(b);
    }

    [RelayCommand]
    public void TogglePlay()
    {
        if (!HasAudio)
        {
            if (_book is null && _shell.Library.ContinueItem is { } c) Load(c.Id, autoplay: true);
            else if (_book is not null) _ = StartListeningAsync();
            return;
        }
        _preview.Pause();
        if (IsPlaying) _player.Pause();
        else if (_starved) { /* waiting for generation; it resumes on its own */ }
        else _player.Play();
    }

    [RelayCommand] public void Back() => Seek(Position - 15);
    [RelayCommand] public void Forward() => Seek(Position + 30);

    public void Seek(double seconds)
    {
        if (!HasAudio) return;
        if (seconds < 0 && Position < 2) { Step(-1); return; }
        seconds = Math.Clamp(seconds, 0, Duration);
        if (_stream is not null && _list is not null)
        {
            int item = _stream.Chunks.FindLastIndex(c => c.Start <= seconds);
            if (item < 0) return;
            _pendingSeek = seconds;
            if (item != _streamItem) _list.MoveTo((uint)item);
            else ApplyPendingStreamSeek();
            if (_starved) { _starved = false; _player.Play(); }
        }
        else _player.PlaybackSession.Position = TimeSpan.FromSeconds(seconds);
        Position = seconds;
        UpdateClock();
        UpdateTranscript();
    }

    private void Step(int dir)
    {
        if (_book is null) return;
        // Like other players: "previous" first goes back to the start of this chapter.
        if (dir < 0 && Position > 5) { Seek(0); return; }
        var included = _book.IncludedChapters.Select(c => c.Index).ToList();
        int target = included.IndexOf(_chapter) + dir;
        if (target >= 0 && target < included.Count) _ = OpenChapterAsync(included[target], 0, IsPlaying || dir > 0);
    }

    [RelayCommand] public void CycleSpeed() => SpeedIndex = (SpeedIndex + 1) % Speeds.Length;

    public void SpeedStep(int dir) => SpeedIndex = Math.Clamp(SpeedIndex + dir, 0, Speeds.Length - 1);

    /// <summary>Any chapter can be opened: finished ones play at once, others play as they're generated.</summary>
    [RelayCommand]
    private void GoToChapter(PlayerChapterViewModel? c)
    {
        if (c is null) return;
        _ = OpenChapterAsync(c.Index, 0, true);
    }

    public void SetSleep(int minutes)
    {
        _sleepAtChapterEnd = false;
        _sleepUntil = null;
        if (minutes == 0) { SleepLabel = "Sleep"; return; }
        if (minutes < 0) { _sleepAtChapterEnd = true; SleepLabel = "End of chapter"; return; }
        _sleepUntil = DateTime.Now.AddMinutes(minutes);
        SleepLabel = $"Sleep {minutes}m";
    }

    [RelayCommand]
    private async Task FixAsync()
    {
        if (_book is null || _timingIndex < 0 || _timingIndex >= _timings.Count) return;
        if (_book.Analysis != AnalysisState.Done)
        {
            await Dialogs.InfoAsync("Find speakers first", "This book hasn't been analyzed for speakers yet, so everything is read by the narrator. Use “Find speakers” on the Structure tab.");
            _shell.OpenBook(_book.Id, BookTab.Structure);
            return;
        }
        var timing = _timings[_timingIndex];
        _player.Pause();
        _shell.FixLine(_book.Id, _chapter, timing.LineId);
    }

    [RelayCommand] private void Collapse() => _shell.GoLibrary();
    [RelayCommand] private void OpenGenerate() { if (_book is not null) _shell.OpenBook(_book.Id, BookTab.Generate); }

    // ---------- voice preview ----------

    private int _previewSeq;
    private TaskCompletionSource? _previewDone;

    /// <summary>
    /// Speaks a sample through the preview player. The newest preview wins: starting another one
    /// (e.g. clicking ▶ on the next voice) ends the current one instead of overlapping or cutting it off later.
    /// </summary>
    public async Task PreviewAsync(string text, string voiceId, Bilgames.Core.Analysis.Delivery? delivery = null)
    {
        int mine = ++_previewSeq;
        if (IsPlaying) _player.Pause();
        var d = delivery ?? Bilgames.Core.Analysis.Delivery.Neutral;
        text = SynthesisPlanner.ForSpeechText(text);
        var pcm = await Task.Run(() => AppServices.Engine.SynthesizeAsync(text, voiceId, d.Speed, QualityProfile.Balanced, CancellationToken.None));
        if (mine != _previewSeq) return; // a newer preview started while this one was being made
        pcm = Loudness.Apply(pcm, d.GainDb);
        var path = Path.Combine(AppServices.Store.TempDir, $"preview-{Guid.NewGuid():N}.wav");
        Wav.Write(path, pcm, AppServices.Engine.SampleRate);
        _previewDone?.TrySetResult();
        var done = new TaskCompletionSource();
        _previewDone = done;
        void Ended(MediaPlayer s, object e) => done.TrySetResult();
        _preview.MediaEnded += Ended;
        _preview.Source = MediaSource.CreateFromUri(new Uri(path));
        _preview.Play();
        await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(pcm.Length / 24000.0 + 2)));
        _preview.MediaEnded -= Ended;
        if (mine == _previewSeq) _preview.Source = null;
        try { File.Delete(path); } catch { }
    }
}
