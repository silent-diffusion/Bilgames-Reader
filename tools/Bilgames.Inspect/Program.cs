using Bilgames.Core.Epub;
using Bilgames.Core.Model;

// Developer tool: prints what the importer extracts from an EPUB.
// Usage: Bilgames.Inspect <file.epub> [structure|text <chapter>]
Console.OutputEncoding = System.Text.Encoding.UTF8; // titles use “ ” · — and accented letters
var path = args[0];
var mode = args.Length > 1 ? args[1] : "structure";
if (mode == "tokens") { Environment.ExitCode = TokensDump.Run(args[2..]); return; } // x tokens "text" [voice] [--out file.json]
var book = mode is "m4b" or "engine" or "gpu" or "phonemes" ? new ImportedBook() : EpubImporter.Import(path);
await Task.CompletedTask;
Console.WriteLine($"{book.Title} — {book.Author} [{book.Language}] cover={(book.Cover?.Length ?? 0)} bytes");
if (mode == "structure")
{
    foreach (var c in book.Chapters)
        Console.WriteLine($"{c.Index,3} {c.Kind,-11} {c.WordCount,7:N0}w {c.Notes.Count,3}n  {c.Title}");
    Console.WriteLine($"total words {book.Chapters.Sum(c => c.WordCount):N0}");
}
else if (mode == "text")
{
    var ch = book.Chapters[int.Parse(args[2])];
    foreach (var b in ch.Blocks.Take(args.Length > 3 ? int.Parse(args[3]) : 40))
        Console.WriteLine($"[{b.Kind}] {b.Text}{(b.NoteRefs.Count > 0 ? "  {notes:" + string.Join(",", b.NoteRefs) + "}" : "")}\n");
    foreach (var n in ch.Notes.Take(5)) Console.WriteLine($"NOTE {n.Id}: {n.Text}");
}
else if (mode == "analyze")
{
    var body = book.Chapters.Where(c => c.Kind == SectionKind.Body).ToList();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var result = Bilgames.Core.Analysis.DialogueAnalyzer.Analyze(body);
    Console.WriteLine($"analyzed in {sw.ElapsedMilliseconds} ms");
    var all = result.Chapters.SelectMany(c => c.Lines).Where(l => l.IsDialogue).ToList();
    Console.WriteLine($"dialogue lines {all.Count}, unsure {all.Count(l => l.Unsure)}; by source: " +
        string.Join(", ", all.GroupBy(l => l.Source).Select(g => $"{g.Key}={g.Count()}")));
    foreach (var m in result.Cast.Members.Take(25))
        Console.WriteLine($"  {m.Id,-6} {m.Name,-28} {m.Gender,-7} {m.VoiceId,-12} lines={m.LineCount} conf={m.ConfidentCount} aliases={string.Join("/", m.Aliases)}");
    foreach (var s in result.Cast.Suggestions) Console.WriteLine($"  MERGE? {s.Reason}");
    string Name(string id) => result.Cast.Find(id)?.Name ?? id;
    int ci = args.Length > 2 ? int.Parse(args[2]) : 0;
    int take = args.Length > 3 ? int.Parse(args[3]) : 60;
    foreach (var l in result.Chapters[ci].Lines.Take(take))
    {
        var who = l.IsDialogue ? (l.Unsure ? $"?? {l.Confidence:F2} {l.Source} [{string.Join(", ", l.Candidates.Take(3).Select(c => $"{Name(c.SpeakerId)} {c.Score:P0}"))}]" : $"{Name(l.SpeakerId)} {l.Confidence:F2} {l.Source}") : "N";
        var text = l.Text.Length > 110 ? l.Text[..110] + "…" : l.Text;
        Console.WriteLine($"{who,-40} {(l.Cue is null ? "" : "(" + l.Cue + ") ")}{text}");
    }
}
else if (mode == "pipeline")
{
    // pipeline <epub> <storeRoot> <modelFile> <chapterOrdinal> <maxSegments>
    var root = args[2];
    var store = new Bilgames.Core.Storage.LibraryStore(root);
    var modelSrc = args[3];
    var modelDst = Path.Combine(store.ModelsDir, Path.GetFileName(modelSrc));
    if (!File.Exists(modelDst)) File.Copy(modelSrc, modelDst);
    var rec = store.Import(path, Bilgames.Core.Tts.VoiceCatalog.DefaultNarrator);
    var svc = new Bilgames.Core.BookService(store);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    rec = svc.Analyze(rec.Id);
    Console.WriteLine($"analyzed {sw.ElapsedMilliseconds} ms; unsure={rec.UnsureCount} voices={rec.VoiceCount}");
    var engine = new Bilgames.Tts.Kokoro.KokoroEngine(store.ModelsDir, Path.Combine(AppContext.BaseDirectory, "voices"), () => Bilgames.Core.Tts.ProcessorPreference.Gpu);
    var body = rec.Chapters.Where(c => c.Kind == SectionKind.Body).ToList();
    var info = body[int.Parse(args[4])];
    var cast = store.LoadCast(rec.Id);
    var plan = Bilgames.Core.Planning.SynthesisPlanner.Plan(rec, store.LoadChapterText(rec.Id, info.Index), store.LoadAnalysis(rec.Id, info.Index), cast, engine.ModelIdFor(rec.Quality));
    int max = int.Parse(args[5]);
    Console.WriteLine($"chapter '{info.Title}': {plan.Segments.Count} segments; synthesizing {Math.Min(max, plan.Segments.Count)}");
    var outWav = Path.Combine(root, "sample.wav");
    double audioSec = 0; sw.Restart();
    using (var cat = new Bilgames.Core.Audio.WavConcatenator(outWav, engine.SampleRate))
    {
        foreach (var seg in plan.Segments.Take(max))
        {
            var pcm = await engine.SynthesizeAsync(seg.Text, seg.VoiceId, seg.Speed, rec.Quality, CancellationToken.None);
            var p = Path.Combine(root, "seg.wav");
            Bilgames.Core.Audio.Wav.Write(p, pcm, engine.SampleRate);
            cat.AppendFile(p); cat.AppendSilence(seg.PauseAfterMs);
            audioSec += pcm.Length / 24000.0;
            Console.WriteLine($"  [{cast.Find(seg.SpeakerId)?.Name} / {seg.VoiceId} x{seg.Speed}] {(seg.Text.Length > 70 ? seg.Text[..70] + "…" : seg.Text)}");
        }
    }
    Console.WriteLine($"audio {audioSec:F1}s in {sw.Elapsed.TotalSeconds:F1}s → {audioSec / sw.Elapsed.TotalSeconds:F2}x realtime; wrote {outWav}");
}
else if (mode == "m4b")
{
    foreach (var c in Bilgames.Core.Audio.Mp4Metadata.ReadChapters(args[2])) Console.WriteLine($"{c.Start.TotalSeconds,8:F2}s  {c.Title}");
}

