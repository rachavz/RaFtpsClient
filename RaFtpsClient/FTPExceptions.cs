using System;

namespace RaFtpsClient;

/// <summary>
/// Base exception class for FTP-related errors.
/// </summary>
public class FTPException : Exception
{
    /// <summary>Initializes a new instance.</summary>
    protected FTPException() { }

    /// <summary>Initializes a new instance with a message.</summary>
    /// <param name="Message">The error message.</param>
    public FTPException(string Message) : base(Message) { }

    /// <summary>Initializes a new instance with a message and inner exception.</summary>
    /// <param name="Message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public FTPException(string Message, Exception innerException) : base(Message, innerException) { }
}

/// <summary>
/// Thrown when an FTP server reply cannot be parsed.
/// </summary>
public class FTPReplyParseException : FTPException
{
    /// <summary>Gets the raw reply text that could not be parsed.</summary>
    public string ReplyText => replyText;

    private string replyText;

    /// <summary>Initializes a new instance with the raw reply text.</summary>
    /// <param name="replyText">The raw reply text.</param>
    public FTPReplyParseException(string replyText) : base("Invalid server reply: " + replyText)
    {
        this.replyText = replyText;
    }
}

/// <summary>
/// Thrown when an FTP protocol reply is invalid.
/// </summary>
public class FTPProtocolException : FTPException
{
    /// <summary>Gets the FTP reply that caused the exception.</summary>
    public FTPReply Reply => reply;

    private FTPReply reply;

    /// <summary>Initializes a new instance with the invalid reply.</summary>
    /// <param name="reply">The invalid FTP reply.</param>
    public FTPProtocolException(FTPReply reply) : base("Invalid FTP protocol reply: " + reply.ToString())
    {
        this.reply = reply;
    }
}

/// <summary>
/// Thrown when an FTP operation is cancelled by the user.
/// </summary>
public class FTPOperationCancelledException : FTPException
{
    /// <summary>Initializes a new instance with a message.</summary>
    /// <param name="Message">The error message.</param>
    public FTPOperationCancelledException(string Message) : base(Message) { }
}

/// <summary>
/// Thrown when an FTP command returns an error code (4xx or 5xx).
/// </summary>
public class FTPCommandException : FTPException
{
    private int errorCode;

    /// <summary>Gets the FTP error code returned by the server.</summary>
    public int ErrorCode => errorCode;

    /// <summary>Initializes a new instance with a message.</summary>
    /// <param name="Message">The error message.</param>
    public FTPCommandException(string Message) : base(Message) { }

    /// <summary>Initializes a new instance with a message and inner exception.</summary>
    /// <param name="Message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public FTPCommandException(string Message, Exception innerException) : base(Message, innerException) { }

    /// <summary>Initializes a new instance from an FTP error reply.</summary>
    /// <param name="reply">The FTP reply containing the error.</param>
    public FTPCommandException(FTPReply reply) : base(reply.Message)
    {
        errorCode = reply.Code;
    }
}

/// <summary>
/// Thrown when an SSL/TLS error occurs during FTPS connection.
/// </summary>
public class FTPSslException : FTPException
{
    /// <summary>Initializes a new instance with a message.</summary>
    /// <param name="Message">The error message.</param>
    public FTPSslException(string Message) : base(Message) { }

    /// <summary>Initializes a new instance with a message and inner exception.</summary>
    /// <param name="Message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public FTPSslException(string Message, Exception innerException) : base(Message, innerException) { }
}
