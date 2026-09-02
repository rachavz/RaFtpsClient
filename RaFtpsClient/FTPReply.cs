namespace RaFtpsClient;

/// <summary>
/// Represents an FTP server reply with a status code and message.
/// </summary>
public class FTPReply
{
    /// <summary>Gets or sets the FTP status code.</summary>
    public int Code { get; set; }

    /// <summary>Gets or sets the FTP reply message.</summary>
    public string Message { get; set; }

    /// <summary>Returns the string representation of the reply.</summary>
    public override string ToString()
    {
        return $"{Code} {Message}";
    }
}
