using Bilgames.Core.Tts;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Bilgames.App.Services;

/// <summary>
/// AAC encoding/decoding through Windows' built-in Media Foundation codecs, so the app ships no
/// codec of its own and needs no extra licensing.
/// </summary>
public sealed class MediaFoundationEncoder : IAudioEncoder
{
    public async Task EncodeAacAsync(string wavPath, string outputPath, CancellationToken ct)
    {
        // Mono speech at 64 kbps is transparent for narration and keeps books small (~29 MB/hour).
        var profile = MediaEncodingProfile.CreateM4a(AudioEncodingQuality.Medium);
        profile.Audio = AudioEncodingProperties.CreateAac(44100, 1, 64000);
        await Transcode(wavPath, outputPath, profile, ct);
    }

    public async Task DecodeToWavAsync(string inputPath, string wavPath, int sampleRate, CancellationToken ct)
    {
        var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
        profile.Audio = AudioEncodingProperties.CreatePcm((uint)sampleRate, 1, 16);
        await Transcode(inputPath, wavPath, profile, ct);
    }

    private static async Task Transcode(string input, string output, MediaEncodingProfile profile, CancellationToken ct)
    {
        var src = await StorageFile.GetFileFromPathAsync(input);
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(output)!);
        var dst = await folder.CreateFileAsync(Path.GetFileName(output), CreationCollisionOption.ReplaceExisting);
        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
        var prepared = await transcoder.PrepareFileTranscodeAsync(src, dst, profile);
        if (!prepared.CanTranscode)
            throw new InvalidOperationException($"Windows couldn't encode this audio ({prepared.FailureReason}).");
        await prepared.TranscodeAsync().AsTask(ct);
    }
}

/// <summary>Battery / energy-saver state from Windows.</summary>
public sealed class PowerMonitor : IPowerMonitor
{
    public bool OnBattery
    {
        get
        {
            try
            {
                return Windows.System.Power.PowerManager.PowerSupplyStatus == Windows.System.Power.PowerSupplyStatus.NotPresent
                       && Windows.System.Power.PowerManager.BatteryStatus != Windows.System.Power.BatteryStatus.NotPresent;
            }
            catch { return false; }
        }
    }

    public bool EnergySaver
    {
        get
        {
            try { return Windows.System.Power.PowerManager.EnergySaverStatus == Windows.System.Power.EnergySaverStatus.On; }
            catch { return false; }
        }
    }
}
