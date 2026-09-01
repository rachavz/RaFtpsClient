namespace RaFtpsClient;

/// <summary>
/// Represents an FTP server reply with a status code and message.
/// </summary>
public class FTPReply
{
    private int code;
    private string message;

    /// <summary>Gets or sets the FTP status code.</summary>
    public int Code
    {
        get { return code; }
        set { code = value; }
    }

    /// <summary>Gets or sets the FTP reply message.</summary>
    public string Message
    {
        get { return message; }
        set { message = value; }
    }

    /// <summary>Returns the string representation of the reply.</summary>
    public override string ToString()
    {
        return $"{Code} {Message}";
    }
}
