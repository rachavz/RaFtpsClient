using System;

namespace RaFtpsClient;

/// <summary>
/// Specifies the SSL/TLS support mode for FTP connections.
/// </summary>
[Flags]
public enum ESSLSupportMode
{
    /// <summary>No SSL/TLS encryption (plain text).</summary>
    ClearText = 0,
    /// <summary>SSL/TLS credentials requested.</summary>
    CredentialsRequested = 1,
    /// <summary>SSL/TLS credentials required.</summary>
    CredentialsRequired = 3,
    /// <summary>Control channel encryption requested.</summary>
    ControlChannelRequested = 5,
    /// <summary>Control channel encryption required.</summary>
    ControlChannelRequired = 7,
    /// <summary>Data channel encryption requested.</summary>
    DataChannelRequested = 9,
    /// <summary>Data channel encryption required.</summary>
    DataChannelRequired = 0x1B,
    /// <summary>Both control and data channel encryption requested.</summary>
    ControlAndDataChannelsRequested = 0xD,
    /// <summary>Both control and data channel encryption required.</summary>
    ControlAndDataChannelsRequired = 0x1F,
    /// <summary>All channels encrypted.</summary>
    All = 0x1F,
    /// <summary>Implicit SSL/TLS (connects directly on port 990).</summary>
    Implicit = 0x3F
}
