using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace RaFtpsClient;

/// <summary>
/// FTP/FTPS client for connecting to FTP servers with optional SSL/TLS encryption.
/// Supports explicit and implicit FTPS, active and passive modes, file transfers, and directory operations.
/// </summary>
public sealed partial class FTPSClient : IDisposable
{
    private enum EProtCode
    {
        C,
        S,
        E,
        P
    }

    private enum EAuthMechanism
    {
        TLS
    }

    private enum ERepType
    {
        A,
        E,
        I,
        L
    }

    private TcpClient ctrlClient;
    private StreamReader ctrlSr;
    private StreamWriter ctrlSw;
    private SslStream ctrlSslStream;
    private TcpClient dataClient;
    private SslStream dataSslStream;
    private EDataConnectionMode dataConnectionMode = EDataConnectionMode.Passive;
    private bool useCtrlEndPointAddressForData = true;
    private bool waitingCompletionReply;
    private string hostname;
    private const string anonUsername = "anonymous";
    private const string anonPassword = "anonymous@FTPSClient.org";
    private const string clntName = "AlexFTPS";
    private const int transferBufferSize = 81920;
    private const ESSLSupportMode defaultSSLSupportMode = (ESSLSupportMode)11;
    private ESSLSupportMode sslSupportRequestedMode;
    private ESSLSupportMode sslSupportCurrentMode;
    private byte[] sslServerCertRawData;
    private X509Certificate sslClientCert;
    private SslInfo sslInfo;
    private int sslMinKeyExchangeAlgStrength;
    private int sslMinCipherAlgStrength;
    private int sslMinHashAlgStrength;
    private bool sslCheckCertRevocation = true;
    private SslProtocols sslProtocols = SslProtocols.None;
    private RemoteCertificateValidationCallback userValidateServerCertificate;
    private int timeout = 120000;
    private IList<string> features;
    private ETransferMode transferMode;
    private ETextEncoding textEncoding;
    private string welcomeMessage;
    private string bannerMessage;
    private Stack<string> currDirStack = new Stack<string>();
    private TcpListener activeDataConnListener;
    private Thread keepAliveThread;
    private volatile bool keepAlive = true;
    private int keepAliveTimeout = 20000;
    private readonly object ctrlChannelLock = new object();

    // ESSLSupportMode is a cumulative bit set rather than orthogonal flags: bit 3 marks the data
    // channel as requested and bit 4 as required. Clearing both drops the data channel requirement
    // without disturbing the credential and control channel bits.
    private const ESSLSupportMode dataChannelBits = (ESSLSupportMode)0x18;

    /// <summary>Gets the requested SSL/TLS support mode.</summary>
    public ESSLSupportMode SslSupportRequestedMode => sslSupportRequestedMode;
    /// <summary>Gets the current SSL/TLS support mode after negotiation.</summary>
    public ESSLSupportMode SslSupportCurrentMode => sslSupportCurrentMode;
    /// <summary>Gets the text encoding used for communications.</summary>
    public ETextEncoding TextEncoding => textEncoding;
    /// <summary>Gets the current file transfer mode.</summary>
    public ETransferMode TransferMode => transferMode;
    /// <summary>Gets the server welcome message received after login.</summary>
    public string WelcomeMessage => welcomeMessage;
    /// <summary>Gets the server banner message.</summary>
    public string BannerMessage => bannerMessage;

    /// <summary>Gets the remote SSL certificate, or null if not connected with SSL.</summary>
    public X509Certificate RemoteCertificate
    {
        get
        {
            if (ctrlSslStream == null) return null;
            return ctrlSslStream.RemoteCertificate;
        }
    }

    /// <summary>Gets the SSL connection details, or null if not connected with SSL.</summary>
    public SslInfo SslInfo => sslInfo;

    /// <summary>Gets the local SSL certificate, or null if not connected with SSL.</summary>
    public X509Certificate LocalCertificate
    {
        get
        {
            if (ctrlSslStream == null) return null;
            return ctrlSslStream.LocalCertificate;
        }
    }

