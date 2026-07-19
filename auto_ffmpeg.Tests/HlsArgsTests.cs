using auto_ffmpeg;
using Xunit;

public class HlsArgsTests
{
    const string Url = "https://vz.example.net/id/1080p/video.m3u8";
    const string Out = @"C:\Videos\video_1080p.mp4";
    const string Ref = "https://iframe.mediadelivery.net/";
    const string Ua  = "Mozilla/5.0 Chrome/150";

    [Fact]
    public void BuildHlsArgs_ContainsCoreCopyAndBitstreamFilter()
    {
        var a = FfmpegService.BuildHlsArgs(Url, Out, Ref, Ua, withProgress: true);
        Assert.Contains("-c copy", a);
        Assert.Contains("-bsf:a aac_adtstoasc", a);
    }

    [Fact]
    public void BuildHlsArgs_ContainsAllowedExtensions()
    {
        var a = FfmpegService.BuildHlsArgs(Url, Out, Ref, Ua, withProgress: true);
        Assert.Contains("-extension_picky 0", a);
        Assert.Contains("-allowed_extensions ts,mp4,m4s,aac,mpegts,dts", a);
        Assert.Contains("-allowed_segment_extensions ts,mp4,m4s,aac,mpegts,dts", a);
    }

    [Fact]
    public void BuildHlsArgs_RefererHeaderEndsWithCrlf()
    {
        var a = FfmpegService.BuildHlsArgs(Url, Out, Ref, Ua, withProgress: true);
        Assert.Contains("-headers \"Referer: https://iframe.mediadelivery.net/\\r\\n\"", a);
    }

    [Fact]
    public void BuildHlsArgs_IncludesUserAgent()
    {
        var a = FfmpegService.BuildHlsArgs(Url, Out, Ref, Ua, withProgress: true);
        Assert.Contains("-user_agent \"Mozilla/5.0 Chrome/150\"", a);
    }

    [Fact]
    public void BuildHlsArgs_EmptyRefererOmitsHeaders()
    {
        var a = FfmpegService.BuildHlsArgs(Url, Out, "", Ua, withProgress: true);
        Assert.DoesNotContain("-headers", a);
    }

    [Fact]
    public void BuildHlsArgs_EmptyUserAgentOmitsUserAgent()
    {
        var a = FfmpegService.BuildHlsArgs(Url, Out, Ref, "", withProgress: true);
        Assert.DoesNotContain("-user_agent", a);
    }

    [Fact]
    public void BuildHlsArgs_ProgressFlagTogglesWithParameter()
    {
        Assert.Contains("-progress pipe:1", FfmpegService.BuildHlsArgs(Url, Out, Ref, Ua, withProgress: true));
        Assert.DoesNotContain("-progress pipe:1", FfmpegService.BuildHlsArgs(Url, Out, Ref, Ua, withProgress: false));
    }

    [Fact]
    public void BuildHlsArgs_QuotesUrlAndOutput()
    {
        var a = FfmpegService.BuildHlsArgs(Url, Out, Ref, Ua, withProgress: true);
        Assert.Contains($"-i \"{Url}\"", a);
        Assert.Contains($"\"{Out}\"", a);
    }
}
