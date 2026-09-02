using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace RaFtpsClient.Tests;

internal enum FakeTlsMode
{
    /// <summary>No TLS at all; AUTH is rejected.</summary>
    None,
    /// <summary>TLS offered through AUTH TLS on a clear-text control channel.</summary>
    Explicit,
    /// <summary>The control channel is TLS from the first byte, before the banner.</summary>
    Implicit
}

/// <summary>
/// An in-process FTP server speaking just enough of RFC 959 and RFC 4217 to drive
/// <see cref="FTPSClient"/> over a loopback socket.
/// </summary>
internal sealed class FakeFtpServer : IDisposable
{
    private readonly TcpListener listener;
    private readonly Thread acceptThread;
    private readonly List<string> receivedCommands = new();
    private readonly object sync = new();
    private volatile bool running = true;

    public int Port { get; }
    public IPAddress Address { get; }

    /// <summary>Text returned for LIST. NLST returns <see cref="ShortListingText"/>.</summary>
    public string ListingText { get; set; } = "";
    public string ShortListingText { get; set; } = "";
    /// <summary>Payload returned for RETR.</summary>
    public byte[] FileContent { get; set; } = Array.Empty<byte>();
    /// <summary>Bytes the client sent with the most recent STOR/APPE/STOU.</summary>
    public byte[] LastUpload { get; private set; }
    public IList<string> Features { get; set; } = new List<string> { "SIZE", "MDTM", "UTF8", "CLNT" };
    public string WelcomeMessage { get; set; } = "User logged in, proceed.";
    public string CurrentDirectory { get; set; } = "/home/user";
    public string StouName { get; set; } = "/home/user/unique-name.txt";
    /// <summary>Reply code sent for USER; 230 skips the password exchange.</summary>
    public int UserReplyCode { get; set; } = 331;
    /// <summary>Commands answered with a canned error instead of the normal reply.</summary>
    public Dictionary<string, string> ErrorReplies { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Skip the 1xx preliminary reply on transfers, as a few servers do.</summary>
    public bool SuppressPreliminaryReply { get; set; }
    /// <summary>Drop the control connection instead of answering this command.</summary>
    public string DropConnectionOn { get; set; }
    /// <summary>Never answer this command, leaving the client waiting for a reply.</summary>
    public string HangOn { get; set; }

    public FakeTlsMode TlsMode { get; set; } = FakeTlsMode.None;
    /// <summary>Certificate presented to the client; defaults to the shared self-signed one.</summary>
    public X509Certificate2 Certificate { get; set; } = TestCertificate.Server;
    /// <summary>Certificate presented on data connections; defaults to <see cref="Certificate"/>.
    /// Set it to a different one to simulate a substituted certificate mid-session.</summary>
    public X509Certificate2 DataCertificate { get; set; }
    /// <summary>TLS versions the server will accept. None lets the OS decide.</summary>
    public SslProtocols ServerSslProtocols { get; set; } = SslProtocols.None;
    /// <summary>Answer PROT with a refusal, to exercise the client's downgrade handling.</summary>
    public bool RejectProt { get; set; }
    /// <summary>True once a data connection has actually completed a TLS handshake.</summary>
    public bool DataChannelWasEncrypted { get; private set; }
    /// <summary>Transfers the client tore down before the payload was complete.</summary>
    public int AbortedTransfers { get; private set; }

    public FakeFtpServer(bool useIPv6 = false)
    {
        Address = useIPv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        listener = new TcpListener(Address, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptThread = new Thread(AcceptLoop) { IsBackground = true };
        acceptThread.Start();
    }

    public IReadOnlyList<string> ReceivedCommands
    {
        get { lock (sync) return receivedCommands.ToArray(); }
    }

    public bool Received(string command)
    {
        lock (sync) return receivedCommands.Any(c => c.Equals(command, StringComparison.OrdinalIgnoreCase));
    }

    public int CountOf(string verb)
    {
        lock (sync) return receivedCommands.Count(c => c.Split(' ')[0].Equals(verb, StringComparison.OrdinalIgnoreCase));
    }

    private void AcceptLoop()
    {
        while (running)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch { return; }
            var t = new Thread(() => Serve(client)) { IsBackground = true };
            t.Start();
        }
    }

    private SslStream WrapAsServer(Stream inner, bool leaveInnerStreamOpen, X509Certificate2 certificate = null)
    {
        var ssl = new SslStream(inner, leaveInnerStreamOpen);
        ssl.AuthenticateAsServer(certificate ?? Certificate, clientCertificateRequired: false,
            ServerSslProtocols, checkCertificateRevocation: false);
        return ssl;
    }

    private void Serve(TcpClient client)
    {
        TcpListener dataListener = null;
        bool activeModeRequested = false;
        bool dataProtected = false;
        try
        {
            using (client)
            {
                Stream controlStream = client.GetStream();
                if (TlsMode == FakeTlsMode.Implicit)
                {
                    controlStream = WrapAsServer(controlStream, leaveInnerStreamOpen: false);
                }

                var encoding = new UTF8Encoding(false);
                var reader = new StreamReader(controlStream, encoding);
                var writer = new StreamWriter(controlStream, encoding) { NewLine = "\r\n", AutoFlush = true };
                writer.WriteLine("220 FakeFtpServer ready.");

                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    lock (sync) receivedCommands.Add(line);

                    string verb = line.Split(' ')[0].ToUpperInvariant();
                    string arg = line.Length > verb.Length ? line.Substring(verb.Length).Trim() : "";

                    if (DropConnectionOn != null && verb.Equals(DropConnectionOn, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                    if (HangOn != null && verb.Equals(HangOn, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (ErrorReplies.TryGetValue(verb, out string error))
                    {
                        writer.WriteLine(error);
                        continue;
                    }

                    switch (verb)
                    {
                        case "AUTH":
                            if (TlsMode != FakeTlsMode.Explicit)
                            {
                                writer.WriteLine("534 Request denied for policy reasons.");
                                break;
                            }
                            // The acknowledgement goes out in clear text; the handshake follows it.
                            writer.WriteLine("234 AUTH " + arg + " OK.");
                            controlStream = WrapAsServer(client.GetStream(), leaveInnerStreamOpen: true);
                            reader = new StreamReader(controlStream, encoding);
                            writer = new StreamWriter(controlStream, encoding) { NewLine = "\r\n", AutoFlush = true };
                            break;
                        case "PBSZ":
                            writer.WriteLine("200 PBSZ=0");
                            break;
                        case "PROT":
                            if (RejectProt)
                            {
                                writer.WriteLine("534 Request denied for policy reasons.");
                                break;
                            }
                            dataProtected = arg.Equals("P", StringComparison.OrdinalIgnoreCase);
                            writer.WriteLine("200 PROT command successful.");
                            break;
                        case "USER":
                            writer.WriteLine(UserReplyCode == 230
                                ? "230 " + WelcomeMessage
                                : UserReplyCode + " Password required.");
                            break;
                        case "PASS":
                            writer.WriteLine("230 " + WelcomeMessage);
                            break;
                        case "FEAT":
                            writer.WriteLine("211-Extensions supported:");
                            foreach (string f in Features) writer.WriteLine(" " + f);
                            writer.WriteLine("211 End");
                            break;
                        case "PWD":
                            writer.WriteLine("257 \"" + CurrentDirectory + "\" is the current directory.");
                            break;
                        case "CWD":
                            CurrentDirectory = arg.StartsWith("/") ? arg : CurrentDirectory.TrimEnd('/') + "/" + arg;
                            writer.WriteLine("250 CWD command successful.");
                            break;
                        case "TYPE":
                        case "OPTS":
                        case "CLNT":
                        case "NOOP":
                        case "MODE":
                        case "LANG":
                            writer.WriteLine("200 " + verb + " command successful.");
                            break;
                        case "CDUP":
                            CurrentDirectory = CurrentDirectory.Substring(0, Math.Max(1, CurrentDirectory.TrimEnd('/').LastIndexOf('/')));
                            writer.WriteLine("250 CDUP command successful.");
                            break;
                        case "SYST":
                            writer.WriteLine("215 UNIX Type: L8");
                            break;
                        case "SIZE":
                            writer.WriteLine("213 " + FileContent.Length);
                            break;
                        case "MDTM":
                            writer.WriteLine("213 20220531120000");
                            break;
                        case "DELE":
                        case "MKD":
                        case "RMD":
                        case "RNFR":
                        case "RNTO":
                            writer.WriteLine("250 " + verb + " command successful.");
                            break;
                        case "PORT":
                        case "EPRT":
                            // Accepted, but the fake server never dials back: active-mode tests exist
                            // to prove the client stops waiting instead of blocking forever.
                            activeModeRequested = true;
                            writer.WriteLine("200 " + verb + " command successful.");
                            break;
                        case "PASV":
                        {
                            dataListener?.Stop();
                            dataListener = new TcpListener(IPAddress.Loopback, 0);
                            dataListener.Start();
                            int p = ((IPEndPoint)dataListener.LocalEndpoint).Port;
                            byte[] a = IPAddress.Loopback.GetAddressBytes();
                            writer.WriteLine($"227 Entering Passive Mode ({a[0]},{a[1]},{a[2]},{a[3]},{p / 256},{p % 256}).");
                            break;
                        }
                        case "EPSV":
                        {
                            dataListener?.Stop();
                            dataListener = new TcpListener(Address, 0);
                            dataListener.Start();
                            int p = ((IPEndPoint)dataListener.LocalEndpoint).Port;
                            writer.WriteLine($"229 Entering Extended Passive Mode (|||{p}|)");
                            break;
                        }
                        case "LIST":
                            TransferOut(writer, dataListener, Encoding.UTF8.GetBytes(ListingText), dataProtected, activeModeRequested);
                            dataListener = null;
                            break;
                        case "NLST":
                            TransferOut(writer, dataListener, Encoding.UTF8.GetBytes(ShortListingText), dataProtected, activeModeRequested);
                            dataListener = null;
                            break;
                        case "RETR":
                            TransferOut(writer, dataListener, FileContent, dataProtected, activeModeRequested);
                            dataListener = null;
                            break;
                        case "STOR":
                        case "APPE":
                            TransferIn(writer, dataListener, "150 Opening data connection.", dataProtected);
                            dataListener = null;
                            break;
                        case "STOU":
                            // RFC 1123 puts the generated name in the preliminary reply.
                            TransferIn(writer, dataListener, "150 FILE: " + StouName, dataProtected);
                            dataListener = null;
                            break;
                        case "QUIT":
                            writer.WriteLine("221 Goodbye.");
                            return;
                        default:
                            writer.WriteLine("500 Unknown command.");
                            break;
                    }
                }
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (AuthenticationException) { }
        finally
        {
            dataListener?.Stop();
        }
    }

    private Stream AcceptDataStream(TcpClient data, bool dataProtected)
    {
        if (!dataProtected)
        {
            return data.GetStream();
        }
        Stream ssl = WrapAsServer(data.GetStream(), leaveInnerStreamOpen: false, DataCertificate);
        DataChannelWasEncrypted = true;
        return ssl;
    }

    private void TransferOut(StreamWriter writer, TcpListener dataListener, byte[] payload,
        bool dataProtected, bool activeModeRequested = false)
    {
        if (dataListener == null)
        {
            writer.WriteLine(activeModeRequested ? "150 Opening data connection." : "425 Use PASV first.");
            return;
        }
        if (!SuppressPreliminaryReply) writer.WriteLine("150 Opening data connection.");
        bool aborted = false;
        try
        {
            using (TcpClient data = dataListener.AcceptTcpClient())
            using (Stream ds = AcceptDataStream(data, dataProtected))
            {
                // Written in slices so a client that closes mid-transfer is noticed as a failed
                // write, the way a real server sees an aborted download.
                for (int offset = 0; offset < payload.Length; offset += 16384)
                {
                    ds.Write(payload, offset, Math.Min(16384, payload.Length - offset));
                }
                ds.Flush();
            }
        }
        catch (IOException) { aborted = true; }
        catch (ObjectDisposedException) { aborted = true; }
        dataListener.Stop();
        if (aborted) AbortedTransfers++;
        writer.WriteLine(aborted ? "426 Connection closed; transfer aborted." : "226 Transfer complete.");
    }

    private void TransferIn(StreamWriter writer, TcpListener dataListener, string preliminaryReply,
        bool dataProtected)
    {
        if (dataListener == null)
        {
            writer.WriteLine("425 Use PASV first.");
            return;
        }
        if (!SuppressPreliminaryReply) writer.WriteLine(preliminaryReply);
        bool aborted = false;
        using (var received = new MemoryStream())
        {
            try
            {
                using (TcpClient data = dataListener.AcceptTcpClient())
                using (Stream ds = AcceptDataStream(data, dataProtected))
                {
                    ds.CopyTo(received);
                }
            }
            catch (IOException) { aborted = true; }
            LastUpload = received.ToArray();
        }
        dataListener.Stop();
        if (aborted) AbortedTransfers++;
        writer.WriteLine(aborted ? "426 Connection closed; transfer aborted." : "226 Transfer complete.");
    }

    public void Dispose()
    {
        running = false;
        try { listener.Stop(); } catch { }
        acceptThread.Join(2000);
    }
}
