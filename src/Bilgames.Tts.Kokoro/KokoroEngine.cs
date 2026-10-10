using Bilgames.Core.Model;
using Bilgames.Core.Tts;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using Microsoft.ML.OnnxRuntime;

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

    /// <summary>The GPU always runs the full-precision model (half precision overflows on DirectML).</summary>
    public static ModelInfo Gpu => Full;
}

/// <summary>
/// Kokoro text-to-speech running fully on this device through ONNX Runtime. Inference runs on the
/// GPU (DirectML) when preferred and there is video memory for it; otherwise on the CPU. A GPU
/// error or memory shortage falls back to the CPU for that segment, and while on the CPU the engine
/// re-checks GPU memory every <see cref="GpuRetryInterval"/> and moves back between segments, so
/// a generation job never stops.
/// </summary>
public sealed class KokoroEngine : ITtsEngine, IDisposable
{
    public static readonly TimeSpan GpuRetryInterval = TimeSpan.FromSeconds(20);

    private readonly string _modelsDir;
    private readonly Func<ProcessorPreference> _preference;
    private readonly object _cpuLoad = new();
    private readonly Dictionary<string, KokoroModel> _cpu = new();
    private readonly SemaphoreSlim _cpuRun = new(1, 1);
    private readonly SemaphoreSlim _gpuRun = new(1, 1);
    private KokoroModel? _gpu;
    private DateTime _gpuRetryAt = DateTime.MinValue;
    private long _gpuNeeded = 1_400L * 1024 * 1024; // refined after the first GPU run
    private readonly bool _fixedNeed;
    private string _deviceLabel = "CPU";
    private string _deviceDetail = "";
    private static bool _voicesLoaded;
    private static readonly object VoiceLock = new();

    public KokoroEngine(string modelsDir, string voicesDir, Func<ProcessorPreference> preference)
    {
        _modelsDir = modelsDir;
        _preference = preference;
        // Developer knob for exercising the CPU fallback: pretend the GPU needs this much memory.
        if (long.TryParse(Environment.GetEnvironmentVariable("BILGAMES_GPU_NEED_MB"), out var mb)) { _gpuNeeded = mb * 1024 * 1024; _fixedNeed = true; }
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
    public string DeviceLabel => _deviceLabel;
    /// <summary>Why the engine is where it is, e.g. "GPU memory is in use — will switch back when it frees up".</summary>
    public string DeviceDetail => _deviceDetail;
    public event Action? DeviceChanged;

    /// <summary>Video memory the GPU session needs (learned after the first GPU run).</summary>
    public long GpuBytesNeeded { get => _gpuNeeded; set => _gpuNeeded = value; }

    public GpuInfo? Gpu => GpuMonitor.Best;
    public bool GpuModelInstalled => IsInstalled(KokoroModels.Gpu);

    public string ModelIdFor(QualityProfile profile) => KokoroModels.For(profile).Id;
    public string ModelPath(ModelInfo m) => Path.Combine(_modelsDir, m.FileName);
    private string GpuModelPath => Path.Combine(_modelsDir, "kokoro.directml.onnx");

    public bool IsInstalled(ModelInfo m)
    {
        var f = new FileInfo(ModelPath(m));
        return f.Exists && f.Length == m.Size;
    }

    public bool IsInstalled(QualityProfile profile) => IsInstalled(KokoroModels.For(profile));

    public async Task<short[]> SynthesizeAsync(string text, string voiceId, float speed, QualityProfile profile, CancellationToken ct)
    {
        var voice = KokoroVoiceManager.GetVoice(voiceId) ?? KokoroVoiceManager.GetVoice(VoiceCatalog.DefaultNarrator);
        // Plans arrive normalized already; previews and other callers may not.
        text = Bilgames.Core.Planning.SynthesisPlanner.Apostrophes(text);
        var tokens = Tokenizer.Tokenize(text, KokoroLangCodeHelper.GetLangCode(voice), true);
        if (tokens.Length == 0) return Array.Empty<short>();
        var chunks = tokens.Length <= KokoroModel.maxTokens - 10
            ? new List<int[]> { tokens }
            : SegmentationSystem.SplitToSegments(tokens, new DefaultSegmentationConfig());

        var samples = new List<float>();
        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            samples.AddRange(await Task.Run(() => Infer(chunk, voice.Features, speed, profile, ct), ct));
        }
        return Trim(ToPcm(samples));
    }

