using Bilgames.Core.Analysis;

namespace Bilgames.Core.Tests;

public class SpeechTagTests
{
    [Theory]
    [InlineData(", said Mrs. Bennet.", SubjectKind.Name, "Mrs. Bennet")]
    [InlineData("said the major.", SubjectKind.Descriptor, "The Major")]
    [InlineData("the lieutenant said.", SubjectKind.Descriptor, "The Lieutenant")]
    [InlineData("he said to me.", SubjectKind.Pronoun, "he")]
    [InlineData("I says.", SubjectKind.FirstPerson, "I")]
    [InlineData("says I.", SubjectKind.FirstPerson, "I")]
    [InlineData("cried his wife impatiently.", SubjectKind.Unknown, "his wife")]
    [InlineData("Elizabeth said quietly.", SubjectKind.Name, "Elizabeth")]
    [InlineData("said the old gentleman.", SubjectKind.Descriptor, "The Old Gentleman")]
    public void FindsTagsAfterQuotes(string narration, SubjectKind kind, string subject)
    {
        var tag = SpeechTags.FindAfter(narration);
        Assert.NotNull(tag);
        Assert.Equal(kind, tag!.Kind);
        Assert.Equal(subject, tag.Subject);
    }

    [Theory]
    [InlineData("Pretty soon Jim says:", SubjectKind.Name, "Jim")]
    [InlineData("By-and-by Jim says:", SubjectKind.Name, "Jim")]
    [InlineData("So I says:", SubjectKind.FirstPerson, "I")]
    [InlineData("He looked pretty uneasy, and didn’t say nothing for a minute. Then he says:", SubjectKind.Pronoun, "he")]
    public void FindsLeadInParagraphs(string paragraph, SubjectKind kind, string subject)
    {
        var tag = SpeechTags.FindLeadIn(paragraph);
        Assert.NotNull(tag);
        Assert.Equal(kind, tag!.Kind);
        Assert.Equal(subject, tag.Subject);
    }

    [Theory]
    [InlineData("Jim said it was a sign it was going to rain.")]
    [InlineData("The Ministry of Truth contained three thousand rooms.")]
    public void IgnoresReportedSpeech(string paragraph)
    {
        Assert.Null(SpeechTags.FindLeadIn(paragraph));
    }

    [Fact]
    public void LeadInWithActionBeatResolvesToPronoun()
    {
        var tag = SpeechTags.FindLeadIn("He bounced up and stared at me wild. Then he drops down on his knees, and puts his hands together and says:");
        // The subject of "says" is "he" (carried from the clause); at minimum it must not be read as an unnamed speaker.
        Assert.True(tag is not null && tag.Kind == SubjectKind.Pronoun && tag.Subject == "he", $"got {tag?.Kind} {tag?.Subject}");
    }

    [Theory]
    [InlineData("impatiently", "impatient")]
    [InlineData("angrily", "angry")]
    [InlineData("gently", "gentle")]
    [InlineData("terribly", "terrible")]
    public void AdverbRoots(string adverb, string root) => Assert.Equal(root, SpeechTags.AdverbRoot(adverb));
}

public class DescriptorTests
{
    [Theory]
    [InlineData("said the shrewish voice.")]
    [InlineData("said the other appreciatively.")]
    [InlineData("a voice said.")]
    public void UnnamedSpeakersAreUnknown(string narration)
    {
        var tag = SpeechTags.FindAfter(narration);
        Assert.NotNull(tag);
        Assert.Equal(SubjectKind.Unknown, tag!.Kind);
    }

    [Fact]
    public void DescriptorStopsBeforeParticiple()
    {
        var tag = SpeechTags.FindAfter("the captain said looking at the priest and at me.");
        Assert.Equal("The Captain", tag!.Subject);
    }
}
