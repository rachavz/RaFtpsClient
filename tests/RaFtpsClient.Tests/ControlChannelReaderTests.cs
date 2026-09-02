using System.Text;

namespace RaFtpsClient.Tests;

public class ControlChannelReaderTests
{
    /// <summary>Hands back at most <c>chunkSize</c> bytes per read so line boundaries land wherever the
    /// test wants them, including in the middle of a CRLF pair or a multi-byte character.</summary>
    private sealed class ChunkedStream : Stream
    {
        private readonly byte[] data;
        private readonly int chunkSize;
        private int position;

        public ChunkedStream(string text, int chunkSize)
        {
            data = Encoding.UTF8.GetBytes(text);
            this.chunkSize = chunkSize;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(Math.Min(chunkSize, count), data.Length - position);
            Array.Copy(data, position, buffer, offset, n);
            position += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read(buffer, offset, count));
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static List<string> ReadAll(ControlChannelReader reader)
    {
        var lines = new List<string>();
        string line;
        while ((line = reader.ReadLine()) != null) lines.Add(line);
        return lines;
    }

    private static async Task<List<string>> ReadAllAsync(ControlChannelReader reader)
    {
        var lines = new List<string>();
        string line;
        while ((line = await reader.ReadLineAsync(CancellationToken.None)) != null) lines.Add(line);
        return lines;
    }

    private const string Reply = "220-Welcome\r\n220-  to the fake\r\n220 ready\r\n";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(4096)]
    public void SplitsLinesRegardlessOfWhereReadsLand(int chunkSize)
    {
        var lines = ReadAll(new ControlChannelReader(new ChunkedStream(Reply, chunkSize)));

        Assert.Equal(new[] { "220-Welcome", "220-  to the fake", "220 ready" }, lines);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(4096)]
    public async Task SplitsLinesRegardlessOfWhereReadsLandAsync(int chunkSize)
    {
        var lines = await ReadAllAsync(new ControlChannelReader(new ChunkedStream(Reply, chunkSize)));

        Assert.Equal(new[] { "220-Welcome", "220-  to the fake", "220 ready" }, lines);
    }

    [Fact]
    public void AcceptsBareLineFeedsToo()
    {
        Assert.Equal(new[] { "one", "two" }, ReadAll(new ControlChannelReader(new ChunkedStream("one\ntwo\n", 4096))));
    }

    [Fact]
    public void ReturnsAnUnterminatedFinalLine()
    {
        Assert.Equal(new[] { "complete", "partial" }, ReadAll(new ControlChannelReader(new ChunkedStream("complete\r\npartial", 3))));
    }

    [Fact]
    public void ReturnsNullAtEndOfStream()
    {
        var reader = new ControlChannelReader(new ChunkedStream("only\r\n", 4096));

        Assert.Equal("only", reader.ReadLine());
        Assert.Null(reader.ReadLine());
        Assert.Null(reader.ReadLine());
    }

    [Fact]
    public void ReturnsNullForAnEmptyStream()
    {
        Assert.Null(new ControlChannelReader(new ChunkedStream("", 4096)).ReadLine());
    }

    [Fact]
    public void GrowsForLinesLongerThanItsBuffer()
    {
        string longLine = "257 \"" + new string('d', 10_000) + "\"";

        Assert.Equal(longLine, new ControlChannelReader(new ChunkedStream(longLine + "\r\n", 1024)).ReadLine());
    }

    // Grows only when nothing has been consumed; with a consumed prefix it must shift the unread
    // tail down instead, and the shifted bytes have to be the right ones.
    [Fact]
    public void CompactsTheBufferWithoutLosingBytes()
    {
        string longLine = "211-" + string.Concat(Enumerable.Range(0, 800).Select(i => (i % 10).ToString() + "abcde"));
        var reader = new ControlChannelReader(new ChunkedStream("220 short\r\n" + longLine + "\r\n220 after\r\n", 1000));

        Assert.Equal("220 short", reader.ReadLine());
        Assert.Equal(longLine, reader.ReadLine());
        Assert.Equal("220 after", reader.ReadLine());
        Assert.Null(reader.ReadLine());
    }

    [Fact]
    public void DecodesMultiByteCharactersSplitAcrossReads()
    {
        string line = "257 \"/home/中文/café\"";

        Assert.Equal(line, new ControlChannelReader(new ChunkedStream(line + "\r\n", 1)).ReadLine());
    }

    [Fact]
    public void PreservesEmptyLines()
    {
        Assert.Equal(new[] { "a", "", "b" }, ReadAll(new ControlChannelReader(new ChunkedStream("a\r\n\r\nb\r\n", 4096))));
    }

    [Fact]
    public async Task HonoursCancellation()
    {
        var reader = new ControlChannelReader(new ChunkedStream("never\r\n", 4096));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadLineAsync(new CancellationToken(canceled: true)));
    }
}