    private async Task<float[]> Infer(int[] tokens, float[,,] style, float speed, QualityProfile profile, CancellationToken ct)
    {
        if (await TryGpu(ct) is { } gpu)
        {
            await _gpuRun.WaitAsync(ct);
            try
            {
                var result = gpu.Infer(tokens, style, speed);
                if (result.Length == 0 || result.Any(float.IsNaN)) throw new InvalidOperationException("the GPU returned invalid audio");
                LearnGpuFootprint();
                SetDevice($"GPU · {Gpu!.Name}", "");
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DropGpu($"The GPU couldn't continue ({Short(ex.Message)}); using the CPU and retrying the GPU shortly.");
            }
            finally
            {
                _gpuRun.Release();
            }
        }

        var cpu = CpuModel(KokoroModels.For(profile));
        await _cpuRun.WaitAsync(ct);
        try
        {
            var result = cpu.Infer(tokens, style, speed);
            if (_deviceLabel != "CPU" || _deviceDetail != PendingDetail) SetDevice("CPU", PendingDetail);
            return result;
        }
        finally
        {
            _cpuRun.Release();
        }
    }

    private string PendingDetail { get; set; } = "";

    /// <summary>Returns the GPU session when the GPU should be used for the next segment.</summary>
    private async Task<KokoroModel?> TryGpu(CancellationToken ct)
    {
        if (_preference() != ProcessorPreference.Gpu)
        {
            if (_gpu is not null) DropGpu("");
            PendingDetail = "";
            ClearGpuMemoryUsers();
            return null;
        }
        var gpu = Gpu;
        if (gpu is null) { PendingDetail = "No compatible GPU found."; return null; }
        if (!GpuModelInstalled) { PendingDetail = "GPU acceleration needs the full-precision model."; return null; }
        if (_gpu is not null) return _gpu;
        if (DateTime.UtcNow < _gpuRetryAt) return null;

        // Only load onto the GPU when the memory is actually there.
        long available = GpuMonitor.AvailableBytes(gpu);
        if (available < _gpuNeeded)
        {
            _gpuRetryAt = DateTime.UtcNow + GpuRetryInterval;
            GpuFreeBytes = Math.Max(0, available);
            PendingDetail = $"{gpu.Name} is short on memory ({GpuFreeBytes / 1048576:N0} MB free); using the CPU and checking again shortly.";
            ScanGpuMemoryUsers(gpu);
            return null;
        }

        await _gpuRun.WaitAsync(ct);
        try
        {
            if (_gpu is not null) return _gpu;
            if (!File.Exists(GpuModelPath) || File.GetLastWriteTimeUtc(GpuModelPath) < File.GetLastWriteTimeUtc(ModelPath(KokoroModels.Gpu)))
                ModelPatcher.PatchForDirectML(ModelPath(KokoroModels.Gpu), GpuModelPath);
            var options = new SessionOptions();
            options.AppendExecutionProvider_DML(gpu.AdapterIndex);
            _gpu = new KokoroModel(GpuModelPath, options);
            PendingDetail = "";
            ClearGpuMemoryUsers();
            return _gpu;
        }
        catch (Exception ex)
        {
            _gpu = null;
            _gpuRetryAt = DateTime.UtcNow + GpuRetryInterval;
            PendingDetail = $"Couldn't start on the GPU ({Short(ex.Message)}); using the CPU.";
            return null;
        }
        finally
        {
            _gpuRun.Release();
        }
    }

    /// <summary>True while the GPU is preferred but too full, so work runs on the CPU.</summary>
    public bool GpuMemoryShort { get; private set; }

    /// <summary>Video memory this app could use when last checked (while <see cref="GpuMemoryShort"/>).</summary>
    public long GpuFreeBytes { get; private set; }

