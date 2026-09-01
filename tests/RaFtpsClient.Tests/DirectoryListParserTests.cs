namespace RaFtpsClient.Tests;

public class DirectoryListParserTests
{
    private static DirectoryListItem Single(string record)
    {
        IList<DirectoryListItem> list = DirectoryListParser.GetDirectoryList(record + "\r\n");
        return Assert.Single(list);
    }

    [Fact]
    public void ParsesAPlainUnixDirectory()
    {
        DirectoryListItem item = Single("drwxr-xr-x 2 owner grp 4096 May 31 12:00 documents");

        Assert.Equal("documents", item.Name);
        Assert.True(item.IsDirectory);
        Assert.False(item.IsSymLink);
        Assert.Equal("owner", item.Owner);
        Assert.Equal("grp", item.Group);
        Assert.Equal(4096uL, item.Size);
    }

    [Fact]
    public void ParsesAPlainUnixFile()
    {
        DirectoryListItem item = Single("-rw-r--r-- 1 owner grp 1234 May 31 12:00 readme.txt");

        Assert.Equal("readme.txt", item.Name);
        Assert.False(item.IsDirectory);
        Assert.Equal(1234uL, item.Size);
        Assert.Equal("-rw-r--r--", item.Flags);
    }

    // The style guess used to accept only "-" or "d" as the type character and only "-"/"x" in the
    // execute positions, so a listing made up entirely of these came back empty with no error.
    [Theory]
    [InlineData("drwxrwsr-x 2 o g 4096 May 31 12:00 setgid", true, false)]
    [InlineData("drwxrwxrwt 2 o g 4096 May 31 12:00 sticky", true, false)]
    [InlineData("drwxr-Sr-x 2 o g 4096 May 31 12:00 setgid-noexec", true, false)]
    [InlineData("-rwsr-xr-x 1 o g 1234 May 31 12:00 setuid", false, false)]
    [InlineData("lrwxrwxrwx 1 o g 7 May 31 12:00 link -> target", false, true)]
    [InlineData("crw-rw-rw- 1 o g 1234 May 31 12:00 null", false, false)]
    [InlineData("brw-rw---- 1 o g 1234 May 31 12:00 sda", false, false)]
    [InlineData("prw-r--r-- 1 o g 0 May 31 12:00 fifo", false, false)]
    [InlineData("srwxrwxrwx 1 o g 0 May 31 12:00 socket", false, false)]
    public void RecognisesEveryUnixEntryType(string record, bool isDirectory, bool isSymLink)
    {
        DirectoryListItem item = Single(record);

        Assert.Equal(isDirectory, item.IsDirectory);
        Assert.Equal(isSymLink, item.IsSymLink);
    }

    [Fact]
    public void ParsesSymLinkTargets()
    {
        DirectoryListItem item = Single("lrwxrwxrwx 1 o g 7 May 31 12:00 link -> ../elsewhere/file");

        Assert.Equal("link", item.Name);
        Assert.Equal("../elsewhere/file", item.SymLinkTargetPath);
    }

    [Fact]
    public void KeepsSpacesInNames()
    {
        Assert.Equal("my report v2.txt", Single("-rw-r--r-- 1 o g 5 May 31 12:00 my report v2.txt").Name);
    }

    [Fact]
    public void SkipsTheTotalLineAndDotEntries()
    {
        IList<DirectoryListItem> list = DirectoryListParser.GetDirectoryList(
            "total 12\r\n" +
            "drwxr-xr-x 2 o g 4096 May 31 12:00 .\r\n" +
            "drwxr-xr-x 2 o g 4096 May 31 12:00 ..\r\n" +
            "-rw-r--r-- 1 o g 5 May 31 12:00 real.txt\r\n");

        Assert.Equal("real.txt", Assert.Single(list).Name);
    }

    // One unreadable record used to throw and discard the whole listing.
    [Fact]
    public void SkipsUnparseableRecordsAndKeepsTheRest()
    {
        IList<DirectoryListItem> list = DirectoryListParser.GetDirectoryList(
            "drwxr-xr-x 2 o g 4096 May 31 12:00 good\r\n" +
            "this is not a listing record\r\n" +
            "-rw-r--r-- 1 o g 5 May 31 12:00 also-good.txt\r\n");

        Assert.Equal(new[] { "good", "also-good.txt" }, list.Select(i => i.Name));
    }

    [Fact]
    public void ReturnsEmptyForAnUnrecognisedStyle()
    {
        Assert.Empty(DirectoryListParser.GetDirectoryList("nothing here resembles a listing\r\n"));
    }

    [Fact]
    public void ReturnsEmptyForEmptyInput()
    {
        Assert.Empty(DirectoryListParser.GetDirectoryList(""));
    }

    [Fact]
    public void ParsesWindowsStyleListings()
    {
        IList<DirectoryListItem> list = DirectoryListParser.GetDirectoryList(
            "05-31-22  12:00PM       <DIR>          folder\r\n" +
            "05-31-22  12:00PM                 1234 file.txt\r\n");

        Assert.Collection(list,
            i => { Assert.Equal("folder", i.Name); Assert.True(i.IsDirectory); },
            i => { Assert.Equal("file.txt", i.Name); Assert.False(i.IsDirectory); Assert.Equal(1234uL, i.Size); });
    }

    [Fact]
    public void ParsesSingleDigitDaysPaddedByLs()
    {
        DirectoryListItem item = Single("-rw-r--r-- 1 o g 5 May  1 12:00 early.txt");

        Assert.Equal("early.txt", item.Name);
        Assert.Equal(5, item.CreationTime.Month);
        Assert.Equal(1, item.CreationTime.Day);
    }

    [Fact]
    public void UsesTheExplicitYearWhenListingGivesOne()
    {
        Assert.Equal(2019, Single("-rw-r--r-- 1 o g 5 May 31 2019 old.txt").CreationTime.Year);
    }

    // ls omits the year on recent entries, so ParseExact assumes the current one; without a
    // correction a December file read in January is dated eleven months into the future.
    [Fact]
    public void NeverInfersAFutureYear()
    {
        foreach (string month in new[] { "Jan", "Apr", "Jul", "Oct", "Dec" })
        {
            DirectoryListItem item = Single($"-rw-r--r-- 1 o g 5 {month} 28 23:59 dated.txt");
            Assert.True(item.CreationTime <= DateTime.Now.AddDays(1),
                $"{month} entry was dated {item.CreationTime:yyyy-MM-dd}, in the future");
        }
    }
}
