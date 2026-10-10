// Golden-file conformance tests.
//
// Each public-domain book in tests/corpus/ is imported into a temporary LibraryStore, analyzed with
// BookService.Analyze (the app's own path: chapter text round-trips through JSON on disk) and planned
// with SynthesisPlanner using the cast the analysis assigns by default (narrator af_heart) and the
// standard Kokoro model id. The result is written as canonical JSON and compared with the fixtures in
// tests/Bilgames.Core.Tests/Golden/{book}/:
//
//   structure.json       book metadata; every chapter: index, kind, title, words, blocks, notes, analyzed
//   cast.json            analyzed speakers: id, name, gender, voice (ids are deterministic: ch0, ch1, ...)
//   analysis/{NNN}.json  per analyzed chapter: every line (id, block, dialogue, speaker id + name,
//                        confidence to 2 dp, source, cue, cueFrom, unsure, text)
//   plan/{NNN}.json      per analyzed chapter: the plan hash, then every segment in order (segment
//                        hash, block, line, voice, speed, gainDb, pauseAfterMs, speech text)
//   hashes.json          per analyzed chapter: plan hash and segment count (a quick overview)
//
// One record per line so a diff (and the failure message) points at a single line or segment.
// These fixtures are also the conformance spec for ports of Bilgames.Core (e.g. Swift).
//
// The same output is produced again under de-DE and tr-TR (comma decimals, dotted/dotless i) to prove
// nothing that feeds analysis, plans or hashes is culture-sensitive. Fixtures are read fresh in every
// process, so passing across runs also proves run-to-run stability (string hashing is randomized
// per process in .NET).
//
// To regenerate the fixtures after an intentional change, run:
//     $env:BILGAMES_UPDATE_GOLDEN = "1"; dotnet test tests/Bilgames.Core.Tests --filter GoldenFile
// (bash: BILGAMES_UPDATE_GOLDEN=1 dotnet test ...). Review the git diff before committing: a changed
// segment hash means users re-synthesize that audio.

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bilgames.Core.Model;
using Bilgames.Core.Planning;
using Bilgames.Core.Storage;
using Bilgames.Core.Tts;

namespace Bilgames.Core.Tests;

public static class Golden
{
    /// <summary>KokoroModels.Half.Id: the model Fast and Balanced quality use (Core can't reference the Kokoro project).</summary>
    public const string ModelId = "kokoro-v1-fp16";

    public static readonly string[] Books = { "alice", "christmas-carol", "sherlock-holmes", "frankenstein" };

    public static bool Updating => Environment.GetEnvironmentVariable("BILGAMES_UPDATE_GOLDEN") == "1";

