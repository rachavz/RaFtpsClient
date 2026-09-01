using System;
using System.IO;

namespace RaFtpsClient;

internal delegate void FTPStreamCallback();

/// <summary>
/// A stream wrapper for FTP data transfers that enforces read/write permissions.
/// </summary>
public class FTPStream : Stream
{
    /// <summary>
    /// Specifies the allowed operation on the FTP stream.
    /// </summary>
    public enum EAllowedOperation
    {
        /// <summary>Reading is allowed.</summary>
        Read = 1,
        /// <summary>Writing is allowed.</summary>
        Write
    }

    private Stream innerStream;
    private FTPStreamCallback streamClosedCallback;
    private EAllowedOperation allowedOp;

    /// <summary>Gets whether the stream supports reading.</summary>
    public override bool CanRead
    {
        get
        {
            if (innerStream.CanRead)
            {
                return (allowedOp & EAllowedOperation.Read) == EAllowedOperation.Read;
            }
            return false;
        }
    }

    /// <summary>Gets whether the stream supports seeking.</summary>
    public override bool CanSeek => innerStream.CanSeek;

    /// <summary>Gets whether the stream supports writing.</summary>
    public override bool CanWrite
    {
        get
        {
            if (innerStream.CanWrite)
            {
                return (allowedOp & EAllowedOperation.Write) == EAllowedOperation.Write;
            }
            return false;
        }
    }

    /// <summary>Gets the length of the stream.</summary>
    public override long Length => innerStream.Length;

    /// <summary>Gets or sets the current position in the stream.</summary>
    public override long Position
    {
        get { return innerStream.Position; }
        set { innerStream.Position = value; }
    }

    internal FTPStream(Stream innerStream, EAllowedOperation allowedOp, FTPStreamCallback streamClosedCallback)
    {
        this.innerStream = innerStream;
        this.streamClosedCallback = streamClosedCallback;
        this.allowedOp = allowedOp;
    }

    /// <summary>Flushes the underlying stream.</summary>
    public override void Flush()
    {
        innerStream.Flush();
    }

    /// <summary>Reads data from the stream.</summary>
    /// <param name="buffer">The buffer to read into.</param>
    /// <param name="offset">The byte offset in the buffer.</param>
    /// <param name="count">The maximum number of bytes to read.</param>
    /// <returns>The number of bytes read.</returns>
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (!CanRead)
        {
            throw new FTPException("Operation not allowed");
        }
        return innerStream.Read(buffer, offset, count);
    }

    /// <summary>Sets the position within the stream.</summary>
    /// <param name="offset">The byte offset.</param>
    /// <param name="origin">The seek origin.</param>
    /// <returns>The new position.</returns>
    public override long Seek(long offset, SeekOrigin origin)
    {
        return innerStream.Seek(offset, origin);
    }

    /// <summary>Sets the length of the stream.</summary>
    /// <param name="value">The new length.</param>
    public override void SetLength(long value)
    {
        innerStream.SetLength(value);
    }

    /// <summary>Writes data to the stream.</summary>
    /// <param name="buffer">The buffer to write from.</param>
    /// <param name="offset">The byte offset in the buffer.</param>
    /// <param name="count">The number of bytes to write.</param>
    public override void Write(byte[] buffer, int offset, int count)
    {
        if (!CanWrite)
        {
            throw new FTPException("Operation not allowed");
        }
        innerStream.Write(buffer, offset, count);
    }

    /// <summary>Closes the stream and notifies the callback.</summary>
    public override void Close()
    {
        base.Close();
        streamClosedCallback();
    }
}
