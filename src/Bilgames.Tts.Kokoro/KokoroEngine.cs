using Bilgames.Core.Model;
using Bilgames.Core.Tts;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;

namespace Bilgames.Tts.Kokoro;

public sealed record ModelInfo(string Id, string FileName, string Url, long Size, string Label);

/// <summary>Kokoro-82M model files (Apache-2.0), hosted on the KokoroSharp binaries release.</summary>
public static class KokoroModels
{
    public static readonly ModelInfo Half = new(
        "kokoro-v1-fp16", "kokoro-fp16.onnx",
        "https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/kokoro-fp16.onnx",
        163_636_560, "Kokoro 82M · standard");

    public static readonly ModelInfo Full = new(
        "kokoro-v1-fp32", "kokoro.onnx",
        "https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/kokoro.onnx",
        325_508_342, "Kokoro 82M · full precision");

    public static IReadOnlyList<ModelInfo> All { get; } = new[] { Half, Full };

    /// <summary>Fast and Balanced share the standard model; High quality uses full precision.</summary>
    public static ModelInfo For(QualityProfile profile) => profile == QualityProfile.High ? Full : Half;
}

/// <summary>
/// Kokoro text-to-speech running fully on this device through ONNX Runtime. Text goes from
/// MisakiSharp (a managed phonemizer) to the model and back as PCM; nothing leaves the process.
/// </summary>
public sealed class KokoroEngine : ITtsEngine, IDisposable
{
    private readonly string _modelsDir;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private KokoroWavSynthesizer? _synth;
    private string? _loadedModel;
    private static bool _voicesLoaded;
    private static readonly object VoiceLock = new();

    public KokoroEngine(string modelsDir, string voicesDir)
    {
        _modelsDir = modelsDir;
        lock (VoiceLock)
        {
            if (!_voicesLoaded && Directory.Exists(voicesDir))
            {
                KokoroVoiceManager.LoadVoicesFromPath(voicesDir);
                _voicesLoaded = true;
            }
        }
    }

    public int SampleRate => 24_000;

    public string ModelIdFor(QualityProfile profile) => KokoroModels.For(profile).Id;

    public string ModelPath(ModelInfo m) => Path.Combine(_modelsDir, m.FileName);

    public bool IsInstalled(ModelInfo m)
    {
        var f = new FileInfo(ModelPath(m));
        return f.Exists && f.Length == m.Size;
    }

    public bool IsInstalled(QualityProfile profile) => IsInstalled(KokoroModels.For(profile));

    public async Task<short[]> SynthesizeAsync(string text, string voiceId, float speed, QualityProfile profile, CancellationToken ct)
    {
        var synth = await EnsureLoaded(KokoroModels.For(profile), ct);
        var voice = KokoroVoiceManager.GetVoice(voiceId) ?? KokoroVoiceManager.GetVoice(VoiceCatalog.DefaultNarrator);
        var config = new KokoroTTSPipelineConfig { Speed = speed };
        var task = synth.SynthesizeAsync(text, voice, config);
        // The synthesizer has no cancellation hook; stop waiting and let the job finish in the background.
        var bytes = await task.WaitAsync(ct);
        var samples = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2);
        return Trim(samples);
    }

    /// <summary>Drops leading/trailing near-silence so the planner's pauses are the only gaps.</summary>
    private static short[] Trim(short[] s)
    {
        const short threshold = 180;
        int start = 0, end = s.Length - 1;
        while (start < end && Math.Abs((int)s[start]) < threshold) start++;
        while (end > start && Math.Abs((int)s[end]) < threshold) end--;
        // Keep a few milliseconds of tail so consonants aren't clipped.
        start = Math.Max(0, start - 240);
        end = Math.Min(s.Length - 1, end + 1200);
        return start == 0 && end == s.Length - 1 ? s : s[start..(end + 1)];
    }

    private async Task<KokoroWavSynthesizer> EnsureLoaded(ModelInfo model, CancellationToken ct)
    {
        if (_synth is not null && _loadedModel == model.Id) return _synth;
        await _loadLock.WaitAsync(ct);
        try
        {
            if (_synth is not null && _loadedModel == model.Id) return _synth;
            if (!IsInstalled(model)) throw new InvalidOperationException($"{model.Label} isn't installed.");
            // Only one model in memory at a time.
            _synth?.Dispose();
            _synth = null;
            _synth = await Task.Run(() => KokoroWavSynthesizer.LoadModel(ModelPath(model)), ct);
            _loadedModel = model.Id;
            return _synth;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public void Dispose()
    {
        _synth?.Dispose();
        _loadLock.Dispose();
    }
}
