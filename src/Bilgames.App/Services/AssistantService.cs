using Bilgames.Assist.Llama;
using Bilgames.Core.Assist;
using Bilgames.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bilgames.App.Services;

/// <summary>
/// The review assistant on this PC: installing it (one click, resumable), running llama.cpp only
/// while it's needed, and one background job at a time (reviewing a book's lines or reading the
/// whole book again). Screens bind to its state; all changes are raised on the UI thread.
/// </summary>
public sealed partial class AssistantService : ObservableObject, IAsyncDisposable
{
    private readonly AssistantDownloader _install;
    private readonly LocalAssistantModel _model;
    private readonly ReviewAssistant _assistant;
    private CancellationTokenSource? _downloadCts, _jobCts;

    public AssistantService()
    {
        var root = Path.Combine(AppServices.Store.Root, "assistant");
        _install = new AssistantDownloader(root);
        _model = new LocalAssistantModel(_install, () => AppServices.Settings.Assistant.Device ?? AppServices.Engine.Gpu?.Name,
            Path.Combine(AppServices.Store.Root, "logs", "assistant-server.log"));
        _model.StateChanged += s => AppServices.RunOnUI(() => ServerState = s);
        _assistant = new ReviewAssistant(AppServices.Store, AppServices.Books, _model,
            () => AppServices.Settings.Assistant.ApplyThreshold, () => AppServices.Engine.Voices);
        Refresh();
    }

