using McpEngramMemory.Core.Services.Evaluation;

namespace McpEngramMemory.Tests;

public class MrcrProbeParserTests
{
    [Theory]
    [InlineData(
        "Prepend el85MM9uD2 to the 6th (1 indexed) short scene in a play about temperatures. Do not include any other text in your response.",
        "el85MM9uD2", 6, "short scene in a play about temperatures")]
    [InlineData(
        "Prepend lm20V0QF3K to the 4th (1 indexed) social media post about judgment. Do not include any other text in your response.",
        "lm20V0QF3K", 4, "social media post about judgment")]
    [InlineData(
        "Prepend X to the 1st (1 indexed) limerick about cheese.",
        "X", 1, "limerick about cheese")]
    [InlineData(
        "Prepend abc123 to the 22nd (1 indexed) haiku about night. Do not include any other text.",
        "abc123", 22, "haiku about night")]
    public void TryParse_ProbeTemplates(string probe, string expectedRand, int expectedOrd, string expectedSig)
    {
        Assert.True(MrcrProbeParser.TryParse(probe, out var info));
        Assert.Equal(expectedRand, info.RandomPrefix);
        Assert.Equal(expectedOrd, info.Ordinal);
        Assert.Equal(expectedSig, info.CategorySignature);
    }

    [Theory]
    [InlineData("Summarize the conversation so far.")]
    [InlineData("")]
    [InlineData("Prepend X to a haiku.")] // no ordinal
    public void TryParse_UnmatchedProbes(string probe)
    {
        Assert.False(MrcrProbeParser.TryParse(probe, out _));
    }

    [Theory]
    [InlineData("Write me a short scene in a play about temperatures", "short scene in a play about temperatures")]
    [InlineData("write me a  Short Scene  in a Play ABOUT TEMPERATURES", "short scene in a play about temperatures")]
    [InlineData("Create an essay about the moon.", "essay about the moon")]
    [InlineData("a limerick about cheese", "a limerick about cheese")]
    public void NormalizeSignature_StripsPrefixesAndLowercases(string raw, string expected)
    {
        Assert.Equal(expected, MrcrProbeParser.NormalizeSignature(raw));
    }

    [Fact]
    public void NormalizeSignature_ProbeAndUserAskAlign()
    {
        var probe = "Prepend XYZ to the 6th (1 indexed) short scene in a play about temperatures.";
        var ask = "Write me a short scene in a play about temperatures";

        Assert.True(MrcrProbeParser.TryParse(probe, out var info));
        Assert.Equal(info.CategorySignature, MrcrProbeParser.NormalizeSignature(ask));
    }
}