    /// <summary>Gets whether the keep-alive thread is running.</summary>
    public bool KeepAliveStarted => keepAliveThread != null;

    /// <summary>
    /// Gets or sets the interval in milliseconds between keep-alive NOOP commands. Defaults to 20000.
    /// </summary>
    public int KeepAliveTimeout
    {
        get { return keepAliveTimeout; }
        set { keepAliveTimeout = value; }
    }

    /// <summary>
    /// Gets or sets the SSL/TLS protocol versions offered during the handshake. Defaults to
    /// <see cref="System.Security.Authentication.SslProtocols.None"/>, letting the operating system
    /// negotiate the strongest version both ends support. Set before calling Connect.
    /// </summary>
    public SslProtocols SslProtocols
    {
        get { return sslProtocols; }
        set { sslProtocols = value; }
    }

    /// <summary>
    /// Gets or sets whether the server certificate is checked against its revocation list.
    /// Enabled by default; disable only when the CRL/OCSP endpoint is unreachable.
    /// Set before calling Connect.
    /// </summary>
    public bool SslCheckCertRevocation
    {
        get { return sslCheckCertRevocation; }
        set { sslCheckCertRevocation = value; }
    }

    private bool IsControlChannelEncrypted => ctrlSslStream != null;
    private bool IsDataChannelOpen => dataClient != null;

    // The control channel is off limits to the keep-alive thread from the moment a data connection
    // is being set up until the transfer's completion reply has been read.
    private bool IsDataTransferInProgress => dataClient != null || activeDataConnListener != null || waitingCompletionReply;

    /// <summary>Event raised when an FTP command is sent.</summary>
    public event LogCommandEventHandler LogCommand;
    /// <summary>Event raised when an FTP server reply is received.</summary>
    public event LogServerReplyEventHandler LogServerReply;

    /// <summary>Connects to an FTP server on the default port with default SSL mode.</summary>
    /// <param name="hostname">The FTP server hostname.</param>
    /// <returns>The server welcome message.</returns>
    public string Connect(string hostname)
    {
        return Connect(hostname, (ESSLSupportMode)11);
    }

    /// <summary>Connects to an FTP server on the default port with the specified SSL mode.</summary>
    /// <param name="hostname">The FTP server hostname.</param>
    /// <param name="sslSupportMode">The SSL/TLS support mode.</param>
    /// <returns>The server welcome message.</returns>
    public string Connect(string hostname, ESSLSupportMode sslSupportMode)
    {
        return Connect(hostname, null, sslSupportMode);
    }

    /// <summary>Connects to an FTP server on the default port with credentials and default SSL mode.</summary>
    /// <param name="hostname">The FTP server hostname.</param>
    /// <param name="credential">The login credentials, or null for anonymous.</param>
    /// <returns>The server welcome message.</returns>
    public string Connect(string hostname, NetworkCredential credential)
    {
        return Connect(hostname, credential, (ESSLSupportMode)11);
    }

    /// <summary>Connects to an FTP server on the default port with credentials and SSL mode.</summary>
    /// <param name="hostname">The FTP server hostname.</param>
    /// <param name="credential">The login credentials, or null for anonymous.</param>
    /// <param name="sslSupportMode">The SSL/TLS support mode.</param>
    /// <returns>The server welcome message.</returns>
    public string Connect(string hostname, NetworkCredential credential, ESSLSupportMode sslSupportMode)
    {
        return Connect(hostname, credential, sslSupportMode, null);
    }

    /// <summary>Connects to an FTP server on the default port with full SSL configuration.</summary>
    /// <param name="hostname">The FTP server hostname.</param>
    /// <param name="credential">The login credentials, or null for anonymous.</param>
    /// <param name="sslSupportMode">The SSL/TLS support mode.</param>
    /// <param name="userValidateServerCertificate">Custom certificate validation callback.</param>
    /// <returns>The server welcome message.</returns>
    public string Connect(string hostname, NetworkCredential credential, ESSLSupportMode sslSupportMode, RemoteCertificateValidationCallback userValidateServerCertificate)
    {
        int port = (((sslSupportMode & ESSLSupportMode.Implicit) == ESSLSupportMode.Implicit) ? 990 : 21);
        return Connect(hostname, port, credential, sslSupportMode, userValidateServerCertificate, null, 0, 0, 0, null);
    }

