using Bilgames.App.Services;
using Bilgames.Core.Jobs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bilgames.App.ViewModels;

public enum LayoutClass
{
    Compact,
    Medium,
    Expanded,
}

public enum Section
{
    Library,
    Book,
    Player,
    Settings,
    Notes,
}

public enum BookTab
{
    Structure,
    Cast,
    Review,
    Generate,
}

/// <summary>
/// Navigation state (section, tab, selected book) and which panes the current window width shows.
/// Layout changes only change visibility, never this state, so resizing is lossless.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    public ShellViewModel()
    {
        Library = new LibraryViewModel(this);
        Book = new BookViewModel(this);
        Player = new PlayerViewModel(this);
        Settings = new SettingsViewModel(this);
        Notes = new NotesViewModel(this);
        AppServices.Jobs.ProgressChanged += p => AppServices.RunOnUI(() => OnProgress(p));
    }

    public LibraryViewModel Library { get; }
    public BookViewModel Book { get; }
    public PlayerViewModel Player { get; }
    public SettingsViewModel Settings { get; }
    public NotesViewModel Notes { get; }

    [ObservableProperty] private LayoutClass _layout = LayoutClass.Expanded;
    [ObservableProperty] private Section _section = Section.Library;

    public bool IsCompact => Layout == LayoutClass.Compact;
    public bool IsMedium => Layout == LayoutClass.Medium;
    public bool IsExpanded => Layout == LayoutClass.Expanded;

    public bool ShowLibrary => Layout switch
    {
        LayoutClass.Compact => Section == Section.Library,
        LayoutClass.Medium => true,
        _ => Section == Section.Library,
    };

    public bool ShowPlayer => Layout switch
    {
        LayoutClass.Compact => Section == Section.Player,
        LayoutClass.Medium => Section == Section.Player,
        _ => true,
    };

    /// <summary>Settings takes the Book pane's place (right pane in medium, center in expanded).</summary>
    public bool ShowSettings => Section == Section.Settings;

    /// <summary>The Notes page (all bookmarks, highlights and notes) takes the same place as Settings.</summary>
    public bool ShowNotesPage => Section == Section.Notes;

    public bool ShowBook => Layout switch
    {
        LayoutClass.Compact => Section == Section.Book,
        LayoutClass.Medium => Section is Section.Book or Section.Library,
        // Expanded: the center shows Library only in the Library section, otherwise the book.
        _ => Section is Section.Book or Section.Player,
    };

    public bool ShowMini => !IsExpanded && !ShowPlayer && Player.HasBook;

    // Nav highlight: Library / Listening / Voices (book, any tab but Generate) / Jobs (Generate).
    public bool NavLibrary => Section == Section.Library;
    public bool NavListening => Section == Section.Player;
    public bool NavVoices => Section == Section.Book && Book.Tab != BookTab.Generate;
    public bool NavJobs => Section == Section.Book && Book.Tab == BookTab.Generate;
    public bool NavSettings => Section == Section.Settings;
    public bool NavNotes => Section == Section.Notes;

    partial void OnLayoutChanged(LayoutClass value) => RaiseLayout();
    partial void OnSectionChanged(Section value) => RaiseLayout();

    public void RaiseLayout()
    {
        foreach (var name in new[]
                 {
                     nameof(IsCompact), nameof(IsMedium), nameof(IsExpanded), nameof(ShowLibrary), nameof(ShowPlayer), nameof(ShowBook),
                     nameof(ShowSettings), nameof(ShowMini), nameof(NavLibrary), nameof(NavListening), nameof(NavVoices), nameof(NavJobs),
                     nameof(NavSettings), nameof(NavNotes), nameof(ShowNotesPage),
                 })
            OnPropertyChanged(name);
        UpdateSelection();
        LayoutChanged?.Invoke();
    }

    /// <summary>Raised after any visibility change so the window can re-arrange its grid.</summary>
    public event Action? LayoutChanged;

    private void UpdateSelection()
    {
        foreach (var b in Library.Books)
            b.IsSelected = IsMedium && ShowBook && Book.BookId == b.Id;
    }

    // ---------- navigation ----------

    [RelayCommand] public void GoLibrary() => Section = Section.Library;

    [RelayCommand]
    public void GoListening()
    {
        if (!Player.HasBook && Library.ContinueItem is { } c) Player.Load(c.Id);
        // Wide windows keep the playing book's details beside the player.
        if (Player.BookId is { } id && Book.BookId != id) Book.Load(id);
        Section = Section.Player;
    }

    [RelayCommand] public void GoVoices() => GoBookTab(BookTab.Cast);
    [RelayCommand] public void GoJobs() => GoBookTab(BookTab.Generate);
    [RelayCommand] public void GoSettings() => Section = Section.Settings;

    [RelayCommand]
    public void GoNotes()
    {
        Notes.Refresh();
        Section = Section.Notes;
    }

    private void GoBookTab(BookTab tab)
    {
        if (Book.BookId is null)
        {
            var target = AppServices.Jobs.ActiveBookId ?? Player.BookId ?? Library.Books.FirstOrDefault()?.Id;
            if (target is not null) Book.Load(target);
        }
        Book.Tab = tab;
        Section = Section.Book;
        RaiseLayout();
    }

    public void OpenBook(string id, BookTab? tab = null)
    {
        if (Book.BookId != id) Book.Load(id);
        if (tab is not null) Book.Tab = tab.Value;
        Section = Section.Book;
        RaiseLayout();
    }

    public void OpenPlayer(string? bookId = null, bool autoplay = false)
    {
        if (bookId is not null && (Player.BookId != bookId || (autoplay && !Player.HasAudio))) Player.Load(bookId, autoplay);
        GoListening();
    }

    /// <summary>"Fix" in the player: jump to correcting that line's speaker.</summary>
    public void FixLine(string bookId, int chapterIndex, string lineId)
    {
        OpenBook(bookId, BookTab.Review);
        Book.Review.FocusLine(chapterIndex, lineId);
    }

    public void BookChanged(string id)
    {
        Library.Refresh(id);
        if (Book.BookId == id) Book.Reload();
        UpdateSelection();
    }

    private readonly Dictionary<string, int> _readySeen = new();

    private void OnProgress(GenerationProgress p)
    {
        Library.Refresh(p.BookId, p);
        if (Book.BookId == p.BookId) Book.Generate.Apply(p);
        // Only when a chapter finished (or the job stopped): the player may switch from streaming to the saved file.
        if (Player.BookId == p.BookId && (_readySeen.GetValueOrDefault(p.BookId, -1) != p.ReadyChapters || p.State != Bilgames.Core.Model.JobState.Running))
        {
            _readySeen[p.BookId] = p.ReadyChapters;
            Player.OnChaptersChanged();
        }
        UpdateSelection();
    }

    public void Start()
    {
        Library.Load();
        var s = AppServices.Settings;
        if (s.LastBookId is { } last && Library.Find(last) is not null)
        {
            Book.Load(last);
            Player.Load(last, autoplay: false);
        }
        else if (Library.ContinueItem is { } c)
        {
            Player.Load(c.Id, autoplay: false);
        }
        RaiseLayout();
    }

    public void Remember()
    {
        AppServices.Settings.LastBookId = Player.BookId ?? Book.BookId;
        AppServices.Settings.LastSection = Section.ToString();
        AppServices.SaveSettings();
    }
}
