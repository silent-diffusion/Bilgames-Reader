using Bilgames.Core;
using Bilgames.Core.Jobs;
using Bilgames.Core.Model;
using Bilgames.Core.Storage;
using Bilgames.Tts.Kokoro;
using Microsoft.UI.Dispatching;

namespace Bilgames.App.Services;

/// <summary>Composition root: one instance of each long-lived service for the app's lifetime.</summary>
public static class AppServices
{
    public static LibraryStore Store { get; private set; } = null!;
    public static BookService Books { get; private set; } = null!;
    public static KokoroEngine Engine { get; private set; } = null!;
    public static GenerationCoordinator Jobs { get; private set; } = null!;
    public static MediaFoundationEncoder Encoder { get; } = new();
    public static PowerMonitor Power { get; } = new();
    public static UpdateService Updates { get; private set; } = null!;
    public static ModelService Models { get; private set; } = null!;
    public static AppSettings Settings { get; private set; } = new();
    public static DispatcherQueue UI { get; private set; } = null!;

    public static void Initialize()
    {
        UI = DispatcherQueue.GetForCurrentThread();
        // BILGAMES_DATA lets developers point the app at a scratch library without touching real data.
        var root = Environment.GetEnvironmentVariable("BILGAMES_DATA") is { Length: > 0 } custom ? custom : LibraryStore.DefaultRoot;
        Store = new LibraryStore(root);
        Settings = Store.LoadSettings();
        Books = new BookService(Store);
        Engine = new KokoroEngine(Store.ModelsDir, Path.Combine(AppContext.BaseDirectory, "voices"), () => Settings.Processor);
        Models = new ModelService(Engine, Store.ModelsDir);
        Jobs = new GenerationCoordinator(Store, Engine, Encoder, Power, () => Settings);
        Updates = new UpdateService();
        Jobs.ResumeInterrupted();
    }

    public static void SaveSettings() => Store.SaveSettings(Settings);

    public static void RunOnUI(Action a)
    {
        if (UI.HasThreadAccess) a();
        else UI.TryEnqueue(() => a());
    }

    private static readonly object LogLock = new();

    /// <summary>Local diagnostic log. Never includes book text.</summary>
    public static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(Store?.Root ?? LibraryStore.DefaultRoot, "logs");
            Directory.CreateDirectory(dir);
            lock (LogLock) File.AppendAllText(Path.Combine(dir, "app.log"), $"{DateTime.Now:s} {message}{Environment.NewLine}");
        }
        catch { }
    }

    public static void Shutdown()
    {
        Jobs?.Dispose();
        Engine?.Dispose();
    }
}

/// <summary>Voice model install state, shared by Generate, Cast previews and Settings.</summary>
public sealed class ModelService
{
    private readonly KokoroEngine _engine;
    private readonly string _dir;
    private CancellationTokenSource? _cts;

    public ModelService(KokoroEngine engine, string dir)
    {
        _engine = engine;
        _dir = dir;
    }

    public event Action? Changed;

    public ModelInfo? Downloading { get; private set; }
    public double Progress { get; private set; }
    public string? Error { get; private set; }

    public bool IsInstalled(ModelInfo m) => _engine.IsInstalled(m);
    public bool IsInstalled(QualityProfile p) => _engine.IsInstalled(p);

    /// <summary>Downloads the model (the user's click is the go-ahead). Returns true once installed.</summary>
    public async Task<bool> RequestDownloadAsync(ModelInfo model)
    {
        if (IsInstalled(model)) return true;
        if (Downloading is not null) return false;

        Downloading = model;
        Progress = 0;
        Error = null;
        Changed?.Invoke();
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<(long Done, long Total)>(p =>
            {
                Progress = p.Total == 0 ? 0 : (double)p.Done / p.Total;
                Changed?.Invoke();
            });
            await Task.Run(() => ModelDownloader.DownloadAsync(model, _dir, progress, _cts.Token));
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Error = "Download failed: " + ex.Message;
            AppServices.Log("Model download failed: " + ex.Message);
            return false;
        }
        finally
        {
            Downloading = null;
            _cts?.Dispose();
            _cts = null;
            Changed?.Invoke();
        }
    }

    public void Cancel() => _cts?.Cancel();

    public void Delete(ModelInfo model)
    {
        if (model == KokoroModels.Gpu) _engine.ResetGpu();
        ModelDownloader.Delete(model, _dir);
        Changed?.Invoke();
    }
}