    /// <summary>Connects to an FTP server with credentials, SSL mode, and certificate validation.</summary>
    /// <param name="hostname">The FTP server hostname.</param>
    /// <param name="port">The FTP server port.</param>
    /// <param name="credential">The login credentials, or null for anonymous.</param>
    /// <param name="sslSupportMode">The SSL/TLS support mode.</param>
    /// <param name="userValidateServerCertificate">Custom certificate validation callback.</param>
    /// <returns>The server welcome message.</returns>
    public string Connect(string hostname, int port, NetworkCredential credential, ESSLSupportMode sslSupportMode, RemoteCertificateValidationCallback userValidateServerCertificate)
    {
        return Connect(hostname, port, credential, sslSupportMode, userValidateServerCertificate, null, 0, 0, 0, null);
    }

    /// <summary>Connects to an FTP server with full SSL configuration including client certificate.</summary>
    /// <param name="hostname">The FTP server hostname.</param>
    /// <param name="port">The FTP server port.</param>
    /// <param name="credential">The login credentials.</param>
    /// <param name="sslSupportMode">The SSL/TLS support mode.</param>
    /// <param name="userValidateServerCertificate">Custom certificate validation callback.</param>
    /// <param name="x509ClientCert">Client SSL certificate.</param>
    /// <param name="sslMinKeyExchangeAlgStrength">Minimum key exchange algorithm strength.</param>
    /// <param name="sslMinCipherAlgStrength">Minimum cipher algorithm strength.</param>
    /// <param name="sslMinHashAlgStrength">Minimum hash algorithm strength.</param>
    /// <param name="timeout">Connection timeout in milliseconds, or null for default.</param>
    /// <returns>The server welcome message.</returns>
    public string Connect(string hostname, int port, NetworkCredential credential, ESSLSupportMode sslSupportMode, RemoteCertificateValidationCallback userValidateServerCertificate, X509Certificate x509ClientCert, int sslMinKeyExchangeAlgStrength, int sslMinCipherAlgStrength, int sslMinHashAlgStrength, int? timeout)
    {
        return Connect(hostname, port, credential, sslSupportMode, userValidateServerCertificate, x509ClientCert, sslMinKeyExchangeAlgStrength, sslMinCipherAlgStrength, sslMinHashAlgStrength, timeout, useCtrlEndPointAddressForData: true);
    }

    /// <summary>Connects to an FTP server with all options including data connection endpoint control.</summary>
    /// <param name="hostname">The FTP server hostname.</param>
    /// <param name="port">The FTP server port.</param>
    /// <param name="credential">The login credentials.</param>
    /// <param name="sslSupportMode">The SSL/TLS support mode.</param>
    /// <param name="userValidateServerCertificate">Custom certificate validation callback.</param>
    /// <param name="x509ClientCert">Client SSL certificate.</param>
    /// <param name="sslMinKeyExchangeAlgStrength">Minimum key exchange algorithm strength.</param>
    /// <param name="sslMinCipherAlgStrength">Minimum cipher algorithm strength.</param>
    /// <param name="sslMinHashAlgStrength">Minimum hash algorithm strength.</param>
    /// <param name="timeout">Connection timeout in milliseconds.</param>
    /// <param name="useCtrlEndPointAddressForData">Whether to use the control endpoint address for data connections.</param>
    /// <returns>The server welcome message.</returns>
    public string Connect(string hostname, int port, NetworkCredential credential, ESSLSupportMode sslSupportMode, RemoteCertificateValidationCallback userValidateServerCertificate, X509Certificate x509ClientCert, int sslMinKeyExchangeAlgStrength, int sslMinCipherAlgStrength, int sslMinHashAlgStrength, int? timeout, bool useCtrlEndPointAddressForData)
    {
        return Connect(hostname, port, credential, sslSupportMode, userValidateServerCertificate, x509ClientCert, sslMinKeyExchangeAlgStrength, sslMinCipherAlgStrength, sslMinHashAlgStrength, timeout, useCtrlEndPointAddressForData, EDataConnectionMode.Passive);
    }

