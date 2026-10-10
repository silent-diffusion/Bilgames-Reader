using Bilgames.Core.Model;

namespace Bilgames.Core.Tts;

/// <summary>A local speech engine. Implementations must never send text anywhere.</summary>
public interface ITtsEngine
{
    int SampleRate { get; }

    /// <summary>Identifies the model a profile uses; part of every segment hash so changing models regenerates.</summary>
    string ModelIdFor(QualityProfile profile);

    bool IsInstalled(QualityProfile profile);

    /// <summary>Synthesizes one segment to 16-bit mono PCM at <see cref="SampleRate"/>.</summary>
    Task<short[]> SynthesizeAsync(string text, string voiceId, float speed, QualityProfile profile, CancellationToken ct);

    /// <summary>Where the last segment was computed, e.g. "GPU · NVIDIA GeForce RTX 3060" or "CPU".</summary>
    string DeviceLabel { get; }

    /// <summary>Raised (on a background thread) when processing moves between CPU and GPU.</summary>
    event Action? DeviceChanged;
}

public enum ProcessorPreference
{
    /// <summary>Use the GPU when it has room; fall back to the CPU and return when memory frees up.</summary>
    Gpu,
    Cpu,
}

/// <summary>Platform audio encoding (Media Foundation on Windows).</summary>
public interface IAudioEncoder
{
    /// <summary>WAV → AAC in an MPEG-4 container.</summary>
    Task EncodeAacAsync(string wavPath, string outputPath, CancellationToken ct);

    /// <summary>Any supported audio file → 16-bit mono WAV at the given sample rate.</summary>
    Task DecodeToWavAsync(string inputPath, string wavPath, int sampleRate, CancellationToken ct);
}

/// <summary>Power state, so long jobs can slow down on battery.</summary>
public interface IPowerMonitor
{
    bool OnBattery { get; }
    bool EnergySaver { get; }
}

public sealed class AlwaysPluggedIn : IPowerMonitor
{
    public bool OnBattery => false;
    public bool EnergySaver => false;
}
