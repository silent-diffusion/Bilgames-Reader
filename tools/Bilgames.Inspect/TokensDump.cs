using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Bilgames.Core.Planning;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;

/// <summary>
/// <c>x tokens "text" [voice] [--speed 1.0] [--voices dir] [--out file.json]</c>: everything Kokoro's ONNX
/// model receives for one segment, as JSON, so it can be benchmarked elsewhere (e.g. Core ML on Apple
/// hardware) without porting the phonemizer. Needs no model file — only the voices folder.
/// <list type="bullet">
/// <item><c>speechText</c>: the text after SynthesisPlanner's speech clean-up (what generation sends).</item>
/// <item><c>phonemes</c>/<c>tokens</c>: KokoroSharp's Tokenizer, as KokoroEngine calls it (one token per phoneme char).</item>
/// <item><c>style</c>: the voice-pack row KokoroModel.Infer uses: <c>Features[n - 1, 0, :]</c> where n is the
/// token count (capped at 510). The model's <c>tokens</c> input is the token list padded with 0 at both ends;
/// <c>speed</c> is a 1-element float input.</item>
/// </list>
/// </summary>
internal static class TokensDump
{
    public static int Run(string[] args)
    {
        var positional = new List<string>();
        string? outFile = null, voicesDir = null;
        float speed = 1f;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out": outFile = args[++i]; break;
                case "--voices": voicesDir = args[++i]; break;
                case "--speed": speed = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                default: positional.Add(args[i]); break;
            }
        }
        if (positional.Count == 0)
        {
            Console.Error.WriteLine("usage: Bilgames.Inspect x tokens \"text\" [voice] [--speed 1.0] [--voices dir] [--out file.json]");
            return 2;
        }
        var text = positional[0];
        var voiceId = positional.Count > 1 ? positional[1] : Bilgames.Core.Tts.VoiceCatalog.DefaultNarrator;

        KokoroVoiceManager.LoadVoicesFromPath(voicesDir ?? Path.Combine(AppContext.BaseDirectory, "voices"));
        var voice = KokoroVoiceManager.GetVoice(voiceId);
        if (voice is null)
        {
            Console.Error.WriteLine($"voice '{voiceId}' not found");
            return 1;
        }

        // Same steps as KokoroEngine.SynthesizeAsync (the planner's clean-up includes its Apostrophes pass).
        var speechText = SynthesisPlanner.ForSpeechText(text);
        var lang = KokoroLangCodeHelper.GetLangCode(voice);
        var phonemes = Tokenizer.Phonemize(speechText, lang, true);
        var tokens = Tokenizer.Tokenize(speechText, lang, true);
        if (tokens.Length > KokoroModel.maxTokens - 10)
            Console.Error.WriteLine($"note: {tokens.Length} tokens; KokoroEngine would split this into several model calls (limit {KokoroModel.maxTokens - 10}).");

        var features = voice.Features;
        int row = Math.Min(Math.Max(tokens.Length, 1), features.GetLength(0)) - 1;
        var style = new float[features.GetLength(2)];
        for (int j = 0; j < style.Length; j++) style[j] = features[row, 0, j];

        var json = JsonSerializer.Serialize(new
        {
            text,
            speechText,
            phonemes,
            tokens,
            voice = voiceId,
            langCode = lang,
            styleIndex = row,
            style,
            speed,
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        if (outFile is null) Console.WriteLine(json);
        else
        {
            File.WriteAllText(outFile, json + "\n");
            Console.Error.WriteLine($"wrote {outFile}: {tokens.Length} tokens, style row {row}");
        }
        return 0;
    }
}
