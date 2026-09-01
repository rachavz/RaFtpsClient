using System;

namespace RaFtpsClient;

/// <summary>
/// Event arguments for the <see cref="FTPSClient.LogCommand"/> event.
/// </summary>
public class LogCommandEventArgs : EventArgs
{
    /// <summary>Gets the FTP command text that was sent.</summary>
    public string CommandText { get; private set; }

    /// <summary>Initializes a new instance with the specified command text.</summary>
    /// <param name="commandText">The FTP command text.</param>
    public LogCommandEventArgs(string commandText)
    {
        CommandText = commandText;
    }
}
