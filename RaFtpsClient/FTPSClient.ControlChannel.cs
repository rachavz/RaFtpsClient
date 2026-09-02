using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RaFtpsClient;

// Control channel transport: connection, TLS wrapping, command/reply exchange and its lock.
// Every operation exists in a synchronous and an asynchronous form over the same state; the two
// are kept adjacent so a change to one is visibly missing from the other.
public sealed partial class FTPSClient
{
    private static readonly Encoding controlEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    // ----- connection -----------------------------------------------------------------------------

    private void SetupCtrlConnection(string hostname, int port)
    {
        CloseCtrlConnection();
        ctrlClient = ConnectWithTimeout(hostname, port);
        BindControlStream(ctrlClient.GetStream());
    }

    private async Task SetupCtrlConnectionAsync(string hostname, int port, CancellationToken cancellationToken)
    {
        CloseCtrlConnection();
        ctrlClient = await ConnectWithTimeoutAsync(hostname, port, cancellationToken).ConfigureAwait(false);
        BindControlStream(ctrlClient.GetStream());
    }

    private static IPAddress[] ResolveHost(string host)
    {
        return IPAddress.TryParse(host, out IPAddress literal) ? new IPAddress[1] { literal } : Dns.GetHostAddresses(host);
    }

    private static async Task<IPAddress[]> ResolveHostAsync(string host)
    {
        return IPAddress.TryParse(host, out IPAddress literal)
            ? new IPAddress[1] { literal }
            : await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
    }

