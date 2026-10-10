using Bilgames.Core.Model;
using Bilgames.Core.Planning;
using Bilgames.Core.Tts;

namespace Bilgames.Core.Tests;

public class VoiceResolverTests
{
    // A hypothetical other engine with a small, different catalog.
    private static readonly IReadOnlyList<VoiceInfo> Other = new[]
    {
        new VoiceInfo("x_f_us", "Ava", Gender.Female, "American", "A"),
        new VoiceInfo("x_m_us", "Sam", Gender.Male, "American", "A"),
        new VoiceInfo("x_m_gb", "Oliver", Gender.Male, "British", "A"),
    };

    [Fact]
    public void KokoroVoicesStayAsTheyAre()
    {
        foreach (var v in VoiceCatalog.All) Assert.Equal(v.Id, VoiceResolver.Resolve(v.Id, VoiceCatalog.All));
    }

    [Fact]
    public void MissingVoiceGetsClosestStandIn()
    {
        Assert.Equal("x_m_gb", VoiceResolver.Resolve("bm_george", Other)); // male, British
        Assert.Equal("x_m_us", VoiceResolver.Resolve("am_adam", Other));   // male, American
        Assert.Equal("x_f_us", VoiceResolver.Resolve("bf_emma", Other));   // female; no British female, so any female
        Assert.Equal("x_m_us", VoiceResolver.Resolve("unknown_voice", Other, Gender.Male));
        Assert.Equal("x_f_us", VoiceResolver.Resolve("unknown_voice", Other));
    }

    [Fact]
    public void PlanKeepsHashesForTheSameEngine()
    {
        var book = new BookRecord { Id = "bk", Title = "T" };
        var text = new ChapterText { Index = 0, Title = "One", Kind = SectionKind.Body, Blocks = { new Block { Id = "c0b0", Kind = BlockKind.Paragraph, Text = "It was a dark night." } } };
        var cast = new CastSheet { Members = { new CastMember { Id = CastMember.NarratorId, Name = "Narrator", VoiceId = "bm_george" } } };

        var asSaved = SynthesisPlanner.Plan(book, text, null, cast, "m");
        var withKokoro = SynthesisPlanner.Plan(book, text, null, cast, "m", VoiceCatalog.All);
        var withOther = SynthesisPlanner.Plan(book, text, null, cast, "m", Other);

        Assert.Equal(asSaved.Segments.Select(s => s.Hash), withKokoro.Segments.Select(s => s.Hash));
        Assert.All(withOther.Segments, s => Assert.Equal("x_m_gb", s.VoiceId));
        Assert.Equal("bm_george", cast.Narrator.VoiceId); // the book keeps its voice
    }
}
