using System.Security.Cryptography.X509Certificates;
using System.Net.Security;

namespace RaFtpsClient;

/// <summary>
/// Callback for file transfer progress notifications.
/// </summary>
/// <param name="sender">The <see cref="FTPSClient"/> instance performing the transfer.</param>
/// <param name="action">The current transfer action.</param>
/// <param name="localObjectName">The local file or directory path.</param>
/// <param name="remoteObjectName">The remote file or directory path.</param>
/// <param name="fileTransmittedBytes">The number of bytes transmitted so far.</param>
/// <param name="fileTransferSize">The total file size, or null if unknown.</param>
/// <param name="cancel">Set to true to cancel the transfer.</param>
public delegate void FileTransferCallback(FTPSClient sender, ETransferActions action, string localObjectName, string remoteObjectName, ulong fileTransmittedBytes, ulong? fileTransferSize, ref bool cancel);

/// <summary>
/// Event handler for FTP command logging.
/// </summary>
/// <param name="sender">The event source.</param>
/// <param name="args">The command event arguments.</param>
public delegate void LogCommandEventHandler(object sender, LogCommandEventArgs args);

/// <summary>
/// Event handler for FTP server reply logging.
/// </summary>
/// <param name="sender">The event source.</param>
/// <param name="args">The server reply event arguments.</param>
public delegate void LogServerReplyEventHandler(object sender, LogServerReplyEventArgs args);