    /// <summary>Connects to an FTP server with all options including data connection mode.</summary>
    /// <param name="hostname">The FTP server hostname.</param>
    /// <param name="port">The FTP server port.</param>
    /// <param name="credential">The login credentials.</param>
    /// <param name="sslSupportMode">The SSL/TLS support mode.</param>
    /// <param name="userValidateServerCertificate">Custom certificate validation callback.</param>
    /// <param name="x509ClientCert">Client SSL certificate.</param>
    /// <param name="sslMinKeyExchangeAlgStrength">Minimum key exchange algorithm strength.</param>
    /// <param name="sslMinCipherAlgStrength">Minimum cipher algorithm strength.</param>
    /// <param name="sslMinHashAlgStrength">Minimum hash algorithm strength.</param>
    /// <param name="timeout">Connection timeout in milliseconds.</param>
    /// <param name="useCtrlEndPointAddressForData">Whether to use the control endpoint address for data connections.</param>
    /// <param name="dataConnectionMode">Active or passive data connection mode.</param>
    /// <returns>The server welcome message.</returns>
    public string Connect(string hostname, int port, NetworkCredential credential, ESSLSupportMode sslSupportMode, RemoteCertificateValidationCallback userValidateServerCertificate, X509Certificate x509ClientCert, int sslMinKeyExchangeAlgStrength, int sslMinCipherAlgStrength, int sslMinHashAlgStrength, int? timeout, bool useCtrlEndPointAddressForData, EDataConnectionMode dataConnectionMode)
    {
        Close();
        if (credential == null)
        {
            credential = new NetworkCredential("anonymous", "anonymous@FTPSClient.org");
        }
        if (timeout.HasValue)
        {
            this.timeout = timeout.Value;
        }
        sslClientCert = x509ClientCert;
        this.userValidateServerCertificate = userValidateServerCertificate;
        this.sslMinKeyExchangeAlgStrength = sslMinKeyExchangeAlgStrength;
        this.sslMinCipherAlgStrength = sslMinCipherAlgStrength;
        this.sslMinHashAlgStrength = sslMinHashAlgStrength;
        sslSupportRequestedMode = sslSupportMode;
        sslSupportCurrentMode = sslSupportMode;
        this.useCtrlEndPointAddressForData = useCtrlEndPointAddressForData;
        this.dataConnectionMode = dataConnectionMode;
        sslInfo = null;
        features = null;
        transferMode = ETransferMode.ASCII;
        textEncoding = ETextEncoding.ASCII;
        bannerMessage = null;
        welcomeMessage = null;
        currDirStack.Clear();
        SetupCtrlConnection(hostname, port);
        this.hostname = hostname;
        bool flag = (sslSupportMode & ESSLSupportMode.Implicit) == ESSLSupportMode.Implicit;
        if (flag)
        {
            SwitchCtrlToSSLMode();
        }
        bannerMessage = GetReply().Message;
        if (!flag)
        {
            SslControlChannelCheckExplicitEncryptionRequest(sslSupportMode);
        }
        if (UserCmd(credential.UserName, out welcomeMessage))
        {
            welcomeMessage = PassCmd(credential.Password);
        }
        GetFeaturesFromServer();
        if (IsControlChannelEncrypted)
        {
            if (!flag)
            {
                SslDataChannelCheckExplicitEncryptionRequest();
                if ((sslSupportMode & ESSLSupportMode.ControlChannelRequested) != ESSLSupportMode.ControlChannelRequested)
                {
                    SSlCtrlChannelCheckRevertToClearText();
                }
            }
            else
            {
                SslDataChannelImplicitEncryptionRequest();
            }
        }
        try
        {
            if (CheckFeature("CLNT"))
            {
                ClntCmd("AlexFTPS");
            }
            if (CheckFeature("UTF8"))
            {
                SetTextEncoding(ETextEncoding.UTF8);
            }
        }
        catch (Exception) { }
        SetTransferMode(ETransferMode.Binary);
        return welcomeMessage;
    }

