using System.Collections.ObjectModel;
using Bilgames.App.Controls;
using Bilgames.App.Services;
using Bilgames.Core.Export;
using Bilgames.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bilgames.App.ViewModels;

public enum NotesFilter
{
    All,
    Bookmarks,
    Highlights,
    WithNotes,
}

/// <summary>A bookmark or highlight from any book, for the global Notes page.</summary>
public sealed class GlobalNoteItem
{
    public string Id { get; init; } = "";
    public string BookId { get; init; } = "";
    public string BookTitle { get; init; } = "";
    public string Author { get; init; } = "";
    public string CoverColor { get; init; } = "#2B4A5E";
    public string? CoverPath { get; init; }
    public bool IsBookmark { get; init; }
    public int ChapterIndex { get; init; }
    public double Seconds { get; init; }
    public string Where { get; init; } = "";
    public string Text { get; init; } = "";
    public string Note { get; init; } = "";
    public string Swatch { get; init; } = "#ECEEF1";
    public DateTime CreatedUtc { get; init; }

    public bool HasNote => Note.Length > 0;
    public string KindLabel => IsBookmark ? "BOOKMARK" : "HIGHLIGHT";
    public string Caption => $"{BookTitle} · {Where}";
    public string When => CreatedUtc.ToLocalTime().ToString("d MMM yyyy");
    public string OpenName => $"{(IsBookmark ? "Bookmark" : "Highlight")} in {BookTitle}, {Where}: {Text}. Play from here";
    public string EditName => HasNote ? "Edit note" : "Add a note";
    public string DeleteName => IsBookmark ? "Delete bookmark" : "Remove highlight";
}