    public static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "BilgamesReader.sln"))) return dir.FullName;
        throw new InvalidOperationException("Repository root (BilgamesReader.sln) not found above " + AppContext.BaseDirectory);
    }

    public static string CorpusPath(string book) => Path.Combine(RepoRoot, "tests", "corpus", book + ".epub");
    public static string FixtureDir(string book) => Path.Combine(RepoRoot, "tests", "Bilgames.Core.Tests", "Golden", book);

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep “quotes” and accents readable
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string J(object o) => JsonSerializer.Serialize(o, Json);

    /// <summary>{ header fields..., "key": [ one record per line ] }</summary>
    private static string Document(IEnumerable<(string Key, object Value)> header, string listKey, IEnumerable<object> records)
    {
        var sb = new StringBuilder("{\n");
        foreach (var (k, v) in header) sb.Append("  ").Append(J(k)).Append(": ").Append(J(v)).Append(",\n");
        sb.Append("  ").Append(J(listKey)).Append(": [");
        bool first = true;
        foreach (var r in records)
        {
            sb.Append(first ? "\n    " : ",\n    ").Append(J(r));
            first = false;
        }
        sb.Append(first ? "]\n}\n" : "\n  ]\n}\n");
        return sb.ToString();
    }

    private sealed record ChapterRow(int Index, SectionKind Kind, string Title, int Words, int Blocks, int Notes, bool Analyzed);
    private sealed record CastRow(string Id, string Name, Gender Gender, string Voice, int Lines);
    private sealed record LineRow(string Id, string Block, bool Dialogue, string SpeakerId, string Speaker, double Confidence,
        AttributionSource Source, string? Cue, CueSource CueFrom, bool Unsure, string Text);
    private sealed record SegmentRow(string Hash, string Block, string Line, string Voice, float Speed, float GainDb, int PauseAfterMs, string Text);
    private sealed record HashRow(int Chapter, string PlanHash, int Segments);

    /// <summary>Runs the pipeline on one corpus book; returns fixture-relative path → content.</summary>
    public static SortedDictionary<string, string> Produce(string book)
    {
        var root = Path.Combine(Path.GetTempPath(), "bilgames-golden-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LibraryStore(root);
            var svc = new BookService(store);
            var rec = store.Import(CorpusPath(book), VoiceCatalog.DefaultNarrator);
            rec = svc.Analyze(rec.Id);
            var cast = store.LoadCast(rec.Id);
            var files = new SortedDictionary<string, string>(StringComparer.Ordinal);

            var texts = rec.Chapters.Select(c => store.LoadChapterText(rec.Id, c.Index)).ToList();
            var analyses = rec.Chapters.ToDictionary(c => c.Index, c => store.LoadAnalysis(rec.Id, c.Index));

            files["structure.json"] = Document(
                new (string, object)[] { ("title", rec.Title), ("author", rec.Author), ("language", rec.Language) },
                "chapters",
                texts.Select(t => (object)new ChapterRow(t.Index, t.Kind, t.Title, t.WordCount, t.Blocks.Count, t.Notes.Count, analyses[t.Index] is not null)));

            files["cast.json"] = Document(Array.Empty<(string, object)>(), "members",
                cast.Members.Select(m => (object)new CastRow(m.Id, m.Name, m.Gender, m.VoiceId, m.LineCount)));

            string NameOf(string id) => cast.Find(id)?.Name ?? id;
            var hashes = new List<object>();
            foreach (var text in texts)
            {
                var analysis = analyses[text.Index];
                if (analysis is null) continue;
                var name = text.Index.ToString("D3", CultureInfo.InvariantCulture) + ".json";
                files["analysis/" + name] = Document(new (string, object)[] { ("chapter", text.Index) }, "lines",
                    analysis.Lines.Select(l => (object)new LineRow(l.Id, l.BlockId, l.IsDialogue, l.SpeakerId, NameOf(l.SpeakerId),
                        Math.Round(l.Confidence, 2, MidpointRounding.AwayFromZero), l.Source, l.Cue, l.CueFrom, l.Unsure, l.Text)));

                var plan = SynthesisPlanner.Plan(rec, text, analysis, cast, ModelId);
                files["plan/" + name] = Document(new (string, object)[] { ("chapter", text.Index), ("model", ModelId), ("planHash", plan.Hash) }, "segments",
                    plan.Segments.Select(s => (object)new SegmentRow(s.Hash, s.BlockId, s.LineId, s.VoiceId, s.Speed, s.GainDb, s.PauseAfterMs, s.Text)));
                hashes.Add(new HashRow(text.Index, plan.Hash, plan.Segments.Count));
            }
            files["hashes.json"] = Document(new (string, object)[] { ("model", ModelId) }, "chapters", hashes);
            return files;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public static Dictionary<string, string> ReadFixtures(string book)
    {
        var dir = FixtureDir(book);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(dir)) return result;
        foreach (var f in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories))
            result[Path.GetRelativePath(dir, f).Replace('\\', '/')] = File.ReadAllText(f, Encoding.UTF8).Replace("\r\n", "\n");
        return result;
    }

    public static void Write(string book, IReadOnlyDictionary<string, string> files)
    {
        var dir = FixtureDir(book);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        foreach (var (rel, content) in files)
        {
            var path = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }
    }

    /// <summary>Null when equal; otherwise a readable description of the first difference.</summary>
    public static string? FirstDifference(string book, IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string> actual)
    {
        foreach (var rel in expected.Keys.Union(actual.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!actual.TryGetValue(rel, out var a)) return $"{book}/{rel}: fixture exists but the pipeline produced no such file.";
            if (!expected.TryGetValue(rel, out var e)) return $"{book}/{rel}: produced by the pipeline but there is no fixture.";
            if (a == e) continue;
            var el = e.Split('\n');
            var al = a.Split('\n');
            for (int i = 0; i < Math.Max(el.Length, al.Length); i++)
            {
                var x = i < el.Length ? el[i] : "<end of file>";
                var y = i < al.Length ? al[i] : "<end of file>";
                if (x == y) continue;
                int col = 0;
                while (col < x.Length && col < y.Length && x[col] == y[col]) col++;
                return $"{book}/{rel} line {i + 1}, column {col + 1}:\n  expected: {Around(x, col)}\n  actual:   {Around(y, col)}";
            }
        }
        return null;
    }

    /// <summary>A long line cut to the part around the first difference.</summary>
    private static string Around(string s, int col)
    {
        const int before = 60, after = 140;
        if (s.Length <= before + after) return s;
        int start = Math.Max(0, col - before), end = Math.Min(s.Length, col + after);
        return (start > 0 ? "…" : "") + s[start..end] + (end < s.Length ? "…" : "");
    }

    public static void Check(string book, CultureInfo? culture)
    {
        var (prevCulture, prevUi) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        SortedDictionary<string, string> actual;
        try
        {
            if (culture is not null) { CultureInfo.CurrentCulture = culture; CultureInfo.CurrentUICulture = culture; }
            actual = Produce(book);
        }
        finally { CultureInfo.CurrentCulture = prevCulture; CultureInfo.CurrentUICulture = prevUi; }

        if (Updating && culture is not null) return; // fixtures are being rewritten by GoldenFileTests
        if (Updating && culture is null)
        {
            Write(book, actual);
            return;
        }
        var diff = FirstDifference(book, ReadFixtures(book), actual);
        if (diff is not null)
            Assert.Fail($"Golden mismatch{(culture is null ? "" : " under " + culture.Name)} — {diff}\n" +
                        "If the change is intended, regenerate with BILGAMES_UPDATE_GOLDEN=1 and review the diff (changed hashes mean re-synthesis).");
    }

    public static IEnumerable<object[]> BookData => Books.Select(b => new object[] { b });
}

// Separate classes so xUnit runs them in parallel (tests within one class run one after another).

public class GoldenFileTests
{
    [Theory]
    [MemberData(nameof(Golden.BookData), MemberType = typeof(Golden))]
    public void MatchesFixtures(string book) => Golden.Check(book, null);
}

public class GoldenFileGermanCultureTests
{
    [Theory]
    [MemberData(nameof(Golden.BookData), MemberType = typeof(Golden))]
    public void MatchesFixturesUnderGerman(string book) => Golden.Check(book, CultureInfo.GetCultureInfo("de-DE"));
}

public class GoldenFileTurkishCultureTests
{
    [Theory]
    [MemberData(nameof(Golden.BookData), MemberType = typeof(Golden))]
    public void MatchesFixturesUnderTurkish(string book) => Golden.Check(book, CultureInfo.GetCultureInfo("tr-TR"));
}
