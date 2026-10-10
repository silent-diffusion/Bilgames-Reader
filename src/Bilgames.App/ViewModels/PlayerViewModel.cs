using System.Collections.ObjectModel;
using Bilgames.App.Controls;
using Bilgames.App.Services;
using Bilgames.Core.Audio;
using Bilgames.Core.Model;
using Bilgames.Core.Tts;
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

public sealed partial class PlayerViewModel : ObservableObject
{
    private static readonly double[] Speeds = { 0.8, 1.0, 1.25, 1.5, 2.0 };

    private readonly ShellViewModel _shell;
    private readonly MediaPlayer _player = new() { AudioCategory = MediaPlayerAudioCategory.Media };
    private readonly MediaPlayer _preview = new() { AudioCategory = MediaPlayerAudioCategory.Media };
    private readonly DispatcherQueueTimer _tick;
    private BookRecord? _book;
    private CastSheet _cast = new();
    private ChapterAudio? _audio;
    private Dictionary<string, string> _blockText = new();
    private List<string> _blockOrder = new();
    private List<int> _ready = new();
    private int _chapter = -1;
    private int _timingIndex = -1;
    private DateTime _lastSave;
    private bool _sleepAtChapterEnd;
    private DateTime? _sleepUntil;
    private double _pendingSeek = -1;
    private bool _autoplayOnOpen;

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
    [ObservableProperty] private string _prevText = "";
    [ObservableProperty] private string _currentText = "";
    [ObservableProperty] private string _nextText = "";
    [ObservableProperty] private string _speakerName = "Narrator";
    [ObservableProperty] private string _speakerLabel = "";
    [ObservableProperty] private string _speakerInitials = "N";
    [ObservableProperty] private string _speakerColor = "#C9CED6";
    [ObservableProperty] private string _miniSubtitle = "";
    [ObservableProperty] private string _emptyText = "Pick a book from your library to start listening.";
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private string _exportLabel = "Export";

    private static readonly string[] SpeedLabels = { "0.8×", "1.0×", "1.25×", "1.5×", "2.0×" };
    private bool _restoringSpeed;

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

    public void Load(string bookId, bool autoplay = false)
    {
        SavePosition();
        _book = AppServices.Store.LoadBook(bookId);
        HasBook = _book is not null;
        if (_book is null) return;
        Title = _book.Title;
        Author = _book.Author;
        CoverColor = _book.CoverColor;
        CoverPath = AppServices.Store.CoverPath(_book);
        _cast = AppServices.Store.LoadCast(bookId);
        int idx = Array.FindIndex(Speeds, s => Math.Abs(s - _book.PlaybackRate) < 0.01);
        _restoringSpeed = true; // restoring the saved speed shouldn't write it back
        SpeedIndex = idx < 0 ? 1 : idx;
        _restoringSpeed = false;
        RefreshReady();

        var pos = _book.Position;
        int chapter = _ready.Contains(pos.ChapterIndex) ? pos.ChapterIndex : _ready.FirstOrDefault(-1);
        if (chapter < 0)
        {
            Clear();
            return;
        }
        OpenChapter(chapter, chapter == pos.ChapterIndex ? pos.Seconds : 0, autoplay);
        _shell.RaiseLayout();
    }