    private void KeepAliveThreadFunc()
    {
        while (keepAlive)
        {
            try
            {
                lock (ctrlChannelLock)
                {
                    // Sending NOOP mid-transfer would consume the transfer's own completion reply.
                    if (!IsDataTransferInProgress)
                    {
                        NoopCmd();
                    }
                }
                Thread.Sleep(keepAliveTimeout);
            }
            catch (ThreadInterruptedException)
            {
                return;
            }
            catch { }
        }
    }

    /// <summary>Starts sending periodic NOOP commands to keep the connection alive.</summary>
    /// <exception cref="FTPException">If already started or not connected.</exception>
    public void StartKeepAlive()
    {
        CheckConnection();
        if (keepAliveThread != null)
        {
            throw new FTPException("KeepAlive already started");
        }
        keepAlive = true;
        keepAliveThread = new Thread(KeepAliveThreadFunc);
        keepAliveThread.IsBackground = true;
        keepAliveThread.Start();
    }

    /// <summary>Stops the keep-alive thread if running.</summary>
    public void StopKeepAlive()
    {
        if (keepAliveThread != null)
        {
            keepAlive = false;
            keepAliveThread.Interrupt();
            // Interrupt does not unblock a socket read, so a NOOP already in flight is only
            // abandoned once the control channel times out; the thread is a background one.
            keepAliveThread.Join(timeout);
            keepAliveThread = null;
        }
    }

    private void SslDataChannelImplicitEncryptionRequest()
    {
        if (!CheckFeature("AUTH") && !(CheckFeature("PBSZ") && CheckFeature("PROT")))
        {
            return;
        }
        try
        {
            PbszCmd(0u);
            ProtCmd(EProtCode.P);
        }
        catch (FTPCommandException ex)
        {
            // The data channel is wrapped in SSL regardless on an implicit connection, so a refused
            // PROT means client and server disagree on the data channel: fail instead of transferring
            // into a mismatched stream.
            throw new FTPSslException("The server refused to enable data channel encryption", ex);
        }
    }

    /// <summary>Sets the file transfer mode (ASCII or Binary).</summary>
    /// <param name="transferMode">The desired transfer mode.</param>
    public void SetTransferMode(ETransferMode transferMode)
    {
        TypeCmd((transferMode != ETransferMode.ASCII) ? ERepType.I : ERepType.A, null);
        this.transferMode = transferMode;
    }

    /// <summary>Gets the list of features supported by the FTP server.</summary>
    /// <returns>A list of feature strings, or null if not connected.</returns>
    public IList<string> GetFeatures()
    {
        if (features == null) return null;
        return new List<string>(features);
    }

    /// <summary>Gets the size of a remote file in bytes.</summary>
    /// <param name="remoteFileName">The remote file path.</param>
    /// <returns>The file size, or null if the SIZE command is not supported.</returns>
    public ulong? GetFileTransferSize(string remoteFileName)
    {
        if (!CheckFeature("SIZE")) return null;
        return SizeCmd(remoteFileName);
    }

    /// <summary>Gets the remote system type string.</summary>
    /// <returns>The system type (e.g., "UNIX").</returns>
    public string GetSystem()
    {
        return SystCmd();
    }

    /// <summary>Gets the modification time of a remote file.</summary>
    /// <param name="remoteFileName">The remote file path.</param>
    /// <returns>The modification time, or null if MDTM is not supported.</returns>
    public DateTime? GetFileModificationTime(string remoteFileName)
    {
        if (!CheckFeature("MDTM")) return null;
        return MdtmCmd(remoteFileName);
    }

    /// <summary>Sets the server language.</summary>
    /// <param name="ietfLanguageTag">The IETF language tag (e.g., "en-US").</param>
    public void SetLanguage(string ietfLanguageTag)
    {
        LangCmd(ietfLanguageTag);
    }

