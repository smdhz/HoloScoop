using HoloScoop.Services.Media;
using Xunit;

namespace HoloScoop.Tests;

public sealed class SubtitleCanonicalizerTests
{
    private readonly SubtitleCanonicalizer _canonicalizer = new();

    [Fact]
    public void Normalize_CollapsesAdjacentRollingDuplicatesAndNoise()
    {
        var cues = new ParsedSubtitleCue[]
        {
            new(0, 0, 1_000, "[music]"),
            new(1, 1_000, 2_000, "Hello world."),
            new(2, 1_900, 3_000, "Hello world."),
            new(3, 4_000, 5_000, "Next sentence.")
        };

        var normalized = _canonicalizer.Normalize(cues);

        Assert.Collection(
            normalized,
            cue =>
            {
                Assert.Equal("Hello world.", cue.Text);
                Assert.Equal(1_000, cue.StartMs);
                Assert.Equal(3_000, cue.EndMs);
            },
            cue => Assert.Equal("Next sentence.", cue.Text));
    }

    [Fact]
    public void Normalize_MergesTransientRollingFragments()
    {
        var cues = new ParsedSubtitleCue[]
        {
            new(0, 0, 2_000, "That is it?"),
            new(1, 2_000, 2_010, "it?"),
            new(2, 2_010, 4_000, "Next sentence.")
        };

        var normalized = _canonicalizer.Normalize(cues);

        Assert.Collection(
            normalized,
            cue =>
            {
                Assert.Equal("That is it?", cue.Text);
                Assert.Equal(2_010, cue.EndMs);
            },
            cue => Assert.Equal("Next sentence.", cue.Text));
    }

    [Fact]
    public void Normalize_SplitsSpeakerBoundaryBeforeDiarization()
    {
        var cues = new[]
        {
            new ParsedSubtitleCue(0, 1_000, 3_000, ">> Really? >> Yeah, four is better.")
        };

        var normalized = _canonicalizer.Normalize(cues);

        Assert.Collection(
            normalized,
            cue =>
            {
                Assert.Equal("Really?", cue.Text);
                Assert.Equal(1_000, cue.StartMs);
            },
            cue =>
            {
                Assert.Equal("Yeah, four is better.", cue.Text);
                Assert.Equal(3_000, cue.EndMs);
            });
        Assert.Equal(normalized[0].EndMs, normalized[1].StartMs);
    }

    [Fact]
    public void CreateDraft_PrefersOriginalLanguageTrack()
    {
        var english = new ParsedSubtitleTrack(
            new DownloadedSubtitle("video.en-orig.vtt", "en-orig", "auto"),
            [new ParsedSubtitleCue(0, 0, 2_000, "This is the original speech.")]);
        var japaneseTranslation = new ParsedSubtitleTrack(
            new DownloadedSubtitle("video.ja.vtt", "ja", "auto"),
            [new ParsedSubtitleCue(0, 0, 2_000, "これは翻訳です。")]);

        var draft = _canonicalizer.CreateDraft([japaneseTranslation, english], "en", 2);

        Assert.NotNull(draft);
        Assert.Equal("en", draft.Language);
        Assert.Single(draft.Cues);
        Assert.Equal("This is the original speech.", draft.Cues[0].Text);
        Assert.Equal("en", draft.Cues[0].DetectedLanguage);
    }

    [Fact]
    public void CreateDraft_PrefersOfficialOriginalCaptionOverAutomaticOriginalVariant()
    {
        var official = new ParsedSubtitleTrack(
            new DownloadedSubtitle("video.en.vtt", "en", "official"),
            [new ParsedSubtitleCue(0, 0, 2_000, "Human caption.")]);
        var automatic = new ParsedSubtitleTrack(
            new DownloadedSubtitle("video.en-orig.vtt", "en-orig", "auto"),
            [new ParsedSubtitleCue(0, 0, 2_000, "Automatic caption.")]);

        var draft = _canonicalizer.CreateDraft([automatic, official], "en", 2);

        Assert.NotNull(draft);
        Assert.Equal("Human caption.", draft.Cues[0].Text);
        Assert.Equal("official", draft.Cues[0].OriginSource);
    }

    [Fact]
    public void CreateDraft_MarksLongSingleWordCueForRepair()
    {
        var track = new ParsedSubtitleTrack(
            new DownloadedSubtitle("video.en.vtt", "en", "auto"),
            [new ParsedSubtitleCue(0, 5_000, 20_000, "Heat.")]);

        var draft = _canonicalizer.CreateDraft([track], "en", 2);

        Assert.NotNull(draft);
        Assert.True(draft.Cues[0].NeedsReview);
        var range = Assert.Single(draft.RepairRanges);
        Assert.Equal(3_000, range.StartMs);
        Assert.Equal(22_000, range.EndMs);
    }

    [Theory]
    [InlineData("今日はね、I want to play.", "mixed")]
    [InlineData("今日は遊びます。", "ja")]
    [InlineData("I want to play.", "en")]
    public void DetectWrittenLanguage_RecognizesSupportedScripts(string text, string expected)
    {
        Assert.Equal(expected, SubtitleCanonicalizer.DetectWrittenLanguage(text));
    }

}