    public void Unload()
    {
        _player.Pause();
        _player.Source = null;
        _book = null;
        HasBook = false;
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
        ChapterTitle = "";
        HeaderLabel = "NOW PLAYING";
        PrevText = NextText = "";
        CurrentText = "";
        EmptyText = _book is null
            ? "Pick a book from your library to start listening."
            : "Nothing to play yet. Generate audio on the book's Generate tab; chapters become playable as soon as each one is ready.";
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

    /// <summary>New chapters finished generating, or speakers/voices changed.</summary>
    public void OnChaptersChanged()
    {
        if (_book is null) return;
        _book = AppServices.Store.LoadBook(_book.Id) ?? _book;
        _cast = AppServices.Store.LoadCast(_book.Id);
        RefreshReady();
        if (!HasAudio && _ready.Count > 0) OpenChapter(_ready[0], 0, false);
        else if (HasAudio && _chapter >= 0)
        {
            // The current chapter may have been regenerated with corrected speakers.
            var fresh = AppServices.Store.LoadChapterAudio(_book.Id, _chapter);
            if (fresh is not null && _audio is not null && fresh.PlanHash != _audio.PlanHash) OpenChapter(_chapter, Position, IsPlaying);
        }
        BuildChapterList();
    }

    private async void OpenChapter(int index, double seconds, bool play)
    {
        if (_book is null) return;
        var audio = AppServices.Store.LoadChapterAudio(_book.Id, index);
        if (audio is null) return;
        _audio = audio;
        _chapter = index;
        _timingIndex = -1;
        HasAudio = true;
        var info = _book.Chapters[index];
        ChapterTitle = info.Title;
        int ordinal = _book.IncludedChapters.ToList().FindIndex(c => c.Index == index) + 1;
        HeaderLabel = $"NOW PLAYING · {info.Title.ToUpperInvariant()}";
        Duration = Math.Max(1, audio.DurationSeconds);

        // Transcript source: the chapter's paragraphs (and notes) in spoken order.
        var text = AppServices.Store.LoadChapterText(_book.Id, index);
        _blockText = text.Blocks.Where(b => b.Kind != BlockKind.SceneBreak).ToDictionary(b => b.Id, b => b.Text);
        foreach (var n in text.Notes) _blockText[n.Id] = "Note: " + n.Text;
        _blockText["notes"] = "Notes";
        _blockOrder = audio.Timings.Select(t => t.BlockId).Distinct().ToList();

        var item = new MediaPlaybackItem(MediaSource.CreateFromUri(new Uri(AppServices.Store.ChapterAudioPath(_book.Id, index))));
        var props = item.GetDisplayProperties();
        props.Type = MediaPlaybackType.Music;
        props.MusicProperties.Title = info.Title;
        props.MusicProperties.Artist = _book.Author;
        props.MusicProperties.AlbumTitle = _book.Title;
        props.MusicProperties.TrackNumber = (uint)Math.Max(1, ordinal);
        if (AppServices.Store.CoverPath(_book) is { } cover && File.Exists(cover))
        {
            try { props.Thumbnail = RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(cover)); } catch { }
        }
        item.ApplyDisplayProperties(props);

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

    private void OnOpened()
    {
        if (_pendingSeek > 0) _player.PlaybackSession.Position = TimeSpan.FromSeconds(_pendingSeek);
        _pendingSeek = -1;
        _player.PlaybackSession.PlaybackRate = Speeds[SpeedIndex];
        if (_autoplayOnOpen) _player.Play();
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
                Detail = c.Index == _chapter ? "Playing" : ready ? "Ready" : "Not generated yet",
            });
        }
    }

    // ---------- playback ----------

    private void OnTick()
    {
        if (!HasAudio) return;
        var pos = _pendingSeek >= 0 ? _pendingSeek : _player.PlaybackSession.Position.TotalSeconds;
        if (!_dragging) Position = pos;
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

    private bool _dragging;

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
        RemainingText = "−" + Ui.Clock(Duration - Position);
    }

    private void UpdateTranscript(bool force = false)
    {
        if (_audio is null || _audio.Timings.Count == 0) return;
        var t = Position;
        int i = _audio.Timings.FindLastIndex(x => x.Start <= t + 0.05);
        if (i < 0) i = 0;
        if (i == _timingIndex && !force) return;
        _timingIndex = i;
        var timing = _audio.Timings[i];
        int bi = _blockOrder.IndexOf(timing.BlockId);
        CurrentText = BlockText(bi);
        PrevText = BlockText(bi - 1);
        NextText = BlockText(bi + 1);

        var member = _cast.Find(timing.SpeakerId) ?? _cast.Members.FirstOrDefault(m => m.IsNarrator);
        SpeakerName = member?.Name ?? "Narrator";
        SpeakerInitials = member?.Initials ?? "N";
        SpeakerColor = member?.Color ?? VoiceCatalog.NarratorColor;
        SpeakerLabel = $"{SpeakerName} · voice {VoiceCatalog.Name(member?.VoiceId ?? VoiceCatalog.DefaultNarrator)}";
        MiniSubtitle = $"{ChapterTitle} · {(member?.IsNarrator != false ? "Narrator" : SpeakerName)} speaking";
    }

    private string BlockText(int i) => i >= 0 && i < _blockOrder.Count && _blockText.TryGetValue(_blockOrder[i], out var s) ? s : "";

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
        if (_sleepAtChapterEnd)
        {
            _sleepAtChapterEnd = false;
            SleepLabel = "Sleep";
            IsPlaying = false;
            return;
        }
        int next = _ready.FindIndex(i => i > _chapter);
        if (next >= 0) OpenChapter(_ready[next], 0, true);
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
            return;
        }
        _preview.Pause();
        if (IsPlaying) _player.Pause();
        else _player.Play();
    }

    [RelayCommand] public void Back() => Seek(Position - 15);
    [RelayCommand] public void Forward() => Seek(Position + 30);

    public void Seek(double seconds)
    {
        if (!HasAudio) return;
        if (seconds < 0 && _chapter > 0 && Position < 2) { Step(-1); return; }
        seconds = Math.Clamp(seconds, 0, Duration);
        _player.PlaybackSession.Position = TimeSpan.FromSeconds(seconds);
        Position = seconds;
        UpdateClock();
        UpdateTranscript();
    }

    private void Step(int dir)
    {
        int at = _ready.IndexOf(_chapter);
        int target = at + dir;
        if (target >= 0 && target < _ready.Count) OpenChapter(_ready[target], 0, IsPlaying || dir > 0);
    }

    [RelayCommand] public void CycleSpeed() => SpeedIndex = (SpeedIndex + 1) % Speeds.Length;

    public void SpeedStep(int dir) => SpeedIndex = Math.Clamp(SpeedIndex + dir, 0, Speeds.Length - 1);

    [RelayCommand]
    private void GoToChapter(PlayerChapterViewModel? c)
    {
        if (c is null || !c.IsReady) return;
        OpenChapter(c.Index, 0, true);
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
        if (_book is null || _audio is null || _timingIndex < 0) return;
        if (_book.Analysis != AnalysisState.Done)
        {
            await Dialogs.InfoAsync("Find speakers first", "This book hasn't been analyzed for speakers yet, so everything is read by the narrator. Use “Find speakers” on the Structure tab.");
            _shell.OpenBook(_book.Id, BookTab.Structure);
            return;
        }
        var timing = _audio.Timings[_timingIndex];
        _player.Pause();
        _shell.FixLine(_book.Id, _chapter, timing.LineId);
    }

    [RelayCommand] private void Collapse() => _shell.GoLibrary();
    [RelayCommand] private void OpenGenerate() { if (_book is not null) _shell.OpenBook(_book.Id, BookTab.Generate); }

    // ---------- voice preview ----------

    public async Task PreviewAsync(string text, string voiceId)
    {
        if (IsPlaying) _player.Pause();
        var pcm = await Task.Run(() => AppServices.Engine.SynthesizeAsync(text, voiceId, 1f, QualityProfile.Balanced, CancellationToken.None));
        var path = Path.Combine(AppServices.Store.TempDir, $"preview-{Guid.NewGuid():N}.wav");
        Wav.Write(path, pcm, AppServices.Engine.SampleRate);
        var done = new TaskCompletionSource();
        void Ended(MediaPlayer s, object e) => done.TrySetResult();
        _preview.MediaEnded += Ended;
        _preview.Source = MediaSource.CreateFromUri(new Uri(path));
        _preview.Play();
        await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(pcm.Length / 24000.0 + 2)));
        _preview.MediaEnded -= Ended;
        _preview.Source = null;
        try { File.Delete(path); } catch { }
    }

    // ---------- export ----------

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (_book is null || IsExporting) return;
        RefreshReady();
        var included = _book.IncludedChapters.ToList();
        if (_ready.Count == 0)
        {
            await Dialogs.InfoAsync("Nothing to export yet", "Generate audio first; you can export once chapters are ready.");
            return;
        }
        if (_ready.Count < included.Count)
        {
            var go = await Dialogs.ConfirmAsync("Export ready chapters?",
                $"{_ready.Count} of {included.Count} chapters are ready. Export those now as an audiobook, or wait until generation finishes.",
                $"Export {_ready.Count} chapters");
            if (!go) return;
        }
        var safe = string.Concat(_book.Title.Split(Path.GetInvalidFileNameChars()));
        var dest = await Pickers.PickSaveAsync(safe, "Audiobook with chapters", ".m4b");
        if (dest is null) return;

        IsExporting = true;
        var book = _book;
        var chapters = _ready.ToList();
        try
        {
            await Task.Run(() => Export(book, chapters, dest, p => AppServices.RunOnUI(() => ExportLabel = $"Exporting {p:0}%")));
            ExportLabel = "Export";
            await Dialogs.InfoAsync("Audiobook exported", $"Saved “{Path.GetFileName(dest)}” with {chapters.Count} chapter markers.\n\n{Path.GetDirectoryName(dest)}");
        }
        catch (Exception ex)
        {
            AppServices.Log("Export failed: " + ex);
            await Dialogs.InfoAsync("Export failed", ex.Message);
        }
        finally
        {
            IsExporting = false;
            ExportLabel = "Export";
        }
    }

    private static async Task Export(BookRecord book, List<int> chapters, string dest, Action<double> progress)
    {
        var store = AppServices.Store;
        var tmp = Path.Combine(store.TempDir, $"export-{book.Id}");
        Directory.CreateDirectory(tmp);
        var bigWav = Path.Combine(tmp, "book.wav");
        var m4a = Path.Combine(tmp, "book.m4a");
        var marks = new List<Mp4Chapter>();
        try
        {
            using (var cat = new WavConcatenator(bigWav, AppServices.Engine.SampleRate))
            {
                for (int i = 0; i < chapters.Count; i++)
                {
                    var chWav = Path.Combine(tmp, $"{chapters[i]}.wav");
                    await AppServices.Encoder.DecodeToWavAsync(store.ChapterAudioPath(book.Id, chapters[i]), chWav, AppServices.Engine.SampleRate, CancellationToken.None);
                    marks.Add(new Mp4Chapter(TimeSpan.FromSeconds(cat.PositionSeconds), book.Chapters[chapters[i]].Title));
                    cat.AppendFile(chWav);
                    File.Delete(chWav);
                    progress(70.0 * (i + 1) / chapters.Count);
                }
            }
            await AppServices.Encoder.EncodeAacAsync(bigWav, m4a, CancellationToken.None);
            progress(95);
            Mp4Metadata.Write(m4a, marks, book.Title, book.Author);
            File.Move(m4a, dest, overwrite: true);
            progress(100);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }
}
