using System.Collections.ObjectModel;
using Bilgames.App.Controls;
using Bilgames.App.Services;
using Bilgames.Core.Export;
using Bilgames.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bilgames.App.ViewModels;

/// <summary>A bookmark or highlight in the player's list.</summary>
public sealed class NoteItem
{
    public string Id { get; init; } = "";
    public bool IsBookmark { get; init; }
    public int ChapterIndex { get; init; }
    public double Seconds { get; init; }
    public string LineId { get; init; } = "";
    public string Where { get; init; } = "";
    public string Text { get; init; } = "";
    public string Note { get; init; } = "";
    public string Swatch { get; init; } = "#ECEEF1";
    public bool HasNote => Note.Length > 0;
    public string KindLabel => IsBookmark ? "BOOKMARK" : "HIGHLIGHT";
    public string OpenName => $"{(IsBookmark ? "Bookmark" : "Highlight")}, {Where}: {Text}. Play from here";
    public string EditName => HasNote ? "Edit note" : "Add a note";
    public string DeleteName => IsBookmark ? "Delete bookmark" : "Remove highlight";
}

/// <summary>Bookmarks and highlights: making them, showing them in the transcript, the list, and export.</summary>
public sealed partial class PlayerViewModel
{
    private AnnotationSet _notes = new();

    public ObservableCollection<NoteItem> Notes { get; } = new();

    [ObservableProperty] private bool _showNotes;
    [ObservableProperty] private string _notesSummary = "";
    [ObservableProperty] private string _noteStatus = "";
    [ObservableProperty] private bool _hasNotes;

    private DateTime _noteStatusAt;

    public string NotesButtonName => ShowNotes ? "Back to the text" : $"Bookmarks and highlights ({Notes.Count})";

    /// <summary>The middle of the player shows the transcript, the notes list, or the "nothing to play" message.</summary>
    public bool ShowTranscript => HasAudio && !ShowNotes;
    public bool ShowEmpty => !HasAudio && !ShowNotes;

    partial void OnShowNotesChanged(bool value)
    {
        OnPropertyChanged(nameof(NotesButtonName));
        OnPropertyChanged(nameof(ShowTranscript));
        OnPropertyChanged(nameof(ShowEmpty));
    }