    // TcpClient's connecting constructor blocks on the OS default, ignoring the configured timeout.
    // One socket per address family: a parameterless TcpClient would open a dual mode IPv6 socket
    // even for an IPv4 server, and the control channel's family is what decides between PASV/PORT
    // and EPSV/EPRT.
    private TcpClient ConnectWithTimeout(string host, int port)
    {
        IPAddress[] addresses = ResolveHost(host);
        if (addresses.Length == 0)
        {
            throw new FTPException("Could not resolve " + host);
        }
        Exception lastError = null;
        foreach (IPAddress address in addresses)
        {
            TcpClient client = new TcpClient(address.AddressFamily);
            try
            {
                IAsyncResult ar = client.BeginConnect(address, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeout))
                {
                    throw new FTPException("Timeout connecting to " + host + ":" + port);
                }
                client.EndConnect(ar);
                return client;
            }
            catch (Exception ex)
            {
                client.Close();
                lastError = ex;
            }
        }
        throw new FTPException("Could not connect to " + host + ":" + port, lastError);
    }

    private async Task<TcpClient> ConnectWithTimeoutAsync(string host, int port, CancellationToken cancellationToken)
    {
        IPAddress[] addresses = await ResolveHostAsync(host).ConfigureAwait(false);
        if (addresses.Length == 0)
        {
            throw new FTPException("Could not resolve " + host);
        }
        Exception lastError = null;
        foreach (IPAddress address in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TcpClient client = new TcpClient(address.AddressFamily);
            try
            {
                using (CancellationTokenSource scope = TimeoutScope(cancellationToken))
                {
                    Task connect = client.ConnectAsync(address, port);
                    Task finished = await Task.WhenAny(connect, Task.Delay(Timeout.Infinite, scope.Token)).ConfigureAwait(false);
                    if (finished != connect)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new FTPException("Timeout connecting to " + host + ":" + port);
                    }
                    await connect.ConfigureAwait(false);
                    return client;
                }
            }
            catch (OperationCanceledException)
            {
                client.Close();
                throw;
            }
            catch (Exception ex)
            {
                client.Close();
                lastError = ex;
            }
        }
        throw new FTPException("Could not connect to " + host + ":" + port, lastError);
    }

    // The configured timeout applies to asynchronous operations through a linked token, since
    // ReadTimeout/WriteTimeout only govern the synchronous calls.
    private CancellationTokenSource TimeoutScope(CancellationToken cancellationToken)
    {
        CancellationTokenSource scope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        scope.CancelAfter(timeout);
        return scope;
    }

    private void BindControlStream(Stream s)
    {
        s.ReadTimeout = timeout;
        s.WriteTimeout = timeout;
        ctrlStream = s;
        ctrlReader = new ControlChannelReader(s);
    }

    // ----- TLS ------------------------------------------------------------------------------------

    private void SwitchCtrlToSSLMode()
    {
        ctrlSslStream = CreateSslStream(ctrlClient.GetStream(), leaveInnerStreamOpen: true);
        BindControlStream(ctrlSslStream);
        SetSslInfo(ctrlSslStream);
    }

    private async Task SwitchCtrlToSSLModeAsync(CancellationToken cancellationToken)
    {
        ctrlSslStream = await CreateSslStreamAsync(ctrlClient.GetStream(), leaveInnerStreamOpen: true, cancellationToken).ConfigureAwait(false);
        BindControlStream(ctrlSslStream);
        SetSslInfo(ctrlSslStream);
    }

    private void SwitchCtrlToClearMode()
    {
        ctrlSslStream.Close();
        ctrlSslStream = null;
        BindControlStream(ctrlClient.GetStream());
    }

    private SslStream NewSslStream(Stream s, bool leaveInnerStreamOpen, out X509CertificateCollection clientCertificates)
    {
        SslStream sslStream = new SslStream(s, leaveInnerStreamOpen, ValidateServerCertificate, null);
        sslStream.ReadTimeout = timeout;
        sslStream.WriteTimeout = timeout;
        clientCertificates = new X509CertificateCollection();
        if (sslClientCert != null)
        {
            clientCertificates.Add(sslClientCert);
        }
        return sslStream;
    }

    private SslStream CreateSslStream(Stream s, bool leaveInnerStreamOpen)
    {
        SslStream sslStream = NewSslStream(s, leaveInnerStreamOpen, out X509CertificateCollection clientCertificates);
        sslStream.AuthenticateAsClient(hostname, clientCertificates, sslProtocols, sslCheckCertRevocation);
        CheckSslAlgorithmsStrength(sslStream);
        return sslStream;
    }

    private async Task<SslStream> CreateSslStreamAsync(Stream s, bool leaveInnerStreamOpen, CancellationToken cancellationToken)
    {
        SslStream sslStream = NewSslStream(s, leaveInnerStreamOpen, out X509CertificateCollection clientCertificates);
        // The handshake overload available on netstandard2.0 takes no token; tearing the inner
        // stream down is the only way to abandon it early.
        using (CancellationTokenSource scope = TimeoutScope(cancellationToken))
        using (scope.Token.Register(state => ((Stream)state).Dispose(), s))
        {
            try
            {
                await sslStream.AuthenticateAsClientAsync(hostname, clientCertificates, sslProtocols, sslCheckCertRevocation).ConfigureAwait(false);
            }
            catch (Exception) when (scope.Token.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new FTPException("Timeout during the TLS handshake");
            }
        }
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

    // ----- command / reply exchange ---------------------------------------------------------------

    // The lock spans a command and its reply. SemaphoreSlim is not re-entrant, so everything that
    // runs while it is held calls the *Core variants, never HandleCmd/GetReply.

    private FTPReply HandleCmd(string command)
    {
        return HandleCmd(command, waitForAnswer: true);
    }

    private FTPReply HandleCmd(string command, bool waitForAnswer)
    {
        ctrlChannelLock.Wait();
        try
        {
            return HandleCmdCore(command, waitForAnswer);
        }
        finally
        {
            ctrlChannelLock.Release();
        }
    }

    private Task<FTPReply> HandleCmdAsync(string command, CancellationToken cancellationToken)
    {
        return HandleCmdAsync(command, waitForAnswer: true, cancellationToken);
    }

    private async Task<FTPReply> HandleCmdAsync(string command, bool waitForAnswer, CancellationToken cancellationToken)
    {
        await ctrlChannelLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await HandleCmdCoreAsync(command, waitForAnswer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ctrlChannelLock.Release();
        }
    }

    private FTPReply HandleCmdCore(string command, bool waitForAnswer)
    {
        byte[] bytes = PrepareCommand(command);
        ctrlStream.Write(bytes, 0, bytes.Length);
        ctrlStream.Flush();
        this.LogCommand?.Invoke(this, new LogCommandEventArgs(MaskCredentials(command)));
        if (!waitForAnswer) return null;
        return GetReplyCore();
    }

    private async Task<FTPReply> HandleCmdCoreAsync(string command, bool waitForAnswer, CancellationToken cancellationToken)
    {
        byte[] bytes = PrepareCommand(command);
        using (CancellationTokenSource scope = TimeoutScope(cancellationToken))
        {
            try
            {
                await ctrlStream.WriteAsync(bytes, 0, bytes.Length, scope.Token).ConfigureAwait(false);
                await ctrlStream.FlushAsync(scope.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new FTPException("Timeout sending the command to the server");
            }
        }
        this.LogCommand?.Invoke(this, new LogCommandEventArgs(MaskCredentials(command)));
        if (!waitForAnswer) return null;
        return await GetReplyCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    private byte[] PrepareCommand(string command)
    {
        CheckConnection();
        CheckCommandInjection(command);
        return controlEncoding.GetBytes(command + "\r\n");
    }

    internal static string MaskCredentials(string command)
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

    internal static void CheckCommandInjection(string command)
    {
        // A bare CR or LF is enough: most servers accept either as a command terminator, so a remote
        // name carrying one would smuggle a second command onto the control channel.
        if (command.IndexOf('\r') >= 0 || command.IndexOf('\n') >= 0)
        {
            throw new FTPException("Newlines not allowed in command text");
        }
    }

    private FTPReply GetReply()
    {
        ctrlChannelLock.Wait();
        try
        {
            return GetReplyCore();
        }
        finally
        {
            ctrlChannelLock.Release();
        }
    }

    private async Task<FTPReply> GetReplyAsync(CancellationToken cancellationToken)
    {
        await ctrlChannelLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await GetReplyCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ctrlChannelLock.Release();
        }
    }

    private FTPReply GetReplyCore()
    {
        try
        {
            ReplyAccumulator reply = new ReplyAccumulator();
            do
            {
                string line = ctrlReader.ReadLine();
                if (line == null) throw new FTPException("The server closed the control connection");
                reply.Add(line);
            } while (!reply.IsComplete);
            return FinishReply(reply.Reply);
        }
        catch (Exception)
        {
            waitingCompletionReply = false;
            throw;
        }
    }

    private async Task<FTPReply> GetReplyCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            ReplyAccumulator reply = new ReplyAccumulator();
            using (CancellationTokenSource scope = TimeoutScope(cancellationToken))
            {
                do
                {
                    string line;
                    try
                    {
                        line = await ctrlReader.ReadLineAsync(scope.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new FTPException("Timeout waiting for the server reply");
                    }
                    if (line == null) throw new FTPException("The server closed the control connection");
                    reply.Add(line);
                } while (!reply.IsComplete);
            }
            return FinishReply(reply.Reply);
        }
        catch (Exception)
        {
            waitingCompletionReply = false;
            throw;
        }
    }

    private FTPReply FinishReply(FTPReply reply)
    {
        waitingCompletionReply = reply.Code < 200;
        this.LogServerReply?.Invoke(this, new LogServerReplyEventArgs(reply));
        if (reply.Code >= 400)
        {
            throw new FTPCommandException(reply);
        }
        return reply;
    }

    /// <summary>Assembles a possibly multi-line reply (RFC 959 §4.2) one line at a time.</summary>
    private sealed class ReplyAccumulator
    {
        public FTPReply Reply { get; } = new FTPReply();
        public bool IsComplete { get; private set; }

        // A reply line is three digits, then a space (final line) or a hyphen (continuation).
        private static bool TryParseReplyLine(string line, out int code, out bool isFinal, out string text)
        {
            code = 0;
            isFinal = false;
            text = null;
            if (line.Length < 4 || !IsDigit(line[0]) || !IsDigit(line[1]) || !IsDigit(line[2])) return false;
            char separator = line[3];
            if (separator != '-' && !char.IsWhiteSpace(separator)) return false;
            code = (line[0] - '0') * 100 + (line[1] - '0') * 10 + (line[2] - '0');
            isFinal = separator == ' ';
            text = line.Substring(4);
            return true;
        }

        private static bool IsDigit(char c) => c >= '0' && c <= '9';

        public void Add(string line)
        {
            if (TryParseReplyLine(line, out int code, out bool isFinal, out string text))
            {
                IsComplete = isFinal;
                if (Reply.Code == 0)
                {
                    Reply.Code = code;
                    Reply.Message = text;
                    return;
                }
                if (Reply.Code != code)
                {
                    throw new FTPReplyParseException(line);
                }
                Reply.Message = Reply.Message + "\r\n" + text;
                return;
            }
            if (Reply.Code == 0)
            {
                throw new FTPReplyParseException(line);
            }
            Reply.Message = Reply.Message + "\r\n" + line.TrimStart(Array.Empty<char>());
        }
    }

    private void CloseCtrlConnection()
    {
        if (ctrlClient != null)
        {
            try
            {
                HandleCmd(Cmd.Quit, waitForAnswer: false);
            }
            catch (Exception) { }
            if (ctrlSslStream != null)
            {
                ctrlSslStream.Close();
                ctrlSslStream = null;
            }
            ctrlReader = null;
            ctrlStream = null;
            ctrlClient.Close();
            ctrlClient = null;
            waitingCompletionReply = false;
        }
    }
}
