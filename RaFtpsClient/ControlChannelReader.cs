using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RaFtpsClient;

/// <summary>
/// Reads CRLF-terminated UTF-8 lines from the control channel, synchronously or asynchronously,
/// over one shared buffer. Replaces StreamReader, whose ReadLineAsync takes no cancellation token
/// and whose read-ahead would be stranded when the underlying stream is swapped for TLS.
/// </summary>
internal sealed class ControlChannelReader
{
    private readonly Stream stream;
    private byte[] buffer = new byte[4096];
    private int start;
    private int end;

    public ControlChannelReader(Stream stream)
    {
        this.stream = stream;
    }

    /// <summary>Returns the next line without its terminator, or null at end of stream.</summary>
    public string ReadLine()
    {
        while (true)
        {
            string line = TakeBufferedLine();
            if (line != null) return line;
            MakeRoom();
            int read = stream.Read(buffer, end, buffer.Length - end);
            if (read == 0) return TakeRemainder();
            end += read;
        }
    }

    /// <summary>Returns the next line without its terminator, or null at end of stream.</summary>
    public async Task<string> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            string line = TakeBufferedLine();
            if (line != null) return line;
            MakeRoom();
            int read = await stream.ReadAsync(buffer, end, buffer.Length - end, cancellationToken).ConfigureAwait(false);
            if (read == 0) return TakeRemainder();
            end += read;
        }
    }

    private string TakeBufferedLine()
    {
        int newline = Array.IndexOf(buffer, (byte)'\n', start, end - start);
        if (newline < 0) return null;
        int lineEnd = (newline > start && buffer[newline - 1] == (byte)'\r') ? newline - 1 : newline;
        string line = Encoding.UTF8.GetString(buffer, start, lineEnd - start);
        start = newline + 1;
        if (start == end) start = end = 0;
        return line;
    }

    // A final line the server did not terminate is still a line; only an empty buffer means EOF.
    private string TakeRemainder()
    {
        if (end == start) return null;
        string line = Encoding.UTF8.GetString(buffer, start, end - start);
        start = end = 0;
        return line;
    }

    private void MakeRoom()
    {
        if (end < buffer.Length) return;
        if (start > 0)
        {
            Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
            end -= start;
            start = 0;
            return;
        }
        Array.Resize(ref buffer, buffer.Length * 2);
    }
}
