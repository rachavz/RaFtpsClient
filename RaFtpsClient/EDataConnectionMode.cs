namespace RaFtpsClient;

/// <summary>
/// Specifies the data connection mode for FTP transfers.
/// </summary>
public enum EDataConnectionMode
{
    /// <summary>Active mode - the server connects to the client for data transfers.</summary>
    Active,
    /// <summary>Passive mode - the client connects to the server for data transfers.</summary>
    Passive
}