else if (mode == "phonemes")
{
    // phonemes <x> "text" : what the G2P makes of a text (apostrophes, numbers…)
    var asm = typeof(KokoroSharp.Processing.Tokenizer).Assembly;
    foreach (var m in typeof(KokoroSharp.Processing.Tokenizer).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        Console.WriteLine("  " + m);
    var phon = typeof(KokoroSharp.Processing.Tokenizer).GetMethod("Phonemize");
    foreach (var t in args.Skip(2))
    {
        var r = phon?.GetParameters().Length switch
        {
            1 => phon.Invoke(null, new object[] { t }),
            2 => phon.Invoke(null, new object[] { t, "en-us" }),
            _ => phon?.Invoke(null, new object?[] { t, "en-us", true }),
        };
        Console.WriteLine($"{t}  →  {r}");
    }
}
else if (mode == "gpu")
{
    // gpu <x> : which apps are using the GPU's memory
    var gpu = Bilgames.Tts.Kokoro.GpuMonitor.Best;
    Console.WriteLine($"GPU: {gpu?.Name ?? "none"}  free for this app: {(gpu is null ? 0 : Bilgames.Tts.Kokoro.GpuMonitor.AvailableBytes(gpu)) / 1048576:N0} MB");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    if (gpu is not null)
        foreach (var u in Bilgames.Tts.Kokoro.GpuMemoryUsage.Read(gpu, 20, 0))
            Console.WriteLine($"{u.Bytes / 1048576,8:N0} MB  {u.Name}{(u.Processes > 1 ? $" ({u.Processes} processes)" : "")}");
    Console.WriteLine($"({sw.ElapsedMilliseconds} ms)");
}
else if (mode == "engine")
{
    // engine <x> <modelsDir> <count> : synthesize repeatedly, report device per segment
    var engine = new Bilgames.Tts.Kokoro.KokoroEngine(args[2], Path.Combine(AppContext.BaseDirectory, "voices"), () => Bilgames.Core.Tts.ProcessorPreference.Gpu);
    Console.WriteLine($"GPU: {engine.Gpu?.Name ?? "none"} ({Bilgames.Tts.Kokoro.GpuMonitor.ProbeError}) model installed: {engine.GpuModelInstalled}");
    for (int i = 0; i < int.Parse(args[3]); i++)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var text = Environment.GetEnvironmentVariable("ENGINE_TEXT") ?? "The river ran behind us and the town had been captured very handsomely, but the mountains beyond it could not be taken.";
        var pcm = await engine.SynthesizeAsync(text, "bf_emma", 1f, QualityProfile.Balanced, CancellationToken.None);
        Console.WriteLine($"{i}: {sw.ElapsedMilliseconds,5} ms  {pcm.Length / 24000.0:F1}s  on {engine.DeviceLabel}  {engine.DeviceDetail}");
        if (args.Length > 4) await Task.Delay(int.Parse(args[4]));
        if (args.Length > 5 && i == 1) { engine.GpuBytesNeeded = long.Parse(args[5]) * 1048576; Console.WriteLine("   (GPU memory freed up)"); }
    }
}