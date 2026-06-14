using auto_ffmpeg;
using Xunit;

public class DashStreamTests
{
    [Theory]
    [InlineData("video/mp4", true)]
    [InlineData("video/webm", true)]
    [InlineData("audio/mp4", false)]
    public void IsVideoMime_Works(string mime, bool expected)
        => Assert.Equal(expected, DashStream.IsVideoMime(mime));

    [Theory]
    [InlineData("audio/mp4", true)]
    [InlineData("audio/webm", true)]
    [InlineData("video/mp4", false)]
    [InlineData(null, false)]
    public void IsAudioMime_Works(string? mime, bool expected)
        => Assert.Equal(expected, DashStream.IsAudioMime(mime));

    [Theory]
    [InlineData("video/mp4", "mp4")]
    [InlineData("video/webm", "webm")]
    [InlineData("audio/mp4", "m4a")]
    [InlineData("audio/webm", "weba")]
    [InlineData("audio/ogg", "m4a")]
    [InlineData("text/html", "mp4")]
    [InlineData(null, "mp4")]
    public void ExtFromMime_Works(string? mime, string ext)
        => Assert.Equal(ext, DashStream.ExtFromMime(mime));

    [Fact]
    public void GetQueryParam_ReadsValue()
    {
        var url = "https://x.googlevideo.com/videoplayback?itag=137&mime=video%2Fmp4&clen=12345&range=0-100";
        Assert.Equal("137", DashStream.GetQueryParam(url, "itag"));
        Assert.Equal("video/mp4", DashStream.GetQueryParam(url, "mime")); // URL-decoded
        Assert.Equal("12345", DashStream.GetQueryParam(url, "clen"));
    }

    [Fact]
    public void GetQueryParam_AbsentKey_ReturnsNull()
    {
        Assert.Null(DashStream.GetQueryParam("https://x.com/videoplayback?itag=137", "nope"));
    }

    [Fact]
    public void GetQueryParam_NoQuery_ReturnsNull()
    {
        Assert.Null(DashStream.GetQueryParam("https://x.com/videoplayback", "itag"));
    }

    [Fact]
    public void StripRange_RemovesRangeParams()
    {
        var url = "https://x.googlevideo.com/videoplayback?itag=137&range=0-100&rn=3&rbuf=0&mime=video%2Fmp4";
        var s = DashStream.StripRange(url);
        Assert.DoesNotContain("range=", s);
        Assert.DoesNotContain("rn=", s);
        Assert.DoesNotContain("rbuf=", s);
        Assert.Contains("itag=137", s);
        Assert.Contains("mime=video%2Fmp4", s);
    }

    [Fact]
    public void StripRange_NoQueryString_ReturnsUnchanged()
    {
        Assert.Equal("https://x.com/videoplayback", DashStream.StripRange("https://x.com/videoplayback"));
    }

    [Fact]
    public void StripRange_AllParamsStripped_ReturnsBaseUrlNoTrailingQuestion()
    {
        Assert.Equal("https://x.com/videoplayback",
            DashStream.StripRange("https://x.com/videoplayback?range=0-1&rn=2&rbuf=0"));
    }
}