    /// <summary>Sets the text encoding for FTP commands.</summary>
    /// <param name="textEncoding">The desired text encoding.</param>
    public void SetTextEncoding(ETextEncoding textEncoding)
    {
        OptsCmd("UTF8 " + ((textEncoding == ETextEncoding.UTF8) ? "ON" : "OFF"));
        this.textEncoding = textEncoding;
    }

    /// <summary>Sends a custom FTP command and returns the server reply.</summary>
    /// <param name="command">The FTP command string.</param>
    /// <returns>The server reply.</returns>
    public FTPReply SendCustomCommand(string command)
    {
        return HandleCmd(command);
    }

    /// <summary>Closes the FTP connection and releases all resources.</summary>
    public void Close()
    {
        StopKeepAlive();
        CloseDataConnection();
        CloseCtrlConnection();
        sslServerCertRawData = null;
        sslClientCert = null;
    }

    /// <summary>Disposes the FTP client, closing the connection.</summary>
    public void Dispose()
    {
        Close();
    }

    private void SetSslInfo(SslStream sslStream)
    {
        sslInfo = new SslInfo
        {
            SslProtocol = sslStream.SslProtocol,
            CipherAlgorithm = sslStream.CipherAlgorithm,
            CipherStrength = sslStream.CipherStrength,
            HashAlgorithm = sslStream.HashAlgorithm,
            HashStrength = sslStream.HashStrength,
            KeyExchangeAlgorithm = sslStream.KeyExchangeAlgorithm,
            KeyExchangeStrength = sslStream.KeyExchangeStrength
        };
    }

    private void SSlCtrlChannelCheckRevertToClearText()
    {
        if (CheckFeature("CCC"))
        {
            CccCmd();
        }
        else
        {
            sslSupportCurrentMode |= ESSLSupportMode.ControlChannelRequested;
        }
    }

    private bool CheckFeature(string feature)
    {
        if (features == null) return false;
        foreach (string f in features)
        {
            if (f.Equals(feature, StringComparison.OrdinalIgnoreCase)) return true;
            // FEAT lines carry parameters ("AUTH TLS;SSL", "MDTM 20031111015806", "REST STREAM"),
            // so an exact match would miss most real servers.
            if (f.Length > feature.Length && f.StartsWith(feature, StringComparison.OrdinalIgnoreCase)
                && (f[feature.Length] == ' ' || f[feature.Length] == ';'))
            {
                return true;
            }
        }
        return false;
    }

    private void GetFeaturesFromServer()
    {
        try
        {
            features = FeatCmd();
        }
        catch (FTPCommandException)
        {
            features = null;
        }
    }

    private void SslDataChannelCheckExplicitEncryptionRequest()
    {
        if ((sslSupportCurrentMode & ESSLSupportMode.DataChannelRequested) != ESSLSupportMode.DataChannelRequested) return;
        PbszCmd(0u);
        try
        {
            ProtCmd(EProtCode.P);
        }
        catch (FTPCommandException ex)
        {
            if ((sslSupportCurrentMode & ESSLSupportMode.DataChannelRequired) == ESSLSupportMode.DataChannelRequired)
            {
                if (ex.ErrorCode == 534 || ex.ErrorCode == 536)
                {
                    throw new FTPSslException("The server policy denies SSL/TLS", ex);
                }
                throw;
            }
            sslSupportCurrentMode &= ~dataChannelBits;
            ProtCmd(EProtCode.C);
        }
    }

    private void SslControlChannelCheckExplicitEncryptionRequest(ESSLSupportMode sslSupportMode)
    {
        if ((sslSupportMode & ESSLSupportMode.CredentialsRequested) != ESSLSupportMode.CredentialsRequested) return;
        try
        {
            AuthCmd(EAuthMechanism.TLS);
        }
        catch (FTPCommandException ex)
        {
            if ((sslSupportMode & ESSLSupportMode.CredentialsRequired) == ESSLSupportMode.CredentialsRequired)
            {
                if (ex.ErrorCode == 530 || ex.ErrorCode == 534)
                {
                    throw new FTPSslException("SSL/TLS connection not supported on server", ex);
                }
                throw;
            }
            sslSupportCurrentMode = ESSLSupportMode.ClearText;
        }
    }
}
