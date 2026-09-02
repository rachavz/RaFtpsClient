using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Runtime.ExceptionServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

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
    private Stream ctrlStream;
    private ControlChannelReader ctrlReader;
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
    private readonly ManualResetEventSlim keepAliveStop = new ManualResetEventSlim(false);
    private int keepAliveTimeout = 20000;
    private readonly SemaphoreSlim ctrlChannelLock = new SemaphoreSlim(1, 1);

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


    // ----- connecting -----------------------------------------------------------------------------

    /// <summary>Connects to an FTP server on the default port with default SSL mode.</summary>
    /// <param name="hostname">The FTP server hostname.</param>
    /// <returns>The server welcome message.</returns>
    public string Connect(string hostname)
    {
        return Connect(hostname, defaultSSLSupportMode);
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
        return Connect(hostname, credential, defaultSSLSupportMode);
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
        return Connect(hostname, DefaultPort(sslSupportMode), credential, sslSupportMode, userValidateServerCertificate, null, 0, 0, 0, null);
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
        credential = PrepareConnection(hostname, credential, sslSupportMode, userValidateServerCertificate, x509ClientCert, sslMinKeyExchangeAlgStrength, sslMinCipherAlgStrength, sslMinHashAlgStrength, timeout, useCtrlEndPointAddressForData, dataConnectionMode);
        bool implicitTls = IsImplicit(sslSupportMode);
        SetupCtrlConnection(hostname, port);
        if (implicitTls)
        {
            SwitchCtrlToSSLMode();
        }
        bannerMessage = GetReply().Message;
        if (!implicitTls)
        {
            SslControlChannelCheckExplicitEncryptionRequest(sslSupportMode);
        }
        FTPReply userReply = HandleCmd(Cmd.User(credential.UserName));
        welcomeMessage = PasswordRequired(userReply) ? HandleCmd(Cmd.Pass(credential.Password)).Message : userReply.Message;
        features = TryFeatures(() => ParseFeatReply(HandleCmd(Cmd.Feat)));
        if (IsControlChannelEncrypted)
        {
            if (!implicitTls)
            {
                SslDataChannelCheckExplicitEncryptionRequest();
                if (!ControlChannelEncryptionRequested(sslSupportMode))
                {
                    SslCtrlChannelCheckRevertToClearText();
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
                HandleCmd(Cmd.Clnt(clntName));
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

    /// <summary>Connects to an FTP server on the default port for the SSL mode, asynchronously.</summary>
    /// <param name="hostname">The FTP server hostname.</param>
    /// <param name="credential">The login credentials, or null for anonymous.</param>
    /// <param name="sslSupportMode">The SSL/TLS support mode.</param>
    /// <param name="userValidateServerCertificate">Custom certificate validation callback, or null for the default policy.</param>
    /// <param name="cancellationToken">Cancels the connection attempt.</param>
    /// <returns>The server welcome message.</returns>
    public Task<string> ConnectAsync(string hostname, NetworkCredential credential, ESSLSupportMode sslSupportMode, RemoteCertificateValidationCallback userValidateServerCertificate = null, CancellationToken cancellationToken = default)
    {
        return ConnectAsync(hostname, DefaultPort(sslSupportMode), credential, sslSupportMode, userValidateServerCertificate, cancellationToken: cancellationToken);
    }

    /// <summary>Connects to an FTP server with all options, asynchronously.</summary>
    /// <param name="hostname">The FTP server hostname.</param>
    /// <param name="port">The FTP server port.</param>
    /// <param name="credential">The login credentials, or null for anonymous.</param>
    /// <param name="sslSupportMode">The SSL/TLS support mode.</param>
    /// <param name="userValidateServerCertificate">Custom certificate validation callback, or null for the default policy.</param>
    /// <param name="x509ClientCert">Client SSL certificate, or null.</param>
    /// <param name="sslMinKeyExchangeAlgStrength">Minimum key exchange algorithm strength, or 0 for no check.</param>
    /// <param name="sslMinCipherAlgStrength">Minimum cipher algorithm strength, or 0 for no check.</param>
    /// <param name="sslMinHashAlgStrength">Minimum hash algorithm strength, or 0 for no check.</param>
    /// <param name="timeout">Timeout in milliseconds for every network operation, or null to keep the current value.</param>
    /// <param name="useCtrlEndPointAddressForData">Whether to use the control endpoint address for data connections.</param>
    /// <param name="dataConnectionMode">Active or passive data connection mode.</param>
    /// <param name="cancellationToken">Cancels the connection attempt.</param>
    /// <returns>The server welcome message.</returns>
    public async Task<string> ConnectAsync(string hostname, int port, NetworkCredential credential, ESSLSupportMode sslSupportMode, RemoteCertificateValidationCallback userValidateServerCertificate = null, X509Certificate x509ClientCert = null, int sslMinKeyExchangeAlgStrength = 0, int sslMinCipherAlgStrength = 0, int sslMinHashAlgStrength = 0, int? timeout = null, bool useCtrlEndPointAddressForData = true, EDataConnectionMode dataConnectionMode = EDataConnectionMode.Passive, CancellationToken cancellationToken = default)
    {
        credential = PrepareConnection(hostname, credential, sslSupportMode, userValidateServerCertificate, x509ClientCert, sslMinKeyExchangeAlgStrength, sslMinCipherAlgStrength, sslMinHashAlgStrength, timeout, useCtrlEndPointAddressForData, dataConnectionMode);
        bool implicitTls = IsImplicit(sslSupportMode);
        await SetupCtrlConnectionAsync(hostname, port, cancellationToken).ConfigureAwait(false);
        if (implicitTls)
        {
            await SwitchCtrlToSSLModeAsync(cancellationToken).ConfigureAwait(false);
        }
        bannerMessage = (await GetReplyAsync(cancellationToken).ConfigureAwait(false)).Message;
        if (!implicitTls)
        {
            await SslControlChannelCheckExplicitEncryptionRequestAsync(sslSupportMode, cancellationToken).ConfigureAwait(false);
        }
        FTPReply userReply = await HandleCmdAsync(Cmd.User(credential.UserName), cancellationToken).ConfigureAwait(false);
        welcomeMessage = PasswordRequired(userReply)
            ? (await HandleCmdAsync(Cmd.Pass(credential.Password), cancellationToken).ConfigureAwait(false)).Message
            : userReply.Message;
        features = await TryFeaturesAsync(async () => ParseFeatReply(await HandleCmdAsync(Cmd.Feat, cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
        if (IsControlChannelEncrypted)
        {
            if (!implicitTls)
            {
                await SslDataChannelCheckExplicitEncryptionRequestAsync(cancellationToken).ConfigureAwait(false);
                if (!ControlChannelEncryptionRequested(sslSupportMode))
                {
                    await SslCtrlChannelCheckRevertToClearTextAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await SslDataChannelImplicitEncryptionRequestAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        try
        {
            if (CheckFeature("CLNT"))
            {
                await HandleCmdAsync(Cmd.Clnt(clntName), cancellationToken).ConfigureAwait(false);
            }
            if (CheckFeature("UTF8"))
            {
                await SetTextEncodingAsync(ETextEncoding.UTF8, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { }
        await SetTransferModeAsync(ETransferMode.Binary, cancellationToken).ConfigureAwait(false);
        return welcomeMessage;
    }

    private static int DefaultPort(ESSLSupportMode sslSupportMode)
    {
        return IsImplicit(sslSupportMode) ? 990 : 21;
    }

    private static bool IsImplicit(ESSLSupportMode sslSupportMode)
    {
        return (sslSupportMode & ESSLSupportMode.Implicit) == ESSLSupportMode.Implicit;
    }

    private static bool ControlChannelEncryptionRequested(ESSLSupportMode sslSupportMode)
    {
        return (sslSupportMode & ESSLSupportMode.ControlChannelRequested) == ESSLSupportMode.ControlChannelRequested;
    }

    // Resets every piece of session state and records the connection parameters. Shared by the
    // synchronous and asynchronous Connect so the two cannot drift on what "a fresh session" means.
    private NetworkCredential PrepareConnection(string hostname, NetworkCredential credential, ESSLSupportMode sslSupportMode, RemoteCertificateValidationCallback userValidateServerCertificate, X509Certificate x509ClientCert, int sslMinKeyExchangeAlgStrength, int sslMinCipherAlgStrength, int sslMinHashAlgStrength, int? timeout, bool useCtrlEndPointAddressForData, EDataConnectionMode dataConnectionMode)
    {
        Close();
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
        this.hostname = hostname;
        sslInfo = null;
        features = null;
        transferMode = ETransferMode.ASCII;
        textEncoding = ETextEncoding.ASCII;
        bannerMessage = null;
        welcomeMessage = null;
        currDirStack.Clear();
        return credential ?? new NetworkCredential(anonUsername, anonPassword);
    }

    // A server without FEAT answers 500/502; that is "no features", not a failed connection.
    private static IList<string> TryFeatures(Func<IList<string>> query)
    {
        try
        {
            return query();
        }
        catch (FTPCommandException)
        {
            return null;
        }
    }

    private static async Task<IList<string>> TryFeaturesAsync(Func<Task<IList<string>>> query)
    {
        try
        {
            return await query().ConfigureAwait(false);
        }
        catch (FTPCommandException)
        {
            return null;
        }
    }

    // ----- keep-alive -----------------------------------------------------------------------------

    private void KeepAliveThreadFunc()
    {
        while (keepAlive)
        {
            try
            {
                ctrlChannelLock.Wait();
                try
                {
                    // Sending NOOP mid-transfer would consume the transfer's own completion reply.
                    if (!IsDataTransferInProgress)
                    {
                        HandleCmdCore(Cmd.Noop, waitForAnswer: true);
                    }
                }
                finally
                {
                    ctrlChannelLock.Release();
                }
            }
            catch (ThreadInterruptedException)
            {
                return;
            }
            catch { }
            // Waiting on the stop signal rather than sleeping: an interrupt that lands inside the
            // socket layer surfaces as an IOException and is consumed there, so a plain Sleep would
            // run its full course before noticing the thread was told to stop.
            if (keepAliveStop.Wait(keepAliveTimeout))
            {
                return;
            }
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
        keepAliveStop.Reset();
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
            keepAliveStop.Set();
            // The interrupt frees a thread parked on the channel lock; a NOOP already inside a
            // socket read is only abandoned once the control channel times out, hence the bounded
            // join on a background thread.
            keepAliveThread.Interrupt();
            keepAliveThread.Join(timeout);
            keepAliveThread = null;
        }
    }

    // ----- TLS negotiation policy -----------------------------------------------------------------
    // The decisions live in the shared helpers; the sync/async pairs only sequence the commands.

    private void SslControlChannelCheckExplicitEncryptionRequest(ESSLSupportMode sslSupportMode)
    {
        if ((sslSupportMode & ESSLSupportMode.CredentialsRequested) != ESSLSupportMode.CredentialsRequested) return;
        try
        {
            HandleCmd(Cmd.Auth(EAuthMechanism.TLS));
        }
        catch (FTPCommandException ex)
        {
            OnAuthRefused(ex, sslSupportMode);
            return;
        }
        SwitchCtrlToSSLMode();
    }

    private async Task SslControlChannelCheckExplicitEncryptionRequestAsync(ESSLSupportMode sslSupportMode, CancellationToken cancellationToken)
    {
        if ((sslSupportMode & ESSLSupportMode.CredentialsRequested) != ESSLSupportMode.CredentialsRequested) return;
        try
        {
            await HandleCmdAsync(Cmd.Auth(EAuthMechanism.TLS), cancellationToken).ConfigureAwait(false);
        }
        catch (FTPCommandException ex)
        {
            OnAuthRefused(ex, sslSupportMode);
            return;
        }
        await SwitchCtrlToSSLModeAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnAuthRefused(FTPCommandException ex, ESSLSupportMode sslSupportMode)
    {
        if ((sslSupportMode & ESSLSupportMode.CredentialsRequired) == ESSLSupportMode.CredentialsRequired)
        {
            if (ex.ErrorCode == 530 || ex.ErrorCode == 534)
            {
                throw new FTPSslException("SSL/TLS connection not supported on server", ex);
            }
            ExceptionDispatchInfo.Capture(ex).Throw();
        }
        sslSupportCurrentMode = ESSLSupportMode.ClearText;
    }

    private void SslDataChannelCheckExplicitEncryptionRequest()
    {
        if (!IsDataChannelEncryptionRequested) return;
        HandleCmd(Cmd.Pbsz(0u));
        try
        {
            HandleCmd(Cmd.Prot(EProtCode.P));
        }
        catch (FTPCommandException ex)
        {
            OnProtRefused(ex);
            HandleCmd(Cmd.Prot(EProtCode.C));
        }
    }

    private async Task SslDataChannelCheckExplicitEncryptionRequestAsync(CancellationToken cancellationToken)
    {
        if (!IsDataChannelEncryptionRequested) return;
        await HandleCmdAsync(Cmd.Pbsz(0u), cancellationToken).ConfigureAwait(false);
        try
        {
            await HandleCmdAsync(Cmd.Prot(EProtCode.P), cancellationToken).ConfigureAwait(false);
        }
        catch (FTPCommandException ex)
        {
            OnProtRefused(ex);
            await HandleCmdAsync(Cmd.Prot(EProtCode.C), cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnProtRefused(FTPCommandException ex)
    {
        if ((sslSupportCurrentMode & ESSLSupportMode.DataChannelRequired) == ESSLSupportMode.DataChannelRequired)
        {
            if (ex.ErrorCode == 534 || ex.ErrorCode == 536)
            {
                throw new FTPSslException("The server policy denies SSL/TLS", ex);
            }
            ExceptionDispatchInfo.Capture(ex).Throw();
        }
        sslSupportCurrentMode &= ~dataChannelBits;
    }

    private void SslCtrlChannelCheckRevertToClearText()
    {
        if (CheckFeature("CCC"))
        {
            HandleCmd(Cmd.Ccc);
            SwitchCtrlToClearMode();
        }
        else
        {
            sslSupportCurrentMode |= ESSLSupportMode.ControlChannelRequested;
        }
    }

    private async Task SslCtrlChannelCheckRevertToClearTextAsync(CancellationToken cancellationToken)
    {
        if (CheckFeature("CCC"))
        {
            await HandleCmdAsync(Cmd.Ccc, cancellationToken).ConfigureAwait(false);
            SwitchCtrlToClearMode();
        }
        else
        {
            sslSupportCurrentMode |= ESSLSupportMode.ControlChannelRequested;
        }
    }

    // The data channel is wrapped in SSL regardless on an implicit connection, so a refused PROT
    // means client and server disagree on the data channel: fail instead of transferring into a
    // mismatched stream.
    private bool ImplicitProtNegotiationApplies => CheckFeature("AUTH") || (CheckFeature("PBSZ") && CheckFeature("PROT"));

    private void SslDataChannelImplicitEncryptionRequest()
    {
        if (!ImplicitProtNegotiationApplies) return;
        try
        {
            HandleCmd(Cmd.Pbsz(0u));
            HandleCmd(Cmd.Prot(EProtCode.P));
        }
        catch (FTPCommandException ex)
        {
            throw new FTPSslException("The server refused to enable data channel encryption", ex);
        }
    }

    private async Task SslDataChannelImplicitEncryptionRequestAsync(CancellationToken cancellationToken)
    {
        if (!ImplicitProtNegotiationApplies) return;
        try
        {
            await HandleCmdAsync(Cmd.Pbsz(0u), cancellationToken).ConfigureAwait(false);
            await HandleCmdAsync(Cmd.Prot(EProtCode.P), cancellationToken).ConfigureAwait(false);
        }
        catch (FTPCommandException ex)
        {
            throw new FTPSslException("The server refused to enable data channel encryption", ex);
        }
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

    // ----- features -------------------------------------------------------------------------------

    /// <summary>Gets the list of features supported by the FTP server.</summary>
    /// <returns>A list of feature strings, or null if not connected.</returns>
    public IList<string> GetFeatures()
    {
        if (features == null) return null;
        return new List<string>(features);
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

    // ----- session settings and simple queries ----------------------------------------------------

    /// <summary>Sets the file transfer mode (ASCII or Binary).</summary>
    /// <param name="transferMode">The desired transfer mode.</param>
    public void SetTransferMode(ETransferMode transferMode)
    {
        HandleCmd(Cmd.Type(RepTypeFor(transferMode)));
        this.transferMode = transferMode;
    }

    /// <summary>Sets the file transfer mode (ASCII or Binary), asynchronously.</summary>
    /// <param name="transferMode">The desired transfer mode.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task SetTransferModeAsync(ETransferMode transferMode, CancellationToken cancellationToken = default)
    {
        await HandleCmdAsync(Cmd.Type(RepTypeFor(transferMode)), cancellationToken).ConfigureAwait(false);
        this.transferMode = transferMode;
    }

    private static ERepType RepTypeFor(ETransferMode transferMode)
    {
        return (transferMode != ETransferMode.ASCII) ? ERepType.I : ERepType.A;
    }

    /// <summary>Sets the text encoding for FTP commands.</summary>
    /// <param name="textEncoding">The desired text encoding.</param>
    public void SetTextEncoding(ETextEncoding textEncoding)
    {
        HandleCmd(Cmd.Opts(Utf8Option(textEncoding)));
        this.textEncoding = textEncoding;
    }

    /// <summary>Sets the text encoding for FTP commands, asynchronously.</summary>
    /// <param name="textEncoding">The desired text encoding.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task SetTextEncodingAsync(ETextEncoding textEncoding, CancellationToken cancellationToken = default)
    {
        await HandleCmdAsync(Cmd.Opts(Utf8Option(textEncoding)), cancellationToken).ConfigureAwait(false);
        this.textEncoding = textEncoding;
    }

    private static string Utf8Option(ETextEncoding textEncoding)
    {
        return "UTF8 " + ((textEncoding == ETextEncoding.UTF8) ? "ON" : "OFF");
    }

    /// <summary>Sets the server language.</summary>
    /// <param name="ietfLanguageTag">The IETF language tag (e.g., "en-US").</param>
    public void SetLanguage(string ietfLanguageTag)
    {
        HandleCmd(Cmd.Lang(ietfLanguageTag));
    }

    /// <summary>Sets the server language, asynchronously.</summary>
    /// <param name="ietfLanguageTag">The IETF language tag (e.g., "en-US").</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public Task SetLanguageAsync(string ietfLanguageTag, CancellationToken cancellationToken = default)
    {
        return HandleCmdAsync(Cmd.Lang(ietfLanguageTag), cancellationToken);
    }

    /// <summary>Gets the remote system type string.</summary>
    /// <returns>The system type (e.g., "UNIX").</returns>
    public string GetSystem()
    {
        return HandleCmd(Cmd.Syst).Message;
    }

    /// <summary>Gets the remote system type string, asynchronously.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The system type (e.g., "UNIX").</returns>
    public async Task<string> GetSystemAsync(CancellationToken cancellationToken = default)
    {
        return (await HandleCmdAsync(Cmd.Syst, cancellationToken).ConfigureAwait(false)).Message;
    }

    /// <summary>Gets the size of a remote file in bytes.</summary>
    /// <param name="remoteFileName">The remote file path.</param>
    /// <returns>The file size, or null if the SIZE command is not supported.</returns>
    public ulong? GetFileTransferSize(string remoteFileName)
    {
        if (!CheckFeature("SIZE")) return null;
        return ParseSizeReply(HandleCmd(Cmd.Size(remoteFileName)));
    }

    /// <summary>Gets the size of a remote file in bytes, asynchronously.</summary>
    /// <param name="remoteFileName">The remote file path.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The file size, or null if the SIZE command is not supported.</returns>
    public async Task<ulong?> GetFileTransferSizeAsync(string remoteFileName, CancellationToken cancellationToken = default)
    {
        if (!CheckFeature("SIZE")) return null;
        return ParseSizeReply(await HandleCmdAsync(Cmd.Size(remoteFileName), cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Gets the modification time of a remote file.</summary>
    /// <param name="remoteFileName">The remote file path.</param>
    /// <returns>The modification time, or null if MDTM is not supported.</returns>
    public DateTime? GetFileModificationTime(string remoteFileName)
    {
        if (!CheckFeature("MDTM")) return null;
        return ParseFTPDateTime(HandleCmd(Cmd.Mdtm(remoteFileName)).Message);
    }

    /// <summary>Gets the modification time of a remote file, asynchronously.</summary>
    /// <param name="remoteFileName">The remote file path.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The modification time, or null if MDTM is not supported.</returns>
    public async Task<DateTime?> GetFileModificationTimeAsync(string remoteFileName, CancellationToken cancellationToken = default)
    {
        if (!CheckFeature("MDTM")) return null;
        return ParseFTPDateTime((await HandleCmdAsync(Cmd.Mdtm(remoteFileName), cancellationToken).ConfigureAwait(false)).Message);
    }

    /// <summary>Sends a custom FTP command and returns the server reply.</summary>
    /// <param name="command">The FTP command string.</param>
    /// <returns>The server reply.</returns>
    public FTPReply SendCustomCommand(string command)
    {
        return HandleCmd(command);
    }

    /// <summary>Sends a custom FTP command and returns the server reply, asynchronously.</summary>
    /// <param name="command">The FTP command string.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The server reply.</returns>
    public Task<FTPReply> SendCustomCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        return HandleCmdAsync(command, cancellationToken);
    }

    // ----- shutdown -------------------------------------------------------------------------------

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
}
