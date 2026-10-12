// Makes docs/intro/sample.wav (plus a .json of line start times) with the app's own engine, from
// public-domain lines of Alice's Adventures in Wonderland. The intro animation plays it.
// Usage (needs the installed app's voices and a downloaded model):
//   dotnet run --project tools/Bilgames.IntroSample -c Release -- docs/intro/sample.wav
using Bilgames.Core.Model;
using Bilgames.Core.Tts;
using Bilgames.Tts.Kokoro;

var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
var models = Path.Combine(local, "BilgamesReader", "models");
var voices = Path.Combine(local, "BilgamesReader", "current", "voices");
var engine = new KokoroEngine(models, voices, () => ProcessorPreference.Cpu);

// (voice, text, pauseAfterMs)
var lines = new (string voice, string text, int pause)[]
{
    ("af_heart", "Alice was beginning to get very tired of sitting by her sister on the bank, when suddenly a White Rabbit with pink eyes ran close by her.", 450),
    ("bf_emma", "Oh dear! Oh dear! I shall be too late!", 300),
    ("af_heart", "said the Rabbit. Down the rabbit-hole she went.", 450),
    ("af_bella", "Curiouser and curiouser!", 200),
    ("af_heart", "cried Alice.", 450),
    ("bm_george", "We're all mad here. I'm mad. You're mad.", 300),
    ("af_heart", "said the Cat, and vanished, grin last.", 450),
    ("am_fenrir", "Your hair wants cutting.", 300),
    ("af_heart", "said the Hatter.", 300),
    ("af_bella", "I wonder how many miles I've fallen by this time?", 400),
};

var pcm = new List<short>(); var starts = new List<int>();
foreach (var (voice, text, pause) in lines)
{
    starts.Add((int)(pcm.Count * 1000L / engine.SampleRate));
    Console.WriteLine($"{voice} {text}");
    pcm.AddRange(await engine.SynthesizeAsync(text, voice, 1f, QualityProfile.Balanced, CancellationToken.None));
    pcm.AddRange(new short[engine.SampleRate * pause / 1000]);
}
Console.WriteLine("device: " + engine.DeviceLabel);

var out_ = args.Length > 0 ? args[0] : "sample.wav";
File.WriteAllText(Path.ChangeExtension(out_, ".json"), "[" + string.Join(",", starts) + "]"); // line start times, pasted into bilgames-intro.html
using var bw = new BinaryWriter(File.Create(out_));
int sr = engine.SampleRate, bytes = pcm.Count * 2;
bw.Write("RIFF"u8); bw.Write(36 + bytes); bw.Write("WAVE"u8);
bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)1); bw.Write(sr); bw.Write(sr * 2); bw.Write((short)2); bw.Write((short)16);
bw.Write("data"u8); bw.Write(bytes);
foreach (var x in pcm) bw.Write(x);
Console.WriteLine($"wrote {out_}: {pcm.Count / (double)sr:0.0} s");
