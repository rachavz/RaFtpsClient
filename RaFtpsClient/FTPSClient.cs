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
public sealed class FTPSClient : IDisposable
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

    /// <summary>Opens a stream to download a remote file.</summary>
    /// <param name="remoteFileName">The remote file path.</param>
    /// <returns>A readable FTP stream.</returns>
    public FTPStream GetFile(string remoteFileName)
    {
        SetupDataConnection();
        RetrCmd(remoteFileName);
        return EndStreamCommand(FTPStream.EAllowedOperation.Read);
    }

    /// <summary>Downloads a remote file to a local path.</summary>
    /// <param name="remoteFileName">The remote file path.</param>
    /// <param name="localFileName">The local file path to save to.</param>
    /// <returns>The number of bytes downloaded.</returns>
    public ulong GetFile(string remoteFileName, string localFileName)
    {
        return GetFile(remoteFileName, localFileName, null);
    }

    /// <summary>Downloads a remote file to a local path with progress callback.</summary>
    /// <param name="remoteFileName">The remote file path.</param>
    /// <param name="localFileName">The local file path to save to.</param>
    /// <param name="transferCallback">Progress callback, or null.</param>
    /// <returns>The number of bytes downloaded.</returns>
    public ulong GetFile(string remoteFileName, string localFileName, FileTransferCallback transferCallback)
    {
        ulong num = 0uL;
        ulong? fileTransferSize = null;
        if (transferCallback != null)
        {
            try
            {
                fileTransferSize = GetFileTransferSize(remoteFileName);
            }
            catch (FTPCommandException ex)
            {
                if (ex.ErrorCode == 550)
                {
                    throw new FTPException("Could not get the requested remote file", ex);
                }
                throw;
            }
        }
        using (Stream stream = GetFile(remoteFileName))
        {
            using (FileStream fileStream = new FileStream(localFileName, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] array = new byte[transferBufferSize];
                int num2 = 0;
                do
                {
                    CallTransferCallback(transferCallback, ETransferActions.FileDownloadingStatus, localFileName, remoteFileName, num, fileTransferSize);
                    num2 = stream.Read(array, 0, array.Length);
                    if (num2 > 0)
                    {
                        fileStream.Write(array, 0, num2);
                        num += (ulong)num2;
                    }
                } while (num2 > 0);
                fileStream.Close();
            }
            stream.Close();
        }
        CallTransferCallback(transferCallback, ETransferActions.FileDownloaded, localFileName, remoteFileName, num, fileTransferSize);
        return num;
    }

    /// <summary>Downloads multiple files from a remote directory.</summary>
    /// <param name="remoteDirectoryName">The remote directory, or null for current.</param>
    /// <param name="localDirectoryName">The local directory to save files to.</param>
    /// <param name="filePattern">File name pattern, or null for all files.</param>
    /// <param name="patternStyle">The pattern matching style.</param>
    /// <param name="recursive">Whether to download subdirectories recursively.</param>
    /// <param name="transferCallback">Progress callback, or null.</param>
    public void GetFiles(string remoteDirectoryName, string localDirectoryName, string filePattern, EPatternStyle patternStyle, bool recursive, FileTransferCallback transferCallback)
    {
        GetFiles(remoteDirectoryName, localDirectoryName, filePattern, patternStyle, recursive, transferCallback, new List<string>(), new HashSet<string>());
    }

    private void GetFiles(string remoteDirectoryName, string localDirectoryName, string filePattern, EPatternStyle patternStyle, bool recursive, FileTransferCallback transferCallback, IList<string> paths, ISet<string> visitedRemoteDirs)
    {
        Regex regex = null;
        if (filePattern != null)
        {
            regex = new Regex(GetRegexPattern(filePattern, patternStyle));
        }
        string text = localDirectoryName;
        if (text == null || text.Length == 0)
        {
            text = Directory.GetCurrentDirectory();
        }
        else if (!Directory.Exists(text))
        {
            Directory.CreateDirectory(text);
            CallTransferCallback(transferCallback, ETransferActions.LocalDirectoryCreated, text, null, 0uL, null);
        }
        string text2 = remoteDirectoryName;
        if (text2 == null || text2.Length == 0)
        {
            text2 = GetCurrentDirectory();
        }
        if (!visitedRemoteDirs.Add(text2)) return;
        IList<DirectoryListItem> directoryList = GetDirectoryList(text2);
        CheckSymLinks(text2, directoryList);
        foreach (DirectoryListItem item in directoryList)
        {
            if (!item.IsDirectory && (regex == null || regex.IsMatch(item.Name)))
            {
                string uniquePath = GetUniquePath(paths, Path.Combine(text, PathCheck.GetValidLocalFileName(item.Name)));
                string remoteFileName = CombineRemotePath(text2, item.Name);
                GetFile(remoteFileName, uniquePath, transferCallback);
            }
        }
        if (!recursive) return;
        foreach (DirectoryListItem item2 in directoryList)
        {
            if (!item2.IsDirectory) continue;
            // A symlinked directory is recursed under the path CheckSymLinks resolved it to, so a
            // link pointing back at an ancestor is recognised as already visited instead of looping.
            string remoteDirectoryName2 = (item2.IsSymLink && item2.SymLinkTargetPath != null)
                ? item2.SymLinkTargetPath
                : CombineRemotePath(text2, item2.Name);
            if (visitedRemoteDirs.Contains(remoteDirectoryName2)) continue;
            string uniquePath2 = GetUniquePath(paths, Path.Combine(text, PathCheck.GetValidLocalFileName(item2.Name)));
            GetFiles(remoteDirectoryName2, uniquePath2, filePattern, patternStyle, recursive, transferCallback, paths, visitedRemoteDirs);
        }
    }

    private static string GetUniquePath(IList<string> paths, string localFilePath)
    {
        string text = localFilePath;
        int num = 1;
        while (paths.Contains(text.ToLowerInvariant()))
        {
            text = localFilePath + "_" + num++;
        }
        paths.Add(text.ToLowerInvariant());
        return text;
    }

    /// <summary>Downloads files from the current remote directory.</summary>
    /// <param name="localDirectoryName">The local directory.</param>
    /// <param name="filePattern">File pattern.</param>
    /// <param name="patternStyle">Pattern style.</param>
    /// <param name="recursive">Recursive download.</param>
    public void GetFiles(string localDirectoryName, string filePattern, EPatternStyle patternStyle, bool recursive)
    {
        GetFiles(null, localDirectoryName, filePattern, patternStyle, recursive, null);
    }

    /// <summary>Downloads all files from the current remote directory.</summary>
    /// <param name="localDirectoryName">The local directory.</param>
    /// <param name="recursive">Recursive download.</param>
    public void GetFiles(string localDirectoryName, bool recursive)
    {
        GetFiles(null, localDirectoryName, null, EPatternStyle.Verbatim, recursive, null);
    }

    /// <summary>Downloads all files (non-recursive) from the current remote directory.</summary>
    /// <param name="localDirectoryName">The local directory.</param>
    public void GetFiles(string localDirectoryName)
    {
        GetFiles(null, localDirectoryName, null, EPatternStyle.Verbatim, recursive: false, null);
    }

    /// <summary>Opens a stream to upload a file to the server.</summary>
    /// <param name="remoteFileName">The remote file path.</param>
    /// <returns>A writable FTP stream.</returns>
    public FTPStream PutFile(string remoteFileName)
    {
        SetupDataConnection();
        StorCmd(remoteFileName);
        return EndStreamCommand(FTPStream.EAllowedOperation.Write);
    }

    /// <summary>Uploads a local file to the server.</summary>
    /// <param name="localFileName">The local file path.</param>
    /// <param name="remoteFileName">The remote file path.</param>
    /// <returns>The number of bytes uploaded.</returns>
    public ulong PutFile(string localFileName, string remoteFileName)
    {
        return PutFile(localFileName, remoteFileName, null);
    }

    /// <summary>Uploads a local file with progress callback.</summary>
    /// <param name="localFileName">The local file path.</param>
    /// <param name="remoteFileName">The remote file path.</param>
    /// <param name="transferCallback">Progress callback, or null.</param>
    /// <returns>The number of bytes uploaded.</returns>
    public ulong PutFile(string localFileName, string remoteFileName, FileTransferCallback transferCallback)
    {
        using Stream s = PutFile(remoteFileName);
        return SendFile(localFileName, remoteFileName, s, transferCallback);
    }

    /// <summary>Uploads multiple files from a local directory.</summary>
    /// <param name="localDirectoryName">The local directory.</param>
    /// <param name="remoteDirectoryName">The remote directory, or null for current.</param>
    /// <param name="filePattern">File pattern filter, or null for all.</param>
    /// <param name="patternStyle">Pattern matching style.</param>
    /// <param name="recursive">Whether to upload subdirectories.</param>
    /// <param name="transferCallback">Progress callback, or null.</param>
    public void PutFiles(string localDirectoryName, string remoteDirectoryName, string filePattern, EPatternStyle patternStyle, bool recursive, FileTransferCallback transferCallback)
    {
        Regex regex = null;
        if (filePattern != null)
        {
            regex = new Regex(GetRegexPattern(filePattern, patternStyle));
        }
        string text = null;
        string text2 = remoteDirectoryName;
        if (text2 != null)
        {
            text = GetCurrentDirectory();
            EnsureDir(text2, transferCallback);
        }
        else
        {
            text2 = GetCurrentDirectory();
        }
        string text3 = localDirectoryName;
        if (text3 == null || text3.Length == 0)
        {
            text3 = Directory.GetCurrentDirectory();
        }
        try
        {
            string[] files = Directory.GetFiles(text3);
            foreach (string text4 in files)
            {
                string fileName = Path.GetFileName(text4);
                if (regex == null || regex.IsMatch(fileName))
                {
                    string remoteFileName = CombineRemotePath(text2, fileName);
                    PutFile(text4, remoteFileName, transferCallback);
                }
            }
            if (recursive)
            {
                files = Directory.GetDirectories(text3);
                foreach (string text5 in files)
                {
                    string remoteDirectoryName2 = CombineRemotePath(text2, Path.GetFileName(text5));
                    PutFiles(text5, remoteDirectoryName2, filePattern, patternStyle, recursive, transferCallback);
                }
            }
        }
        finally
        {
            if (text != null)
            {
                SetCurrentDirectory(text);
            }
        }
    }

    /// <summary>Uploads files to the current remote directory.</summary>
    public void PutFiles(string localDirectoryName, string filePattern, EPatternStyle patternStyle, bool recursive)
    {
        PutFiles(localDirectoryName, null, filePattern, patternStyle, recursive, null);
    }

    /// <summary>Uploads all files from a local directory.</summary>
    public void PutFiles(string localDirectoryName, bool recursive)
    {
        PutFiles(localDirectoryName, null, null, EPatternStyle.Verbatim, recursive, null);
    }

    /// <summary>Uploads all files (non-recursive) from a local directory.</summary>
    public void PutFiles(string localDirectoryName)
    {
        PutFiles(localDirectoryName, null, null, EPatternStyle.Verbatim, recursive: false, null);
    }

    /// <summary>Opens a stream to append data to a remote file.</summary>
    /// <param name="remoteFileName">The remote file path.</param>
    /// <returns>A writable FTP stream.</returns>
    public FTPStream AppendFile(string remoteFileName)
    {
        SetupDataConnection();
        AppeCmd(remoteFileName);
        return EndStreamCommand(FTPStream.EAllowedOperation.Write);
    }

    /// <summary>Appends a local file to a remote file.</summary>
    /// <param name="localFileName">The local file path.</param>
    /// <param name="remoteFileName">The remote file path.</param>
    /// <returns>The number of bytes uploaded.</returns>
    public ulong AppendFile(string localFileName, string remoteFileName)
    {
        return AppendFile(localFileName, remoteFileName, null);
    }

    /// <summary>Appends a local file to a remote file with progress callback.</summary>
    public ulong AppendFile(string localFileName, string remoteFileName, FileTransferCallback transferCallback)
    {
        using Stream s = AppendFile(remoteFileName);
        return SendFile(localFileName, remoteFileName, s, transferCallback);
    }

    /// <summary>Opens a stream to upload a uniquely named file on the server.</summary>
    /// <param name="remoteFileName">Outputs the generated remote file name.</param>
    /// <returns>A writable FTP stream.</returns>
    public FTPStream PutUniqueFile(out string remoteFileName)
    {
        SetupDataConnection();
        StouCmd(out remoteFileName);
        return EndStreamCommand(FTPStream.EAllowedOperation.Write);
    }

    /// <summary>Uploads a local file with a unique remote name.</summary>
    public ulong PutUniqueFile(string localFileName, out string remoteFileName)
    {
        return PutUniqueFile(localFileName, out remoteFileName, null);
    }

    /// <summary>Uploads a local file with a unique remote name and progress callback.</summary>
    public ulong PutUniqueFile(string localFileName, out string remoteFileName, FileTransferCallback transferCallback)
    {
        using Stream s = PutUniqueFile(out remoteFileName);
        return SendFile(localFileName, remoteFileName, s, transferCallback);
    }

    /// <summary>Deletes a remote file.</summary>
    /// <param name="remoteFileName">The remote file path.</param>
    public void DeleteFile(string remoteFileName)
    {
        DeleCmd(remoteFileName);
    }

    /// <summary>Renames a remote file.</summary>
    /// <param name="remoteFileNameFrom">The current file name.</param>
    /// <param name="remoteFileNameTo">The new file name.</param>
    public void RenameFile(string remoteFileNameFrom, string remoteFileNameTo)
    {
        RnfrCmd(remoteFileNameFrom);
        RntoCmd(remoteFileNameTo);
    }

    /// <summary>Creates a remote directory.</summary>
    /// <param name="remoteDirName">The directory name.</param>
    public void MakeDir(string remoteDirName)
    {
        MkdCmd(remoteDirName);
    }

    /// <summary>Removes a remote directory.</summary>
    /// <param name="remoteDirName">The directory name.</param>
    public void RemoveDir(string remoteDirName)
    {
        RmdCmd(remoteDirName);
    }

    /// <summary>Changes to the parent directory.</summary>
    public void ChangeToUpperDir()
    {
        CdupCmd();
    }

    /// <summary>Gets a short listing of file names in the current directory.</summary>
    /// <returns>A list of file names.</returns>
    public IList<string> GetShortDirectoryList()
    {
        return GetShortDirectoryList(null);
    }

    /// <summary>Gets a short listing of file names in the specified directory.</summary>
    /// <param name="remoteDirName">The remote directory, or null for current.</param>
    /// <returns>A list of file names.</returns>
    public IList<string> GetShortDirectoryList(string remoteDirName)
    {
        SetupDataConnection();
        NlstCmd(remoteDirName);
        string dataString = GetDataString();
        ReadTransferCompletionReply();
        return new List<string>(dataString.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Gets a detailed directory listing for the current directory.</summary>
    /// <returns>A list of directory items.</returns>
    public IList<DirectoryListItem> GetDirectoryList()
    {
        return GetDirectoryList(null);
    }

    /// <summary>Gets a detailed directory listing for the specified directory.</summary>
    /// <param name="remoteDirName">The remote directory, or null for current.</param>
    /// <returns>A list of directory items.</returns>
    public IList<DirectoryListItem> GetDirectoryList(string remoteDirName)
    {
        return DirectoryListParser.GetDirectoryList(GetDirectoryListUnparsed(remoteDirName));
    }

    /// <summary>Gets the raw directory listing text for the current directory.</summary>
    /// <returns>The raw listing string.</returns>
    public string GetDirectoryListUnparsed()
    {
        return GetDirectoryListUnparsed(null);
    }

    /// <summary>Gets the raw directory listing text for the specified directory.</summary>
    /// <param name="remoteDirName">The remote directory, or null for current.</param>
    /// <returns>The raw listing string.</returns>
    public string GetDirectoryListUnparsed(string remoteDirName)
    {
        SetupDataConnection();
        ListCmd(remoteDirName);
        string dataString = GetDataString();
        ReadTransferCompletionReply();
        // An empty listing is ambiguous: probe with a CWD so a missing directory raises 550 instead
        // of silently looking like an empty one.
        if (dataString.Length == 0 && !string.IsNullOrEmpty(remoteDirName))
        {
            PushCurrentDirectory();
            try
            {
                SetCurrentDirectory(remoteDirName);
            }
            finally
            {
                PopCurrentDirectory();
            }
        }
        return dataString;
    }

    /// <summary>Gets the current remote working directory path.</summary>
    /// <returns>The current directory path.</returns>
    public string GetCurrentDirectory()
    {
        return PwdCmd();
    }

    /// <summary>Saves the current directory and pushes it onto the stack.</summary>
    /// <returns>The saved directory path.</returns>
    public string PushCurrentDirectory()
    {
        string currentDirectory = GetCurrentDirectory();
        currDirStack.Push(currentDirectory);
        return currentDirectory;
    }

    /// <summary>Restores the previously saved directory.</summary>
    /// <returns>The restored directory path.</returns>
    public string PopCurrentDirectory()
    {
        string text = currDirStack.Pop();
        SetCurrentDirectory(text);
        return text;
    }

    /// <summary>Changes the current remote working directory.</summary>
    /// <param name="remoteDirName">The directory path to change to.</param>
    public void SetCurrentDirectory(string remoteDirName)
    {
        CwdCmd(remoteDirName);
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

    private void CheckSymLinks(string remoteDirectoryName, IList<DirectoryListItem> dirList)
    {
        string text = null;
        try
        {
            foreach (DirectoryListItem dir in dirList)
            {
                if (!dir.IsSymLink) continue;
                try
                {
                    if (text == null)
                    {
                        text = GetCurrentDirectory();
                    }
                    string currentDirectory = CombineRemotePath(remoteDirectoryName, dir.Name);
                    SetCurrentDirectory(currentDirectory);
                    dir.IsDirectory = true;
                    // Resolving the link to its absolute path is what lets a recursive download
                    // detect a link that points back into a directory it has already walked.
                    dir.SymLinkTargetPath = GetCurrentDirectory();
                }
                catch (FTPCommandException ex)
                {
                    if (ex.ErrorCode == 550)
                    {
                        dir.IsDirectory = false;
                        continue;
                    }
                    throw;
                }
            }
        }
        finally
        {
            if (text != null)
            {
                SetCurrentDirectory(text);
            }
        }
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

    private static string GetRegexPattern(string filePattern, EPatternStyle patternStyle)
    {
        string text = filePattern;
        if ((uint)patternStyle <= 1u)
        {
            text = "^" + Regex.Escape(filePattern) + "$";
            if (patternStyle == EPatternStyle.Wildcard)
            {
                text = text.Replace("\\*", ".*").Replace("\\?", ".{1}");
            }
        }
        return text;
    }

    private void CallTransferCallback(FileTransferCallback transferCallback, ETransferActions transferAction, string localObjectName, string remoteObjectName, ulong fileTransmittedBytes, ulong? fileTransferSize)
    {
        if (transferCallback != null)
        {
            bool cancel = false;
            transferCallback(this, transferAction, localObjectName, remoteObjectName, fileTransmittedBytes, fileTransferSize, ref cancel);
            if (cancel)
            {
                throw new FTPOperationCancelledException("Operation cancelled by the user");
            }
        }
    }

    private ulong SendFile(string localFileName, string remoteFileName, Stream s, FileTransferCallback transferCallback)
    {
        ulong num = 0uL;
        ulong? fileTransferSize = null;
        if (transferCallback != null)
        {
            fileTransferSize = (ulong)new FileInfo(localFileName).Length;
        }
        using (FileStream fileStream = File.OpenRead(localFileName))
        {
            byte[] array = new byte[transferBufferSize];
            int num2 = 0;
            do
            {
                CallTransferCallback(transferCallback, ETransferActions.FileUploadingStatus, localFileName, remoteFileName, num, fileTransferSize);
                num2 = fileStream.Read(array, 0, array.Length);
                if (num2 > 0)
                {
                    s.Write(array, 0, num2);
                    num += (ulong)num2;
                }
            } while (num2 > 0);
            fileStream.Close();
        }
        s.Close();
        CallTransferCallback(transferCallback, ETransferActions.FileUploaded, localFileName, remoteFileName, num, fileTransferSize);
        return num;
    }

    private void EnsureDir(string remoteDirectoryName, FileTransferCallback transferCallback)
    {
        try
        {
            string currentDirectory = GetCurrentDirectory();
            SetCurrentDirectory(remoteDirectoryName);
            SetCurrentDirectory(currentDirectory);
        }
        catch (FTPCommandException ex)
        {
            if (ex.ErrorCode == 550)
            {
                MakeDir(remoteDirectoryName);
                CallTransferCallback(transferCallback, ETransferActions.RemoteDirectoryCreated, null, remoteDirectoryName, 0uL, null);
                return;
            }
            throw;
        }
    }

    private FTPStream EndStreamCommand(FTPStream.EAllowedOperation allowedOp)
    {
        return new FTPStream(GetDataStream(), allowedOp, delegate
        {
            CloseDataConnection();
            ReadTransferCompletionReply();
        });
    }

    // Only servers that answered the transfer command with a 1xx preliminary reply still owe a
    // completion reply; reading unconditionally would block against the ones that do not.
    private void ReadTransferCompletionReply()
    {
        if (waitingCompletionReply)
        {
            GetReply();
        }
    }

    private Stream GetDataStream()
    {
        if (dataConnectionMode == EDataConnectionMode.Active)
        {
            SetupActiveDataConnectionStep2();
        }
        if ((sslSupportCurrentMode & ESSLSupportMode.DataChannelRequested) == ESSLSupportMode.DataChannelRequested)
        {
            if (dataSslStream == null)
            {
                dataSslStream = CreateSSlStream(dataClient.GetStream(), leaveInnerStreamOpen: false);
            }
            return dataSslStream;
        }
        return dataClient.GetStream();
    }

    private string GetDataString()
    {
        try
        {
            Stream dataStream = GetDataStream();
            StringBuilder stringBuilder = new StringBuilder();
            // A shared Decoder carries partial multi-byte sequences across chunk boundaries; decoding
            // each chunk on its own turns any character straddling one into U+FFFD.
            Decoder decoder = Encoding.UTF8.GetDecoder();
            byte[] array = new byte[transferBufferSize];
            char[] chars = new char[Encoding.UTF8.GetMaxCharCount(array.Length)];
            int num = 0;
            do
            {
                num = dataStream.Read(array, 0, array.Length);
                int charCount = decoder.GetChars(array, 0, num, chars, 0, num == 0);
                stringBuilder.Append(chars, 0, charCount);
            } while (num != 0);
            return stringBuilder.ToString();
        }
        finally
        {
            CloseDataConnection();
        }
    }

    private void SetupCtrlConnection(string hostname, int port)
    {
        CloseCtrlConnection();
        ctrlClient = ConnectWithTimeout(hostname, port);
        Stream stream = ctrlClient.GetStream();
        stream.ReadTimeout = timeout;
        stream.WriteTimeout = timeout;
        SetupCtrlStreamReaderAndWriter(stream);
    }

    // TcpClient's connecting constructor blocks on the OS default, ignoring the configured timeout.
    private TcpClient ConnectWithTimeout(string host, int port)
    {
        TcpClient client = new TcpClient();
        try
        {
            IAsyncResult ar = client.BeginConnect(host, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeout))
            {
                throw new FTPException("Timeout connecting to " + host + ":" + port);
            }
            client.EndConnect(ar);
            return client;
        }
        catch
        {
            client.Close();
            throw;
        }
    }

    private void SetupCtrlStreamReaderAndWriter(Stream s)
    {
        if (ctrlSw != null)
        {
            ctrlSw.Flush();
        }
        Encoding encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        ctrlSr = new StreamReader(s, encoding);
        ctrlSw = new StreamWriter(s, encoding);
        ctrlSw.NewLine = "\r\n";
    }

    private int SetupActiveDataConnectionStep1()
    {
        CloseDataConnection();
        // Bind to the control connection's own local address so the listener matches the address
        // family we advertise, and so an IPv6 control channel gets an IPv6 listener.
        IPEndPoint localEP = new IPEndPoint(((IPEndPoint)ctrlClient.Client.LocalEndPoint).Address, 0);
        activeDataConnListener = new TcpListener(localEP);
        activeDataConnListener.Start();
        return (activeDataConnListener.LocalEndpoint as IPEndPoint).Port;
    }

    private void SetupActiveDataConnectionStep2()
    {
        try
        {
            int micros = (timeout > int.MaxValue / 1000) ? int.MaxValue : timeout * 1000;
            if (!activeDataConnListener.Server.Poll(micros, SelectMode.SelectRead))
            {
                throw new FTPException("Timeout waiting for the server to open the data connection");
            }
            dataClient = activeDataConnListener.AcceptTcpClient();
            SetDataClientTimeout();
        }
        finally
        {
            StopActiveDataConnListener();
        }
    }

    private void StopActiveDataConnListener()
    {
        activeDataConnListener.Stop();
        activeDataConnListener = null;
    }

    private void SetupPassiveDataConnection(IPEndPoint dataEndPoint)
    {
        CloseDataConnection();
        IPAddress iPAddress = ((!useCtrlEndPointAddressForData) ? dataEndPoint.Address : (ctrlClient.Client.RemoteEndPoint as IPEndPoint).Address);
        dataClient = new TcpClient(iPAddress.ToString(), dataEndPoint.Port);
        SetDataClientTimeout();
    }

    private void SetDataClientTimeout()
    {
        NetworkStream stream = dataClient.GetStream();
        stream.ReadTimeout = timeout;
        stream.WriteTimeout = timeout;
    }

    private void SwitchCtrlToSSLMode()
    {
        ctrlSslStream = CreateSSlStream(ctrlClient.GetStream(), leaveInnerStreamOpen: true);
        SetupCtrlStreamReaderAndWriter(ctrlSslStream);
        SetSslInfo(ctrlSslStream);
    }

    private SslStream CreateSSlStream(Stream s, bool leaveInnerStreamOpen)
    {
        SslStream sslStream = new SslStream(s, leaveInnerStreamOpen, ValidateServerCertificate, null);
        sslStream.ReadTimeout = timeout;
        sslStream.WriteTimeout = timeout;
        X509CertificateCollection x509CertificateCollection = new X509CertificateCollection();
        if (sslClientCert != null)
        {
            x509CertificateCollection.Add(sslClientCert);
        }
        sslStream.AuthenticateAsClient(hostname, x509CertificateCollection, sslProtocols, sslCheckCertRevocation);
        CheckSslAlgorithmsStrength(sslStream);
        return sslStream;
    }

    private void CheckSslAlgorithmsStrength(SslStream sslStream)
    {
        if (sslMinKeyExchangeAlgStrength > 0 && sslStream.KeyExchangeStrength < sslMinKeyExchangeAlgStrength)
        {
            throw new FTPSslException("The SSL/TSL key exchange algorithm strength does not fulfill the requirements: " + sslStream.KeyExchangeStrength);
        }
        if (sslMinCipherAlgStrength > 0 && sslStream.CipherStrength < sslMinCipherAlgStrength)
        {
            throw new FTPSslException("The SSL/TSL cipher algorithm strength does not fulfill the requirements: " + sslStream.CipherStrength);
        }
        if (sslMinHashAlgStrength > 0 && sslStream.HashStrength < sslMinHashAlgStrength)
        {
            throw new FTPSslException("The SSL/TSL hash algorithm strength does not fulfill the requirements: " + sslStream.HashStrength);
        }
    }

    private void SwitchCtrlToClearMode()
    {
        ctrlSslStream.Close();
        ctrlSslStream = null;
        SetupCtrlStreamReaderAndWriter(ctrlClient.GetStream());
    }

    private bool ValidateServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
    {
        // The data channel handshake reuses the decision made for the control channel, so the two
        // certificates are compared by their full encoding: X509Certificate.Equals only compares the
        // issuer name and serial number, which a substituted certificate can trivially reproduce.
        byte[] rawData = certificate?.Export(X509ContentType.Cert);
        if (rawData != null && RawDataEquals(sslServerCertRawData, rawData))
        {
            return true;
        }
        bool flag = true;
        if (userValidateServerCertificate != null)
        {
            flag = userValidateServerCertificate(this, certificate, chain, sslPolicyErrors);
        }
        else if (sslPolicyErrors != SslPolicyErrors.None)
        {
            flag = false;
        }
        if (flag)
        {
            sslServerCertRawData = rawData;
        }
        return flag;
    }

    private static bool RawDataEquals(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    private static string ParsePwdReply(FTPReply reply)
    {
        int num = reply.Message.IndexOf('"');
        if (num < 0) throw new FTPProtocolException(reply);
        int num2 = reply.Message.IndexOf('"', num + 1);
        if (num2 < 0) throw new FTPProtocolException(reply);
        return reply.Message.Substring(num + 1, num2 - num - 1);
    }

    private static IPEndPoint ParsePasvReply(FTPReply reply)
    {
        int num = reply.Message.IndexOf('(');
        if (num < 0) throw new FTPProtocolException(reply);
        int num2 = reply.Message.IndexOf(')', num + 1);
        if (num2 < 0) throw new FTPProtocolException(reply);
        string[] array = reply.Message.Substring(num + 1, num2 - num - 1).Split(new char[1] { ',' });
        if (array.Length != 6) throw new FTPProtocolException(reply);
        byte[] array2 = new byte[4];
        for (num = 0; num < array2.Length; num++)
        {
            array2[num] = byte.Parse(array[num]);
        }
        int port = byte.Parse(array[4]) * 256 + byte.Parse(array[5]);
        return new IPEndPoint(new IPAddress(array2), port);
    }

    private IPEndPoint ParseEpsvReply(FTPReply reply)
    {
        string[] array = reply.Message.Split(new char[1] { '|' });
        if (array.Length != 5) throw new FTPProtocolException(reply);
        int port = int.Parse(array[3]);
        // EPSV returns only a port: the address is always the server's, i.e. the control channel's
        // remote end, never the client's own local endpoint.
        return new IPEndPoint(((IPEndPoint)ctrlClient.Client.RemoteEndPoint).Address, port);
    }

    private FTPReply HandleCmd(string command)
    {
        return HandleCmd(command, waitForAnswer: true);
    }

    private FTPReply HandleCmd(string command, bool waitForAnswer)
    {
        lock (ctrlChannelLock)
        {
            CheckConnection();
            CheckCommandInjection(command);
            ctrlSw.WriteLine(command);
            ctrlSw.Flush();
            this.LogCommand?.Invoke(this, new LogCommandEventArgs(MaskCredentials(command)));
            if (!waitForAnswer) return null;
            return GetReply();
        }
    }

    private static string MaskCredentials(string command)
    {
        if (command.StartsWith("PASS ", StringComparison.OrdinalIgnoreCase))
        {
            return "PASS ****";
        }
        return command;
    }

    private void CheckConnection()
    {
        if (ctrlClient == null) throw new FTPException("Not connected");
    }

    private static void CheckCommandInjection(string command)
    {
        // A bare CR or LF is enough: most servers accept either as a command terminator, so a remote
        // name carrying one would smuggle a second command onto the control channel.
        if (command.IndexOf('\r') >= 0 || command.IndexOf('\n') >= 0)
        {
            throw new FTPException("Newlines not allowed in command text");
        }
    }

    private static string CombineRemotePath(string path1, string path2)
    {
        return (path1.EndsWith("/") ? path1 : (path1 + "/")) + path2;
    }

    private FTPReply GetReply()
    {
        lock (ctrlChannelLock)
        {
            return GetReplyCore();
        }
    }

    private FTPReply GetReplyCore()
    {
        try
        {
            FTPReply fTPReply = new FTPReply();
            bool flag = false;
            do
            {
                string text = ctrlSr.ReadLine();
                if (text == null)
                {
                    throw new FTPException("The server closed the control connection");
                }
                Match match = Regex.Match(text, "^([0-9]{3})([\\s\\-])(.*)$");
                if (match.Success)
                {
                    int num = int.Parse(match.Groups[1].Value);
                    string value = match.Groups[3].Value;
                    flag = match.Groups[2].Value == " ";
                    if (fTPReply.Code == 0)
                    {
                        fTPReply.Code = num;
                        fTPReply.Message = value;
                        continue;
                    }
                    if (fTPReply.Code != num)
                    {
                        throw new FTPReplyParseException(text);
                    }
                    fTPReply.Message = fTPReply.Message + "\r\n" + value;
                }
                else
                {
                    if (fTPReply.Code == 0)
                    {
                        throw new FTPReplyParseException(text);
                    }
                    fTPReply.Message = fTPReply.Message + "\r\n" + text.TrimStart(Array.Empty<char>());
                }
            } while (!flag);
            waitingCompletionReply = fTPReply.Code < 200;
            this.LogServerReply?.Invoke(this, new LogServerReplyEventArgs(fTPReply));
            if (fTPReply.Code >= 400)
            {
                throw new FTPCommandException(fTPReply);
            }
            return fTPReply;
        }
        catch (Exception)
        {
            waitingCompletionReply = false;
            throw;
        }
    }

    private void CloseCtrlConnection()
    {
        if (ctrlClient != null)
        {
            try
            {
                QuitCmd(waitForAnswer: false);
            }
            catch (Exception) { }
            if (ctrlSslStream != null)
            {
                ctrlSslStream.Close();
                ctrlSslStream = null;
            }
            ctrlSr.Close();
            ctrlSr = null;
            ctrlSw.Close();
            ctrlSw = null;
            ctrlClient.Close();
            ctrlClient = null;
            waitingCompletionReply = false;
        }
    }

    private void CloseDataConnection()
    {
        if (dataClient != null)
        {
            if (dataSslStream != null)
            {
                dataSslStream.Close();
                dataSslStream = null;
            }
            dataClient.Close();
            dataClient = null;
        }
        if (activeDataConnListener != null)
        {
            StopActiveDataConnListener();
        }
    }

    private void StorCmd(string fileName) { HandleCmd("STOR " + fileName); }

    private void StouCmd(out string fileName)
    {
        FTPReply reply = HandleCmd("STOU");
        fileName = ParseStouReply(reply);
    }

    private static string ParseStouReply(FTPReply reply)
    {
        int num = reply.Message.LastIndexOf(' ');
        if (num < 0) throw new FTPProtocolException(reply);
        return reply.Message.Substring(num + 1);
    }

    private void AppeCmd(string fileName) { HandleCmd("APPE " + fileName); }
    private void RetrCmd(string fileName) { HandleCmd("RETR " + fileName); }
    private void DeleCmd(string fileName) { HandleCmd("DELE " + fileName); }
    private void MkdCmd(string dirName) { HandleCmd("MKD " + dirName); }
    private void RmdCmd(string dirName) { HandleCmd("RMD " + dirName); }
    private void CdupCmd() { HandleCmd("CDUP"); }

    private string SystCmd() { return HandleCmd("SYST").Message; }

    private void TypeCmd(ERepType repType, string param2)
    {
        HandleCmd("TYPE " + repType.ToString() + ((param2 != null) ? (" " + param2) : ""));
    }

    private string PwdCmd() { return ParsePwdReply(HandleCmd("PWD")); }
    private void CwdCmd(string dirName) { HandleCmd("CWD " + dirName); }
    // 230 (already logged in) and 232 (authorised by security data exchange) both mean no PASS is due.
    private bool UserCmd(string userName, out string message)
    {
        FTPReply reply = HandleCmd("USER " + userName);
        message = reply.Message;
        return reply.Code != 230 && reply.Code != 232;
    }
    private string PassCmd(string password) { return HandleCmd("PASS " + password).Message; }

    private void PortCmd()
    {
        int num = SetupActiveDataConnectionStep1();
        byte[] addressBytes = (ctrlClient.Client.LocalEndPoint as IPEndPoint).Address.GetAddressBytes();
        string text = string.Format("{0},{1},{2},{3},{4},{5}", new object[6]
        {
            addressBytes[0], addressBytes[1], addressBytes[2], addressBytes[3],
            num / 256, num % 256
        });
        HandleCmd("PORT " + text);
    }

    // PORT can only carry a 4-byte address; EPRT (RFC 2428) is the IPv6 equivalent.
    private void EprtCmd()
    {
        int num = SetupActiveDataConnectionStep1();
        IPAddress address = (ctrlClient.Client.LocalEndPoint as IPEndPoint).Address;
        int protocol = ((address.AddressFamily == AddressFamily.InterNetworkV6) ? 2 : 1);
        HandleCmd("EPRT |" + protocol + "|" + address + "|" + num + "|");
    }

    private void PasvCmd()
    {
        IPEndPoint dataEndPoint = ParsePasvReply(HandleCmd("PASV"));
        SetupPassiveDataConnection(dataEndPoint);
    }

    private AddressFamily GetCtrlConnAddressFamily()
    {
        return ((IPEndPoint)ctrlClient.Client.LocalEndPoint).AddressFamily;
    }

    private void SetupDataConnection()
    {
        bool isIPv4 = GetCtrlConnAddressFamily() == AddressFamily.InterNetwork;
        if (dataConnectionMode == EDataConnectionMode.Active)
        {
            if (isIPv4)
            {
                PortCmd();
            }
            else
            {
                EprtCmd();
            }
        }
        else if (isIPv4)
        {
            PasvCmd();
        }
        else
        {
            EpsvCmd();
        }
    }

    private void ListCmd(string dirName) { HandleCmd("LIST" + ((dirName != null) ? (" " + dirName) : "")); }
    private void NlstCmd(string dirName) { HandleCmd("NLST" + ((dirName != null) ? (" " + dirName) : "")); }
    private void RnfrCmd(string fileOldName) { HandleCmd("RNFR " + fileOldName); }
    private void RntoCmd(string fileNewName) { HandleCmd("RNTO " + fileNewName); }
    private void QuitCmd(bool waitForAnswer) { HandleCmd("QUIT", waitForAnswer); }
    private void NoopCmd() { HandleCmd("NOOP"); }

    private void AuthCmd(EAuthMechanism authMech)
    {
        HandleCmd("AUTH " + authMech);
        SwitchCtrlToSSLMode();
    }

    private void CccCmd()
    {
        HandleCmd("CCC");
        SwitchCtrlToClearMode();
    }

    private void ProtCmd(EProtCode protCode) { HandleCmd("PROT " + protCode); }
    private void PbszCmd(uint maxSize) { HandleCmd("PBSZ " + maxSize); }

    private IList<string> FeatCmd()
    {
        List<string> list = new List<string>(HandleCmd("FEAT").Message.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        // The reply is bracketed by an introductory line and a closing "End" line, neither of which
        // is a feature; a server that answers with anything shorter advertises nothing.
        if (list.Count < 3) return new List<string>();
        list.RemoveAt(list.Count - 1);
        list.RemoveAt(0);
        for (int i = 0; i < list.Count; i++)
        {
            list[i] = list[i].Trim();
        }
        return list;
    }

    private void OptsCmd(string command) { HandleCmd("OPTS " + command); }

    private void EpsvCmd()
    {
        FTPReply reply = HandleCmd("EPSV");
        IPEndPoint dataEndPoint = ParseEpsvReply(reply);
        SetupPassiveDataConnection(dataEndPoint);
    }

    private void LangCmd(string ietfLanguageTag) { HandleCmd("LANG" + ((ietfLanguageTag != null) ? (" " + ietfLanguageTag) : "")); }

    private DateTime MdtmCmd(string fileName) { return ParseFTPDateTime(HandleCmd("MDTM " + fileName).Message); }
    private ulong SizeCmd(string fileName) { return ulong.Parse(HandleCmd("SIZE " + fileName).Message); }

    private static DateTime ParseFTPDateTime(string message)
    {
        return DateTime.ParseExact(message, "yyyyMMddHHmmss.FFF", CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.AssumeUniversal);
    }

    private void ClntCmd(string name) { HandleCmd("CLNT " + name); }
}
