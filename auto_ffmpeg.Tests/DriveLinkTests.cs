using auto_ffmpeg;
using Xunit;

public class DriveLinkTests
{
    [Theory]
    [InlineData("https://drive.google.com/file/d/1OoFogs7Plbb7ReuPolgAfMR6Gd3o3SkU/view?usp=sharing", "1OoFogs7Plbb7ReuPolgAfMR6Gd3o3SkU")]
    [InlineData("https://drive.google.com/file/d/ABC1234567/view", "ABC1234567")]
    [InlineData("https://drive.google.com/open?id=XYZ7891011", "XYZ7891011")]
    [InlineData("https://drive.google.com/uc?export=download&id=ID42abcdef", "ID42abcdef")]
    public void ExtractFileId_ValidLinks_ReturnsId(string url, string expected)
        => Assert.Equal(expected, DriveLink.ExtractFileId(url));

    [Theory]
    [InlineData("https://example.com/file/d/")]
    [InlineData("not a url")]
    [InlineData("")]
    public void ExtractFileId_Invalid_ReturnsNull(string url)
        => Assert.Null(DriveLink.ExtractFileId(url));
}
