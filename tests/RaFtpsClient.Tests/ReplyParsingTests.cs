using System.Net;

namespace RaFtpsClient.Tests;

public class ReplyParsingTests
{
    private static FTPReply Reply(int code, string message) => new FTPReply { Code = code, Message = message };

    [Fact]
    public void ParsesTheQuotedPathFromAPwdReply()
    {
        Assert.Equal("/home/user",
            FTPSClient.ParsePwdReply(Reply(257, "\"/home/user\" is the current directory.")));
    }

    [Fact]
    public void ParsesAPathContainingSpacesFromAPwdReply()
    {
        Assert.Equal("/home/my user", FTPSClient.ParsePwdReply(Reply(257, "\"/home/my user\"")));
    }

    [Theory]
    [InlineData("no quotes at all")]
    [InlineData("\"only one quote")]
    public void RejectsAMalformedPwdReply(string message)
    {
        Assert.Throws<FTPProtocolException>(() => FTPSClient.ParsePwdReply(Reply(257, message)));
    }

    [Fact]
    public void ParsesAPasvReplyIntoAnEndpoint()
    {
        IPEndPoint ep = FTPSClient.ParsePasvReply(Reply(227, "Entering Passive Mode (192,168,1,10,195,80)."));

        Assert.Equal(IPAddress.Parse("192.168.1.10"), ep.Address);
        Assert.Equal(195 * 256 + 80, ep.Port);
    }

    [Theory]
    [InlineData("Entering Passive Mode 192,168,1,10,195,80")]
    [InlineData("Entering Passive Mode (192,168,1,10,195)")]
    [InlineData("Entering Passive Mode (192,168,1,10,195,80,7)")]
    public void RejectsAMalformedPasvReply(string message)
    {
        Assert.Throws<FTPProtocolException>(() => FTPSClient.ParsePasvReply(Reply(227, message)));
    }

    // The length arithmetic here used to drop the final character of the generated name.
    [Fact]
    public void ParsesTheWholeStouFilename()
    {
        Assert.Equal("/pub/upload.txt", FTPSClient.ParseStouReply(Reply(150, "FILE: /pub/upload.txt")));
    }

    [Fact]
    public void RejectsAStouReplyWithoutAName()
    {
        Assert.Throws<FTPProtocolException>(() => FTPSClient.ParseStouReply(Reply(150, "unnamed")));
    }

    [Fact]
    public void ParsesAnMdtmTimestamp()
    {
        DateTime parsed = FTPSClient.ParseFTPDateTime("20220531120000");

        Assert.Equal(2022, parsed.Year);
        Assert.Equal(5, parsed.Month);
        Assert.Equal(31, parsed.Day);
    }

    [Fact]
    public void ParsesAnMdtmTimestampWithFractionalSeconds()
    {
        Assert.Equal(250, FTPSClient.ParseFTPDateTime("20220531120000.250").Millisecond);
    }

    [Theory]
    [InlineData("/pub", "file.txt", "/pub/file.txt")]
    [InlineData("/pub/", "file.txt", "/pub/file.txt")]
    [InlineData("/", "file.txt", "/file.txt")]
    public void CombinesRemotePaths(string a, string b, string expected)
    {
        Assert.Equal(expected, FTPSClient.CombineRemotePath(a, b));
    }

    [Theory]
    [InlineData("report.txt", EPatternStyle.Verbatim, "^report\\.txt$")]
    [InlineData("*.txt", EPatternStyle.Wildcard, "^.*\\.txt$")]
    [InlineData("log?.txt", EPatternStyle.Wildcard, "^log.{1}\\.txt$")]
    [InlineData("^a.*$", EPatternStyle.Regex, "^a.*$")]
    public void TranslatesFilePatterns(string pattern, EPatternStyle style, string expected)
    {
        Assert.Equal(expected, FTPSClient.GetRegexPattern(pattern, style));
    }

    [Fact]
    public void DisambiguatesLocalPathsThatCollide()
    {
        var seen = new List<string>();

        Assert.Equal("/tmp/a.txt", FTPSClient.GetUniquePath(seen, "/tmp/a.txt"));
        Assert.Equal("/tmp/a.txt_1", FTPSClient.GetUniquePath(seen, "/tmp/a.txt"));
        Assert.Equal("/tmp/a.txt_2", FTPSClient.GetUniquePath(seen, "/tmp/a.txt"));
        Assert.Equal("/tmp/b.txt", FTPSClient.GetUniquePath(seen, "/tmp/b.txt"));
    }
}
