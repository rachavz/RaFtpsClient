using System;

namespace RaFtpsClient;

/// <summary>
/// Event arguments for the <see cref="FTPSClient.LogServerReply"/> event.
/// </summary>
public class LogServerReplyEventArgs : EventArgs
{
    /// <summary>Gets the FTP server reply.</summary>
    public FTPReply ServerReply { get; private set; }

    /// <summary>Initializes a new instance with the specified server reply.</summary>
    /// <param name="serverReply">The FTP server reply.</param>
    public LogServerReplyEventArgs(FTPReply serverReply)
    {
        ServerReply = serverReply;
    }
}