/// <summary>Every bookmark, highlight and note across the library: filter, search, play, edit, export.</summary>
public sealed partial class NotesViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private List<(BookRecord Book, AnnotationSet Set)> _all = new();
    private List<GlobalNoteItem> _items = new();

    public NotesViewModel(ShellViewModel shell) => _shell = shell;

    public ObservableCollection<GlobalNoteItem> Items { get; } = new();

    [ObservableProperty] private NotesFilter _filter = NotesFilter.All;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _byBook;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _hasAny;
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private string _emptyText = "";

    public bool IsAll => Filter == NotesFilter.All;
    public bool IsBookmarks => Filter == NotesFilter.Bookmarks;
    public bool IsHighlights => Filter == NotesFilter.Highlights;
    public bool IsWithNotes => Filter == NotesFilter.WithNotes;

    partial void OnFilterChanged(NotesFilter value)
    {
        OnPropertyChanged(nameof(IsAll));
        OnPropertyChanged(nameof(IsBookmarks));
        OnPropertyChanged(nameof(IsHighlights));
        OnPropertyChanged(nameof(IsWithNotes));
        Apply();
    }

    partial void OnSearchChanged(string value) => Apply();
    partial void OnByBookChanged(bool value) => Apply();

    [RelayCommand] private void ShowAll() => Filter = NotesFilter.All;
    [RelayCommand] private void ShowBookmarks() => Filter = NotesFilter.Bookmarks;
    [RelayCommand] private void ShowHighlights() => Filter = NotesFilter.Highlights;
    [RelayCommand] private void ShowWithNotes() => Filter = NotesFilter.WithNotes;

    /// <summary>Reloads everything from disk (cheap: one small file per book).</summary>
    public void Refresh()
    {
        _all = AppServices.Store.LoadAll()
            .Select(b => (Book: b, Set: AppServices.Store.LoadAnnotations(b.Id)))
            .Where(x => !x.Set.IsEmpty).ToList();
        _items = new List<GlobalNoteItem>();
        foreach (var (book, set) in _all)
        {
            string Title(int i) => i >= 0 && i < book.Chapters.Count ? book.Chapters[i].Title : "";
            string Where(int ch, double s) => s >= 0 ? $"{Title(ch)} · {AnnotationExport.Clock(s)}" : Title(ch);
            var cover = AppServices.Store.CoverPath(book);
            foreach (var b in set.Bookmarks)
                _items.Add(new GlobalNoteItem
                {
                    Id = b.Id, BookId = book.Id, BookTitle = book.Title, Author = book.Author, CoverColor = book.CoverColor, CoverPath = cover,
                    IsBookmark = true, ChapterIndex = b.ChapterIndex, Seconds = b.Seconds, Where = Where(b.ChapterIndex, b.Seconds),
                    Text = b.Excerpt, Note = b.Note, CreatedUtc = b.CreatedUtc,
                });
            foreach (var h in set.Highlights)
                _items.Add(new GlobalNoteItem
                {
                    Id = h.Id, BookId = book.Id, BookTitle = book.Title, Author = book.Author, CoverColor = book.CoverColor, CoverPath = cover,
                    ChapterIndex = h.ChapterIndex, Seconds = h.Seconds, Where = Where(h.ChapterIndex, h.Seconds),
                    Text = h.Text, Note = h.Note, Swatch = HighlightPalette.Swatch(h.Color), CreatedUtc = h.CreatedUtc,
                });
        }
        int bookmarks = _items.Count(i => i.IsBookmark), highlights = _items.Count - bookmarks, notes = _items.Count(i => i.HasNote);
        Summary = _items.Count == 0 ? ""
            : $"{bookmarks} {(bookmarks == 1 ? "bookmark" : "bookmarks")} · {highlights} {(highlights == 1 ? "highlight" : "highlights")} · {notes} {(notes == 1 ? "note" : "notes")} across {_all.Count} {(_all.Count == 1 ? "book" : "books")}";
        HasAny = _items.Count > 0;
        Apply();
    }

    private IEnumerable<GlobalNoteItem> Visible()
    {
        var q = Search.Trim();
        return _items.Where(i => Filter switch
            {
                NotesFilter.Bookmarks => i.IsBookmark,
                NotesFilter.Highlights => !i.IsBookmark,
                NotesFilter.WithNotes => i.HasNote,
                _ => true,
            })
            .Where(i => q.Length == 0 || i.Text.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Note.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || i.BookTitle.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Author.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || i.Where.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private void Apply()
    {
        var list = Visible();
        list = ByBook
            ? list.OrderBy(i => i.BookTitle, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.ChapterIndex).ThenBy(i => i.Seconds < 0 ? double.MaxValue : i.Seconds)
            : list.OrderByDescending(i => i.CreatedUtc);
        Items.Clear();
        foreach (var i in list) Items.Add(i);
        HasResults = Items.Count > 0;
        EmptyText = !HasAny
            ? "Nothing marked yet. While listening, press B to bookmark, H to highlight the line being read, or select text in the chapter to highlight it and add a note."
            : Search.Trim().Length > 0 ? $"Nothing matches “{Search.Trim()}”." : Filter switch
            {
                NotesFilter.Bookmarks => "No bookmarks yet.",
                NotesFilter.Highlights => "No highlights yet.",
                NotesFilter.WithNotes => "No notes yet. Add one to any bookmark or highlight with the pencil button.",
                _ => "",
            };
    }

    // ---------- actions ----------

    /// <summary>Plays from the bookmark or highlight, opening its book in the player.</summary>
    public async Task OpenAsync(GlobalNoteItem item)
    {
        await _shell.Player.PlayAtAsync(item.BookId, item.ChapterIndex, Math.Max(0, item.Seconds));
        if (!_shell.IsExpanded) _shell.GoListening();
    }

    public async Task EditNoteAsync(GlobalNoteItem item)
    {
        var set = AppServices.Store.LoadAnnotations(item.BookId);
        var h = set.Highlights.FirstOrDefault(x => x.Id == item.Id);
        var b = set.Bookmarks.FirstOrDefault(x => x.Id == item.Id);
        if (h is null && b is null) return;
        var note = await Dialogs.PromptAsync(h is not null ? "Note" : "Bookmark note", item.Text, item.Note, "Your note", "Save", multiline: true);
        if (note is null) return;
        if (h is not null) h.Note = note; else b!.Note = note;
        Save(item.BookId, set);
        Status = "Note saved.";
    }

    public void Delete(GlobalNoteItem item)
    {
        var set = AppServices.Store.LoadAnnotations(item.BookId);
        int removed = set.Bookmarks.RemoveAll(x => x.Id == item.Id) + set.Highlights.RemoveAll(x => x.Id == item.Id);
        if (removed == 0) return;
        Save(item.BookId, set);
        Status = item.IsBookmark ? "Bookmark deleted." : "Highlight removed.";
    }

    private void Save(string bookId, AnnotationSet set)
    {
        AppServices.Store.SaveAnnotations(bookId, set);
        _shell.Player.NotesChangedElsewhere(bookId);
        Refresh();
    }

    /// <summary>Exports what's showing (current filter and search), grouped by book.</summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        var visible = Visible().Select(i => i.Id).ToHashSet();
        var books = _all
            .Select(x => (x.Book, Set: new AnnotationSet
            {
                Bookmarks = x.Set.Bookmarks.Where(b => visible.Contains(b.Id)).ToList(),
                Highlights = x.Set.Highlights.Where(h => visible.Contains(h.Id)).ToList(),
            }))
            .Where(x => !x.Set.IsEmpty)
            .OrderBy(x => x.Book.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (books.Count == 0)
        {
            await Dialogs.InfoAsync("Nothing to export", "There are no bookmarks or highlights in the current view.");
            return;
        }
        var dest = await Pickers.PickSaveAsync("Bilgames Reader notes", ("Markdown", ".md"), ("Plain text", ".txt"), ("Spreadsheet (CSV)", ".csv"));
        if (dest is null) return;
        try
        {
            await Task.Run(() => File.WriteAllText(dest, AnnotationExport.WriteMany(books, AnnotationExport.FormatFor(dest)), new System.Text.UTF8Encoding(true)));
            int n = books.Sum(b => b.Set.Bookmarks.Count + b.Set.Highlights.Count);
            Status = $"Exported {n} {(n == 1 ? "item" : "items")} from {books.Count} {(books.Count == 1 ? "book" : "books")} to {Path.GetFileName(dest)}.";
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync("Export failed", ex.Message);
        }
    }
}
