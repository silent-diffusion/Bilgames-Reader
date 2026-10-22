using System.ComponentModel;
using Bilgames.App.Controls;
using Bilgames.App.Services;
using Bilgames.Core;
using Bilgames.Core.Assist;
using Bilgames.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Bilgames.App.ViewModels;

/// <summary>The review assistant on a book: reading it again, the progress banner, undo, and voice suggestions.</summary>
public sealed partial class BookViewModel
{
    public AssistantService Assistant => AppServices.Assistant;

    /// <summary>The assistant is on and installed (its buttons show).</summary>
    public bool AssistantReady => Assistant.Ready;
    /// <summary>The assistant is working on this book right now (the banner shows).</summary>
    public bool AssistantHere => Assistant.IsWorking && Assistant.JobBookId == BookId;
    /// <summary>A run on this book was paused or failed: the banner offers to continue.</summary>
    [ObservableProperty] private string _assistantMessage = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasVoiceSuggestions))] private int _voiceSuggestionCount;
    public bool HasVoiceSuggestions => VoiceSuggestionCount > 0;

    private bool _assistantHooked;

    private void HookAssistant()
    {
        if (_assistantHooked) return;
        _assistantHooked = true;
        Assistant.PropertyChanged += OnAssistantChanged;
        Assistant.JobEnded += id => { if (id == BookId) _ = OnJobEndedAsync(id); };
    }

    private void OnAssistantChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AssistantService.IsWorking) or nameof(AssistantService.JobBookId))
        {
            OnPropertyChanged(nameof(AssistantHere));
            if (AssistantHere) AssistantMessage = ""; // the progress banner says it all while it works
        }
        if (e.PropertyName is nameof(AssistantService.Ready) or nameof(AssistantService.Enabled) or nameof(AssistantService.Installed)) OnPropertyChanged(nameof(AssistantReady));
    }

    /// <summary>Called when a book is shown: the banner's message and the voice suggestions waiting.</summary>
    private void RefreshAssistant()
    {
        HookAssistant();
        OnPropertyChanged(nameof(AssistantReady));
        OnPropertyChanged(nameof(AssistantHere));
        if (BookId is null) return;
        var log = AppServices.Store.LoadAssistantLog(BookId);
        VoiceSuggestionCount = log.VoiceSuggestions.Count(s => !s.Applied);
        AssistantMessage = log.Runs.LastOrDefault(r => r.State == AssistantRunState.Paused) is { } paused && !AssistantHere
            ? $"The assistant's {(paused.Kind == AssistantRunKind.WholeBook ? "read-through" : "review")} is paused at {paused.Done} of {paused.Total}."
            : "";
    }

    private async Task OnJobEndedAsync(string id)
    {
        Reload();
        _shell.BookChanged(id);
        RefreshAssistant();
        // Lines it settled leave Review: show the queue as it is now.
        Review.Reset();
        if (IsReview) Review.EnsureLoaded();
        var log = AppServices.Store.LoadAssistantLog(id);
        var run = log.Runs.LastOrDefault();
        if (run is null) return;
        if (run.State == AssistantRunState.Done)
        {
            var left = AppServices.Store.LoadBook(id)?.UnsureCount ?? 0;
            await Dialogs.InfoAsync(run.Kind == AssistantRunKind.WholeBook ? "The assistant has read the book" : "The assistant has reviewed the lines",
                $"It changed {Plural(run.Changes.Count, "line")}{(run.Suggested > 0 ? $" and left {Plural(run.Suggested, "suggestion")} for you in Review" : "")}. " +
                $"{(left > 0 ? $"{Plural(left, "line")} still need you in Review." : "Nothing is waiting in Review.")}" +
                (VoiceSuggestionCount > 0 ? $"\n\nIt also suggested voices for {Plural(VoiceSuggestionCount, "character")}; see the Cast tab." : "") +
                "\n\nAll of its changes can be undone from the ⋯ menu.");
        }
        else if (Assistant.JobStatus.Length > 0) AssistantMessage = Assistant.JobStatus;
    }

    private static string Plural(int n, string word) => $"{n:N0} {word}{(n == 1 ? "" : "s")}";

    /// <summary>
    /// Reads the whole book again with the assistant, using the current speakers as suggestions, then
    /// suggests voices. Long: it says roughly how long first. A paused read-through continues.
    /// </summary>
    [RelayCommand]
    private async Task AssistantReadBookAsync()
    {
        if (BookId is not { } id || Record is null || Assistant.IsWorking) return;
        var log = AppServices.Store.LoadAssistantLog(id);
        if (log.UnfinishedOf(AssistantRunKind.WholeBook) is { State: AssistantRunState.Paused })
        {
            await Assistant.StartWholeBookAsync(id, suggestVoices: true);
            return;
        }
        // Measured on a 6 GB laptop GPU with the experts in RAM: about two and a half minutes per passage
        // of ~7,000 characters, including the closer looks at hard lines. Bigger graphics cards are much faster.
        long chars = Record.Chapters.Where(c => BookService.Analyzable(Record, c)).Sum(c => (long)c.WordCount * 6);
        var minutes = Math.Max(5, chars / 7000.0 * 2.5);
        var estimate = minutes < 90 ? $"about {Math.Round(minutes / 5) * 5:N0} minutes" : $"about {minutes / 60:N0} hours";
        var choice = await Dialogs.ChooseAsync("Read the whole book with the assistant?",
            "The assistant reads the book again, a passage at a time, with the current speakers as suggestions. Where it's sure, it corrects who speaks (and how); " +
            "where it isn't, it leaves a suggestion in Review. Lines you've confirmed are never changed, and everything it changes can be undone.\n\n" +
            $"On this PC that takes {estimate}. You can keep using the app (it's best not to generate audio at the same time), and pause and continue whenever you like.",
            "Read and suggest voices", "Read only");
        if (choice == 0) return;
        AssistantMessage = "";
        await Assistant.StartWholeBookAsync(id, suggestVoices: choice == 1);
    }

    [RelayCommand]
    private async Task AssistantContinueAsync()
    {
        if (BookId is not { } id || Assistant.IsWorking) return;
        var log = AppServices.Store.LoadAssistantLog(id);
        AssistantMessage = "";
        // Continues the most recently paused run.
        if (log.Runs.LastOrDefault(r => r.State == AssistantRunState.Paused)?.Kind == AssistantRunKind.WholeBook) await Assistant.StartWholeBookAsync(id, suggestVoices: true);
        else await Assistant.StartReviewAsync(id);
    }

    [RelayCommand]
    private void AssistantPause() => Assistant.StopJob();

    /// <summary>Undoes the assistant's most recent run on this book (lines you've edited since are left alone).</summary>
    [RelayCommand]
    private async Task AssistantUndoAsync()
    {
        if (BookId is not { } id) return;
        if (Assistant.IsWorking && Assistant.JobBookId == id) { await Dialogs.InfoAsync("The assistant is working", "Pause it first, then undo."); return; }
        var log = AppServices.Store.LoadAssistantLog(id);
        var runs = log.Runs.Where(r => r.State != AssistantRunState.Undone && r.Changes.Count > 0).ToList();
        if (runs.Count == 0) { await Dialogs.InfoAsync("Nothing to undo", "The assistant hasn't changed any lines in this book."); return; }
        var last = runs[^1];
        var total = runs.Sum(r => r.Changes.Count);
        var what = last.Kind == AssistantRunKind.WholeBook ? "read-through" : "review";
        var pick = await Dialogs.ChooseAsync("Undo the assistant's changes?",
            $"Its last {what} ({last.StartedUtc.ToLocalTime():d MMM, HH:mm}) changed {Plural(last.Changes.Count, "line")}." +
            (runs.Count > 1 ? $" In all, it has changed {Plural(total, "line")} in this book." : "") +
            " Lines you've edited since stay as you left them.",
            $"Undo the last {what}", runs.Count > 1 ? "Undo everything" : "");
        if (pick == 0) return;
        int restored = 0;
        await Task.Run(() =>
        {
            foreach (var r in (pick == 1 ? new[] { last } : runs.AsEnumerable().Reverse()))
                restored += AppServices.Books.UndoAssistantRun(id, r.Id);
        });
        Reload();
        _shell.BookChanged(id);
        await Dialogs.InfoAsync("Undone", $"Restored {Plural(restored, "line")} to how they were.");
    }

    /// <summary>Shows the assistant's voice suggestions with reasons; the ticked ones are applied.</summary>
    [RelayCommand]
    private async Task AssistantVoicesAsync()
    {
        if (BookId is not { } id) return;
        var log = AppServices.Store.LoadAssistantLog(id);
        var cast = AppServices.Store.LoadCast(id);
        var pending = log.VoiceSuggestions.Where(s => !s.Applied && cast.Find(s.MemberId) is not null).ToList();
        if (pending.Count == 0) { VoiceSuggestionCount = 0; return; }

        var panel = new StackPanel { Spacing = 12, MaxWidth = 520 };
        panel.Children.Add(new TextBlock { Text = "Tick the voices to use. Each one can still be changed on the Cast tab.", TextWrapping = TextWrapping.Wrap, FontSize = 14 });
        var boxes = new List<(CheckBox Box, VoiceSuggestion S)>();
        foreach (var s in pending)
        {
            var member = cast.Find(s.MemberId)!;
            var box = new CheckBox
            {
                IsChecked = true,
                Content = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = $"{(member.IsNarrator ? "Narrator" : member.Name)}: {Bilgames.Core.Tts.VoiceResolver.NameOf(member.VoiceId, AppServices.Engine.Voices)} → {Bilgames.Core.Tts.VoiceResolver.NameOf(s.VoiceId, AppServices.Engine.Voices)}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = s.Reason, FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 },
                    },
                },
            };
            boxes.Add((box, s));
            panel.Children.Add(box);
        }
        var scroller = new ScrollViewer { Content = panel, MaxHeight = 460 };
        var result = await Dialogs.ShowContentAsync("Suggested voices", scroller, "Use ticked voices", "Not now");
        if (result != ContentDialogResult.Primary) return;
        var ids = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.S.MemberId).ToList();
        if (ids.Count == 0) return;
        await Task.Run(() => AppServices.Books.ApplyVoiceSuggestions(id, ids));
        Reload();
        _shell.BookChanged(id);
    }
}