    partial void OnHasAudioChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowTranscript));
        OnPropertyChanged(nameof(ShowEmpty));
    }

    partial void OnNoteStatusChanged(string value) => _noteStatusAt = DateTime.UtcNow;

    private void LoadNotes()
    {
        _notes = _book is null ? new AnnotationSet() : AppServices.Store.LoadAnnotations(_book.Id);
        RebuildNotes();
    }

    private void SaveNotes()
    {
        if (_book is null) return;
        AppServices.Store.SaveAnnotations(_book.Id, _notes);
        RebuildNotes();
    }

    private void RebuildNotes()
    {
        Notes.Clear();
        if (_book is not null)
        {
            string Title(int i) => i >= 0 && i < _book.Chapters.Count ? _book.Chapters[i].Title : "";
            string Where(int ch, double s) => s >= 0 ? $"{Title(ch)} · {AnnotationExport.Clock(s)}" : Title(ch);
            var items = _notes.Bookmarks.Select(b => (b.ChapterIndex, b.Seconds, b.CreatedUtc, Item: new NoteItem
                {
                    Id = b.Id, IsBookmark = true, ChapterIndex = b.ChapterIndex, Seconds = b.Seconds, LineId = b.LineId,
                    Where = Where(b.ChapterIndex, b.Seconds), Text = b.Excerpt, Note = b.Note, Swatch = "#ECEEF1",
                }))
                .Concat(_notes.Highlights.Select(h => (h.ChapterIndex, h.Seconds, h.CreatedUtc, Item: new NoteItem
                {
                    Id = h.Id, ChapterIndex = h.ChapterIndex, Seconds = h.Seconds, LineId = h.StartLineId,
                    Where = Where(h.ChapterIndex, h.Seconds), Text = h.Text, Note = h.Note, Swatch = HighlightPalette.Swatch(h.Color),
                })))
                .OrderBy(x => x.ChapterIndex).ThenBy(x => x.Seconds < 0 ? double.MaxValue : x.Seconds).ThenBy(x => x.CreatedUtc);
            foreach (var x in items) Notes.Add(x.Item);
        }
        HasNotes = Notes.Count > 0;
        int b = _notes.Bookmarks.Count, h = _notes.Highlights.Count;
        NotesSummary = $"{b} {(b == 1 ? "bookmark" : "bookmarks")} · {h} {(h == 1 ? "highlight" : "highlights")}";
        OnPropertyChanged(nameof(NotesButtonName));
    }

    [RelayCommand] private void ToggleNotes() => ShowNotes = !ShowNotes;

    // ---------- transcript marks ----------

    /// <summary>A paragraph's lines with this chapter's highlights attached.</summary>
    private List<ContextRun> WithMarks(int chapter, List<ContextRun> runs)
    {
        var highlights = _notes.Highlights.Where(h => h.ChapterIndex == chapter).ToList();
        if (highlights.Count == 0) return runs;
        var order = runs.Select((r, i) => (r.LineId, i)).ToDictionary(x => x.LineId, x => x.i);
        var marks = runs.Select(_ => new List<RunMark>()).ToList();
        foreach (var h in highlights)
        {
            if (!order.TryGetValue(h.StartLineId, out var a) || !order.TryGetValue(h.EndLineId, out var b)) continue;
            for (int i = a; i <= b; i++)
            {
                int start = i == a ? h.StartOffset : 0, end = i == b ? h.EndOffset : runs[i].Text.Length;
                if (end > start) marks[i].Add(new RunMark { HighlightId = h.Id, Start = start, Length = end - start, Color = h.Color, HasNote = h.Note.Length > 0 });
            }
        }
        return runs.Select((r, i) => marks[i].Count == 0 ? r : new ContextRun
        {
            LineId = r.LineId, Text = r.Text, Color = r.Color, Speaker = r.Speaker, IsTarget = r.IsTarget, Marks = marks[i],
        }).ToList();
    }

    /// <summary>Redraws the transcript paragraphs that contain any of the given lines.</summary>
    private void RefreshMarks(IEnumerable<string> lineIds)
    {
        var ids = lineIds.ToHashSet();
        foreach (var p in Transcript)
        {
            var lines = p.Runs.SelectMany(r => r).ToList();
            if (!lines.Any(l => ids.Contains(l.LineId))) continue;
            var plain = lines.Select(l => new ContextRun { LineId = l.LineId, Text = l.Text, Color = l.Color, Speaker = l.Speaker }).ToList();
            p.Runs = new[] { (IReadOnlyList<ContextRun>)WithMarks(_chapter, plain) };
        }
    }

    private string? LineText(string lineId) =>
        Transcript.SelectMany(p => p.Runs.SelectMany(r => r)).FirstOrDefault(l => l.LineId == lineId)?.Text;

    private string? CurrentLineId => _timingIndex >= 0 && _timingIndex < _timings.Count ? _timings[_timingIndex].LineId : null;

    private static string Excerpt(string text, int max = 220)
    {
        text = text.Trim();
        if (text.Length <= max) return text;
        int cut = text.LastIndexOf(' ', max);
        return text[..(cut > max / 2 ? cut : max)] + "…";
    }

    // ---------- making them ----------

    /// <summary>Bookmarks the moment being played (B).</summary>
    [RelayCommand]
    public void AddBookmark()
    {
        if (_book is null || !HasAudio || _chapter < 0) return;
        var lineId = CurrentLineId ?? "";
        AddBookmarkAt(lineId, Position);
    }

    /// <summary>Bookmarks a line picked in the transcript.</summary>
    public void BookmarkLine(string lineId) =>
        AddBookmarkAt(lineId, _lineStart.TryGetValue(lineId, out var s) ? s : Position);

    private void AddBookmarkAt(string lineId, double seconds)
    {
        if (_book is null || _chapter < 0) return;
        _notes.Bookmarks.Add(new Bookmark
        {
            ChapterIndex = _chapter, Seconds = Math.Max(0, seconds), LineId = lineId, Excerpt = Excerpt(LineText(lineId) ?? ""),
        });
        SaveNotes();
        NoteStatus = $"Bookmarked {ChapterTitle} at {AnnotationExport.Clock(seconds)}.";
    }

    /// <summary>Highlights the line being read right now (H).</summary>
    [RelayCommand]
    public void HighlightCurrent()
    {
        if (CurrentLineId is not { } id || LineText(id) is not { } text) return;
        _ = AddHighlightAsync(new TextSpanSelection { StartRun = new ContextRun { LineId = id, Text = text }, StartOffset = 0, EndRun = new ContextRun { LineId = id, Text = text }, EndOffset = text.Length, Text = text },
            HighlightColor.Yellow, false);
    }

    public async Task AddHighlightAsync(TextSpanSelection span, HighlightColor color, bool withNote)
    {
        if (_book is null || _chapter < 0) return;
        var block = Transcript.FirstOrDefault(p => p.Runs.SelectMany(r => r).Any(l => l.LineId == span.StartRun.LineId))?.BlockId ?? "";
        // Highlighting exactly the same passage again just changes its colour.
        var same = _notes.Highlights.FirstOrDefault(h => h.ChapterIndex == _chapter && h.StartLineId == span.StartRun.LineId && h.StartOffset == span.StartOffset
                                                         && h.EndLineId == span.EndRun.LineId && h.EndOffset == span.EndOffset);
        var highlight = same ?? new Highlight
        {
            ChapterIndex = _chapter, BlockId = block,
            StartLineId = span.StartRun.LineId, StartOffset = span.StartOffset,
            EndLineId = span.EndRun.LineId, EndOffset = span.EndOffset,
            Text = span.Text,
            Seconds = _lineStart.TryGetValue(span.StartRun.LineId, out var s) ? s : -1,
        };
        highlight.Color = color;
        if (same is null) _notes.Highlights.Add(highlight);
        if (withNote)
        {
            var note = await Dialogs.PromptAsync("Note", Excerpt(span.Text, 300), highlight.Note, "Your note", "Save", multiline: true);
            if (note is not null) highlight.Note = note;
        }
        SaveNotes();
        RefreshMarks(new[] { highlight.StartLineId, highlight.EndLineId });
        NoteStatus = same is null ? "Highlighted." : "Highlight colour changed.";
    }

    public void RemoveHighlight(string id)
    {
        var h = _notes.Highlights.FirstOrDefault(x => x.Id == id);
        if (h is null) return;
        _notes.Highlights.Remove(h);
        SaveNotes();
        if (h.ChapterIndex == _chapter) RefreshMarks(new[] { h.StartLineId, h.EndLineId });
        NoteStatus = "Highlight removed.";
    }

    public async Task EditNoteAsync(string id)
    {
        var h = _notes.Highlights.FirstOrDefault(x => x.Id == id);
        var b = _notes.Bookmarks.FirstOrDefault(x => x.Id == id);
        if (h is null && b is null) return;
        var note = await Dialogs.PromptAsync(h is not null ? "Note" : "Bookmark note", Excerpt(h?.Text ?? b!.Excerpt, 300),
            h?.Note ?? b!.Note, "Your note", "Save", multiline: true);
        if (note is null) return;
        if (h is not null) h.Note = note; else b!.Note = note;
        SaveNotes();
        if (h is not null && h.ChapterIndex == _chapter) RefreshMarks(new[] { h.StartLineId, h.EndLineId });
    }

    // ---------- the list ----------

    [RelayCommand]
    private async Task EditNote(NoteItem? item)
    {
        if (item is not null) await EditNoteAsync(item.Id);
    }

    [RelayCommand]
    private void DeleteNote(NoteItem? item)
    {
        if (item is null) return;
        if (item.IsBookmark)
        {
            _notes.Bookmarks.RemoveAll(b => b.Id == item.Id);
            SaveNotes();
            NoteStatus = "Bookmark deleted.";
        }
        else RemoveHighlight(item.Id);
    }

    /// <summary>Plays from a bookmark or highlight, opening its chapter if needed.</summary>
    [RelayCommand]
    private async Task OpenNote(NoteItem? item)
    {
        if (item is null || _book is null) return;
        ShowNotes = false;
        if (item.ChapterIndex == _chapter && HasAudio)
        {
            if (item.Seconds >= 0) Seek(item.Seconds + 0.01); else JumpToLine(item.LineId);
            return;
        }
        await OpenChapterAsync(item.ChapterIndex, Math.Max(0, item.Seconds), true);
    }

    // ---------- export ----------

    [RelayCommand]
    private async Task ExportNotesAsync()
    {
        if (_book is null) return;
        if (_notes.IsEmpty)
        {
            await Dialogs.InfoAsync("Nothing to export yet", "Bookmark a moment (B) or select text in the transcript to highlight it, then export them here.");
            return;
        }
        var safe = string.Concat(_book.Title.Split(Path.GetInvalidFileNameChars())) + " - notes";
        var dest = await Pickers.PickSaveAsync(safe, ("Markdown", ".md"), ("Plain text", ".txt"), ("Spreadsheet (CSV)", ".csv"));
        if (dest is null) return;
        try
        {
            var book = _book;
            var set = _notes;
            await Task.Run(() => File.WriteAllText(dest, AnnotationExport.Write(book, set, AnnotationExport.FormatFor(dest)), new System.Text.UTF8Encoding(true)));
            NoteStatus = $"Exported to {Path.GetFileName(dest)}.";
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync("Export failed", ex.Message);
        }
    }
}
