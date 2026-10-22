using Bilgames.App.Services;
using Bilgames.Core;
using Bilgames.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bilgames.App.ViewModels;

/// <summary>Review with the assistant: ask about this line, or let it go through every line waiting here.</summary>
public sealed partial class ReviewViewModel
{
    public AssistantService Assistant => AppServices.Assistant;

    [ObservableProperty] private bool _isAsking;
    /// <summary>What the assistant thinks about the line on screen ("Alice · 72% sure: she's answering the Rabbit").</summary>
    [ObservableProperty] private string _assistantNote = "";
    private CancellationTokenSource? _askCts;

    /// <summary>Shows the assistant's earlier opinion on this line, and puts its pick first.</summary>
    private void ShowAssistantOpinion(ReviewItem item)
    {
        AssistantNote = "";
        if (item.Line.Assistant is not { } op) return;
        var who = op.NewName ?? _cast.Find(op.SpeakerId)?.Name;
        if (who is null) return;
        if (op.SpeakerId == CastMember.NarratorId) who = "the narrator";
        AssistantNote = $"The assistant thinks it's {who} ({op.Confidence:P0} sure){(op.Reason.Length > 0 ? ": " + op.Reason : ".")}";
        if (!string.IsNullOrEmpty(op.SpeakerId) && Options.FirstOrDefault(o => o.SpeakerId == op.SpeakerId) is { } pick)
        {
            foreach (var o in Options) o.IsSelected = o == pick;
        }
    }

    [RelayCommand]
    private async Task AskAssistantAsync()
    {
        if (_current is not { } item || _book.BookId is not { } id || IsAsking) return;
        IsAsking = true;
        AssistantNote = Assistant.Ready && !Assistant.IsWorking ? "Asking the assistant… (the first question loads the model, which takes a little longer)" : "Asking the assistant…";
        using var cts = _askCts = new CancellationTokenSource();
        try
        {
            var (opinion, change) = await Assistant.ReviewLineAsync(id, item.ChapterIndex, item.Line.Id, cts.Token);
            _book.Changed();
            if (change is { } c && (c.ToSpeaker != c.FromSpeaker || c.FromUnsure))
            {
                // Sure enough: it settled the line, so move on to the next one.
                var who = AppServices.Store.LoadCast(id).Find(c.ToSpeaker)?.Name ?? "someone";
                LoadQueue(Math.Max(0, item.Position - 1));
                Status = $"The assistant gave that line to {who} ({c.Confidence:P0} sure).";
                return;
            }
            // Not sure enough to change it: show its opinion here and let the reader decide.
            item.Line.Assistant = opinion;
            _cast = AppServices.Store.LoadCast(id);
            ShowAssistantOpinion(item);
            if (AssistantNote.Length == 0) AssistantNote = "The assistant couldn't tell who says this.";
        }
        catch (OperationCanceledException) { AssistantNote = ""; }
        catch (Exception ex)
        {
            AppServices.Log("Ask the assistant failed: " + ex);
            AssistantNote = ex.Message;
        }
        finally
        {
            _askCts = null;
            IsAsking = false;
        }
    }

    /// <summary>Lets the assistant go through every line waiting in Review (a paused run picks up where it stopped).</summary>
    [RelayCommand]
    private async Task AssistantReviewAllAsync()
    {
        if (_book.BookId is not { } id || Assistant.IsWorking) return;
        await Assistant.StartReviewAsync(id);
    }
}
