using System.Collections.Generic;
using auto_ffmpeg;
using Xunit;

public class ProgressiveTranscodesTests
{
    const string Sample = """
    {
      "mediaMetadata": { "title": "My Video.mp4" },
      "mediaStreamingData": {
        "formatStreamingData": {
          "progressiveTranscodes": [
            { "itag": 18, "url": "https://h/vp?itag=18" },
            { "itag": 22, "url": "https://h/vp?itag=22" }
          ]
        }
      }
    }
    """;

    [Fact]
    public void Parse_ReturnsTranscodesInOrder()
    {
        var list = ProgressiveTranscodes.Parse(Sample);
        Assert.Equal(2, list.Count);
        Assert.Equal("18", list[0].Itag);
        Assert.Equal("https://h/vp?itag=18", list[0].Url);
        Assert.Equal("22", list[1].Itag);
    }

    [Fact]
    public void Parse_AcceptsStringItag()
    {
        var json = """
        {"mediaStreamingData":{"formatStreamingData":{"progressiveTranscodes":
        [{"itag":"37","url":"https://h/vp?itag=37"}]}}}
        """;
        var list = ProgressiveTranscodes.Parse(json);
        Assert.Single(list);
        Assert.Equal("37", list[0].Itag);
    }

    [Fact]
    public void Highest_ReturnsLastElement()
    {
        var list = ProgressiveTranscodes.Parse(Sample);
        Assert.Equal("22", ProgressiveTranscodes.Highest(list)!.Itag);
    }

    [Fact]
    public void Highest_NullWhenEmpty()
        => Assert.Null(ProgressiveTranscodes.Highest(new List<Transcode>()));

    [Fact]
    public void Parse_EmptyOnMissingNode()
        => Assert.Empty(ProgressiveTranscodes.Parse("{\"foo\":1}"));

    [Fact]
    public void Parse_EmptyOnMalformedJson()
        => Assert.Empty(ProgressiveTranscodes.Parse("not json"));

    [Fact]
    public void Title_ReturnsTitleOrNull()
    {
        Assert.Equal("My Video.mp4", ProgressiveTranscodes.Title(Sample));
        Assert.Null(ProgressiveTranscodes.Title("{}"));
    }
}
