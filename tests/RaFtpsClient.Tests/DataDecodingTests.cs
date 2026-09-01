using System.Text;

namespace RaFtpsClient.Tests;

public class DataDecodingTests
{
    /// <summary>A stream that hands back at most <c>chunkSize</c> bytes per read, so a decoder is
    /// forced to deal with multi-byte characters split exactly where the test wants them.</summary>
    private sealed class ChunkedStream : Stream
    {
        private readonly byte[] data;
        private readonly int chunkSize;
        private int position;

        public ChunkedStream(byte[] data, int chunkSize)
        {
            this.data = data;
            this.chunkSize = chunkSize;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(Math.Min(chunkSize, count), data.Length - position);
            Array.Copy(data, position, buffer, offset, n);
            position += n;
            return n;
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

    private const string MultiByteText =
        "café-piñata.txt\r\n" +          // 2-byte sequences
        "中文目录名.txt\r\n" +            // 3-byte sequences
        "emoji-\U0001F600-file.txt\r\n" + // 4-byte sequence
        "Ωμέγα-ασκήσεις.txt\r\n";

    // Reads land wherever the network splits them; every split offset has to round-trip.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(16)]
    [InlineData(4096)]
    public void DecodesTextSplitAtEveryChunkSize(int chunkSize)
    {
        var stream = new ChunkedStream(Encoding.UTF8.GetBytes(MultiByteText), chunkSize);

        string decoded = FTPSClient.ReadStreamAsUtf8(stream, 8192);

        Assert.Equal(MultiByteText, decoded);
        Assert.DoesNotContain('�', decoded);
    }

    // Splitting inside a sequence is exactly the case a per-read Encoding.GetString gets wrong.
    [Fact]
    public void DecodesASequenceSplitAcrossTheReadBuffer()
    {
        string text = new string('a', 9) + "中" + new string('b', 9);
        var stream = new ChunkedStream(Encoding.UTF8.GetBytes(text), 1024);

        // A 10-byte buffer puts the first byte of 中 at the end of read one and the rest in read two.
        Assert.Equal(text, FTPSClient.ReadStreamAsUtf8(stream, 10));
    }

    [Fact]
    public void DecodesAnEmptyStream()
    {
        Assert.Equal("", FTPSClient.ReadStreamAsUtf8(new ChunkedStream(Array.Empty<byte>(), 16), 1024));
    }

    [Fact]
    public void DecodesPlainAsciiUnchanged()
    {
        string text = "-rw-r--r-- 1 o g 5 May 31 12:00 plain.txt\r\n";

        Assert.Equal(text, FTPSClient.ReadStreamAsUtf8(new ChunkedStream(Encoding.UTF8.GetBytes(text), 3), 1024));
    }

    [Fact]
    public void ParsesThePortFromAnEpsvReply()
    {
        Assert.Equal(49152, FTPSClient.ParseEpsvPort(
            new FTPReply { Code = 229, Message = "Entering Extended Passive Mode (|||49152|)" }));
    }

    [Theory]
    [InlineData("Entering Extended Passive Mode (49152)")]
    [InlineData("Entering Extended Passive Mode (|||49152|extra|)")]
    [InlineData("")]
    public void RejectsAMalformedEpsvReply(string message)
    {
        Assert.Throws<FTPProtocolException>(() =>
            FTPSClient.ParseEpsvPort(new FTPReply { Code = 229, Message = message }));
    }
}
