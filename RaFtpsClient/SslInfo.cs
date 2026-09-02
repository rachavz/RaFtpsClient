using System.Net.Security;
using System.Security.Authentication;

namespace RaFtpsClient;

/// <summary>
/// Contains SSL/TLS connection details including protocol and algorithm information.
/// </summary>
public class SslInfo
{
    /// <summary>Gets or sets the SSL/TLS protocol version.</summary>
    public SslProtocols SslProtocol { get; set; }

    /// <summary>Gets or sets the cipher algorithm used.</summary>
    public CipherAlgorithmType CipherAlgorithm { get; set; }

    /// <summary>Gets or sets the cipher algorithm strength in bits.</summary>
    public int CipherStrength { get; set; }

    /// <summary>Gets or sets the hash algorithm used.</summary>
    public HashAlgorithmType HashAlgorithm { get; set; }

    /// <summary>Gets or sets the hash algorithm strength in bits.</summary>
    public int HashStrength { get; set; }

    /// <summary>Gets or sets the key exchange algorithm used.</summary>
    public ExchangeAlgorithmType KeyExchangeAlgorithm { get; set; }

    /// <summary>Gets or sets the key exchange algorithm strength in bits.</summary>
    public int KeyExchangeStrength { get; set; }

    /// <summary>Returns a string describing the SSL/TLS connection details.</summary>
    public override string ToString()
    {
        return $"{SslProtocol}, {CipherAlgorithm} ({CipherStrength} bit), {KeyExchangeAlgorithm} ({KeyExchangeStrength} bit), {HashAlgorithm} ({HashStrength} bit)";
    }
}
