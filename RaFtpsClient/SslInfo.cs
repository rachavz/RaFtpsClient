using System;
using System.Net.Security;
using System.Security.Authentication;

namespace RaFtpsClient;

/// <summary>
/// Contains SSL/TLS connection details including protocol and algorithm information.
/// </summary>
public class SslInfo
{
    private SslProtocols sslProtocol;
    private CipherAlgorithmType cipherAlgorithm;
    private int cipherStrength;
    private HashAlgorithmType hashAlgorithm;
    private int hashStrength;
    private ExchangeAlgorithmType keyExchangeAlgorithm;
    private int keyExchangeStrength;

    /// <summary>Gets or sets the SSL/TLS protocol version.</summary>
    public SslProtocols SslProtocol
    {
        get { return sslProtocol; }
        set { sslProtocol = value; }
    }

    /// <summary>Gets or sets the cipher algorithm used.</summary>
    public CipherAlgorithmType CipherAlgorithm
    {
        get { return cipherAlgorithm; }
        set { cipherAlgorithm = value; }
    }

    /// <summary>Gets or sets the cipher algorithm strength in bits.</summary>
    public int CipherStrength
    {
        get { return cipherStrength; }
        set { cipherStrength = value; }
    }

    /// <summary>Gets or sets the hash algorithm used.</summary>
    public HashAlgorithmType HashAlgorithm
    {
        get { return hashAlgorithm; }
        set { hashAlgorithm = value; }
    }

    /// <summary>Gets or sets the hash algorithm strength in bits.</summary>
    public int HashStrength
    {
        get { return hashStrength; }
        set { hashStrength = value; }
    }

    /// <summary>Gets or sets the key exchange algorithm used.</summary>
    public ExchangeAlgorithmType KeyExchangeAlgorithm
    {
        get { return keyExchangeAlgorithm; }
        set { keyExchangeAlgorithm = value; }
    }

    /// <summary>Gets or sets the key exchange algorithm strength in bits.</summary>
    public int KeyExchangeStrength
    {
        get { return keyExchangeStrength; }
        set { keyExchangeStrength = value; }
    }

    /// <summary>Returns a string describing the SSL/TLS connection details.</summary>
    public override string ToString()
    {
        return SslProtocol.ToString() + ", " + CipherAlgorithm.ToString() + " (" + cipherStrength + " bit), " + KeyExchangeAlgorithm.ToString() + " (" + keyExchangeStrength + " bit), " + HashAlgorithm.ToString() + " (" + hashStrength + " bit)";
    }
}