    /// <summary>The apps holding the GPU's memory when it was last found too full (largest first).</summary>
    public IReadOnlyList<GpuMemoryUser> GpuMemoryUsers { get; private set; } = Array.Empty<GpuMemoryUser>();

    private int _scanning;

    /// <summary>Looks up who is using the GPU's memory in the background, then tells listeners.</summary>
    private void ScanGpuMemoryUsers(GpuInfo gpu)
    {
        GpuMemoryShort = true;
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return;
        _ = Task.Run(() =>
        {
            try { GpuMemoryUsers = GpuMemoryUsage.Read(gpu); }
            catch { GpuMemoryUsers = Array.Empty<GpuMemoryUser>(); }
            finally { Interlocked.Exchange(ref _scanning, 0); }
            if (GpuMemoryShort) DeviceChanged?.Invoke();
        });
    }

    private void ClearGpuMemoryUsers()
    {
        bool had = GpuMemoryShort;
        GpuMemoryShort = false;
        GpuMemoryUsers = Array.Empty<GpuMemoryUser>();
        if (had) DeviceChanged?.Invoke();
    }

    private int _footprintSamples;

    private void LearnGpuFootprint()
    {
        // The footprint settles after a few runs; no need to query the driver on every segment.
        if (Gpu is null || _fixedNeed || ++_footprintSamples > 5) return;
        long used = GpuMonitor.UsedBytes(Gpu);
        if (used > 0) _gpuNeeded = Math.Max(512L * 1024 * 1024, (long)(used * 1.15));
    }

    private void DropGpu(string detail)
    {
        try { _gpu?.Dispose(); } catch { }
        _gpu = null;
        _gpuRetryAt = DateTime.UtcNow + GpuRetryInterval;
        PendingDetail = detail;
    }

    private KokoroModel CpuModel(ModelInfo model)
    {
        lock (_cpuLoad)
        {
            if (_cpu.TryGetValue(model.Id, out var m)) return m;
            if (!IsInstalled(model)) throw new InvalidOperationException($"{model.Label} isn't installed.");
            m = new KokoroModel(ModelPath(model), new SessionOptions());
            _cpu[model.Id] = m;
            return m;
        }
    }

    private void SetDevice(string label, string detail)
    {
        bool changed = label != _deviceLabel || detail != _deviceDetail;
        _deviceLabel = label;
        _deviceDetail = detail;
        if (changed) DeviceChanged?.Invoke();
    }

    /// <summary>Releases the GPU session (e.g. after the GPU model is deleted).</summary>
    public void ResetGpu()
    {
        DropGpu("");
        _gpuRetryAt = DateTime.MinValue;
        try { if (File.Exists(GpuModelPath)) File.Delete(GpuModelPath); } catch { }
    }

    private static string Short(string message)
    {
        var line = message.Split('\n')[0];
        return line.Length > 90 ? line[..90] + "…" : line;
    }

    private static short[] ToPcm(List<float> samples)
    {
        var pcm = new short[samples.Count];
        for (int i = 0; i < pcm.Length; i++)
            pcm[i] = (short)Math.Clamp(samples[i] * 32767f, short.MinValue, short.MaxValue);
        return pcm;
    }

    /// <summary>Drops leading/trailing near-silence so the planner's pauses are the only gaps.</summary>
    private static short[] Trim(short[] s)
    {
        if (s.Length == 0) return s;
        const short threshold = 180;
        int start = 0, end = s.Length - 1;
        while (start < end && Math.Abs((int)s[start]) < threshold) start++;
        while (end > start && Math.Abs((int)s[end]) < threshold) end--;
        // Keep a few milliseconds of tail so consonants aren't clipped.
        start = Math.Max(0, start - 240);
        end = Math.Min(s.Length - 1, end + 1200);
        return start == 0 && end == s.Length - 1 ? s : s[start..(end + 1)];
    }

    public void Dispose()
    {
        _gpu?.Dispose();
        foreach (var m in _cpu.Values) m.Dispose();
        _cpuRun.Dispose();
        _gpuRun.Dispose();
    }
}