    // ---------- install ----------

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Ready), nameof(CanStartJob))] private bool _installed;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Ready), nameof(CanStartJob))] private bool _enabled;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadPercent;
    [ObservableProperty] private string _downloadStatus = "";
    [ObservableProperty] private string _serverState = "";

    /// <summary>On, and the model is installed: the assistant's buttons show.</summary>
    public bool Ready => Enabled && Installed;

    public string ModelLabel => AssistantCatalog.Model.Label;
    public long ModelBytes => AssistantCatalog.Model.Size;
    public long PartialBytes => _install.PartialModelBytes;

    public void Refresh()
    {
        Installed = _install.Installed;
        Enabled = AppServices.Settings.Assistant.Enabled;
    }

    public void SetEnabled(bool on)
    {
        AppServices.Settings.Assistant.Enabled = on;
        AppServices.SaveSettings();
        Enabled = on;
        if (!on) { StopJob(); _ = _model.StopAsync(); }
    }

    /// <summary>Downloads llama.cpp and the model (the user's click is the go-ahead). Resumes a partial download.</summary>
    public async Task DownloadAsync()
    {
        if (IsDownloading || Installed) return;
        IsDownloading = true;
        DownloadStatus = "Starting…";
        using var cts = _downloadCts = new CancellationTokenSource();
        var progress = new Progress<DownloadProgress>(p =>
        {
            DownloadPercent = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
            DownloadStatus = p.Total > 0 ? $"{p.Stage} · {p.Done / 1e9:N1} of {p.Total / 1e9:N1} GB" : p.Stage;
        });
        try
        {
            await Task.Run(() => _install.InstallAsync(progress, cts.Token));
            DownloadStatus = "";
        }
        catch (OperationCanceledException) { DownloadStatus = $"Paused · {PartialBytes / 1e9:N1} of {ModelBytes / 1e9:N1} GB downloaded"; }
        catch (Exception ex)
        {
            AppServices.Log("Assistant download failed: " + ex);
            DownloadStatus = "Download failed: " + ex.Message;
        }
        finally
        {
            _downloadCts = null;
            IsDownloading = false;
            Refresh();
        }
    }

    public void PauseDownload() => _downloadCts?.Cancel();

    public async Task DeleteAsync()
    {
        StopJob();
        await _model.StopAsync();
        try { _install.DeleteAll(); } catch (Exception ex) { AppServices.Log("Removing the assistant failed: " + ex); }
        DownloadStatus = "";
        Refresh();
    }

    /// <summary>
    /// Whether this PC has enough memory (RAM plus the graphics card's own) for the model, and a line saying so.
    /// </summary>
    public (bool Enough, string Text) MemoryCheck()
    {
        long ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        long vram = AppServices.Engine.Gpu?.DedicatedBytes ?? 0;
        var have = $"This PC has {ram / 1e9:N0} GB of memory{(vram > 0 ? $" and {vram / 1e9:N0} GB on its graphics card" : "")}";
        return ram + vram >= AssistantCatalog.RecommendedMemoryBytes
            ? (true, have + ": enough.")
            : (false, have + $". The model needs about {AssistantCatalog.RecommendedMemoryBytes / 1e9:N0} GB in all, so it may not fit or may be very slow.");
    }

    // ---------- jobs ----------

    [ObservableProperty, NotifyPropertyChangedFor(nameof(JobPercent), nameof(JobIndeterminate))] private AssistantProgress? _progress;
    [ObservableProperty] private string? _jobBookId;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanStartJob))] private bool _isWorking;

    /// <summary>Ready, and not already busy: "Let the assistant review all" can start.</summary>
    public bool CanStartJob => Ready && !IsWorking;
    /// <summary>Loading the model or between steps: the progress bar just shows activity.</summary>
    public bool JobIndeterminate => Progress is not { Total: > 0 };
    [ObservableProperty] private string _jobStatus = "";

    public double JobPercent => Progress is { Total: > 0 } p ? 100.0 * p.Done / p.Total : 0;

    /// <summary>Raised on the UI thread when a job ends (finished, paused or failed), with the book id.</summary>
    public event Action<string>? JobEnded;

    /// <summary>Reviews every line waiting in Review for this book (resumes a paused review).</summary>
    public Task StartReviewAsync(string bookId) =>
        RunJobAsync(bookId, "Reviewing lines", (p, ct) => _assistant.RunReviewAsync(bookId, p, ct));

    /// <summary>Reads the whole book again (resumes a paused read), then suggests voices.</summary>
    public Task StartWholeBookAsync(string bookId, bool suggestVoices) =>
        RunJobAsync(bookId, "Reading the book", (p, ct) => _assistant.RunWholeBookAsync(bookId, suggestVoices, p, ct));

    public Task SuggestVoicesAsync(string bookId) =>
        RunJobAsync(bookId, "Choosing voices", async (_, ct) => await _assistant.SuggestVoicesAsync(bookId, ct));

    private async Task RunJobAsync(string bookId, string verb, Func<IProgress<AssistantProgress>, CancellationToken, Task> work)
    {
        if (IsWorking) return;
        IsWorking = true;
        JobBookId = bookId;
        Progress = null;
        JobStatus = verb + "…";
        using var cts = _jobCts = new CancellationTokenSource();
        var progress = new Progress<AssistantProgress>(p =>
        {
            Progress = p;
            JobStatus = $"{verb}: {p.Status}";
        });
        try
        {
            await Task.Run(() => work(progress, cts.Token));
            JobStatus = "";
        }
        catch (OperationCanceledException) { JobStatus = "Paused. Start again to pick up where it stopped."; }
        catch (AssistantUnavailableException ex) { JobStatus = ex.Message; AppServices.Log("Assistant: " + ex); }
        catch (Exception ex) { JobStatus = "The assistant stopped: " + ex.Message; AppServices.Log("Assistant job failed: " + ex); }
        finally
        {
            _jobCts = null;
            IsWorking = false;
            JobEnded?.Invoke(bookId);
        }
    }

    public void StopJob() => _jobCts?.Cancel();

    /// <summary>Asks about one line now (from the Review card). Waits its turn if a job is using the model.</summary>
    public Task<(AssistantOpinion Opinion, AssistantChange? Change)> ReviewLineAsync(string bookId, int chapter, string lineId, CancellationToken ct) =>
        Task.Run(() => _assistant.ReviewLineAsync(bookId, chapter, lineId, ct), ct);

    public async ValueTask DisposeAsync()
    {
        _jobCts?.Cancel();
        _downloadCts?.Cancel();
        await _model.DisposeAsync();
    }
}
