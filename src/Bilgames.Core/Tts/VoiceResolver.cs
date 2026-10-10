using Bilgames.Core.Model;

namespace Bilgames.Core.Tts;

/// <summary>
/// Maps a saved voice to one the active engine has. Books keep the voice IDs they were given (they
/// are part of every segment hash, so they never change); when another engine is active — another
/// device, or a future engine — each voice is played by the closest one it offers: same gender
/// and accent, then same gender, then the engine's first (best) voice.
/// </summary>
public static class VoiceResolver
{
    /// <summary>The voice the engine should use for <paramref name="voiceId"/>; the same ID whenever it has it.</summary>
    public static string Resolve(string voiceId, IReadOnlyList<VoiceInfo> available, Gender fallbackGender = Gender.Unknown)
    {
        if (available.Count == 0 || available.Any(v => v.Id == voiceId)) return voiceId;
        var known = Describe(voiceId);
        var gender = known?.Gender ?? fallbackGender;
        var accent = known?.Accent;
        return (available.FirstOrDefault(v => v.Gender == gender && accent is not null && v.Accent == accent)
                ?? available.FirstOrDefault(v => gender != Gender.Unknown && v.Gender == gender)
                ?? available[0]).Id;
    }

    /// <summary>What a voice ID is (gender, accent, name), from any engine's catalog this build knows.</summary>
    public static VoiceInfo? Describe(string voiceId) => VoiceCatalog.All.FirstOrDefault(v => v.Id == voiceId);

    /// <summary>A display name for a voice, falling back to its ID for voices this build doesn't know.</summary>
    public static string NameOf(string voiceId, IReadOnlyList<VoiceInfo> available) =>
        available.FirstOrDefault(v => v.Id == voiceId)?.DisplayName ?? Describe(voiceId)?.DisplayName ?? voiceId;
}
