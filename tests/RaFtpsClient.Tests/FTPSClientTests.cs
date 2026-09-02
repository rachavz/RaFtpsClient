using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RaFtpsClient.Tests;

public class FTPSClientTests : IDisposable
{
    private readonly FakeFtpServer server = new();
    private readonly FTPSClient client = new();
    private readonly List<string> tempFiles = new();

    private string Connect() =>
        client.Connect(server.Address.ToString(), server.Port,
            new NetworkCredential("alice", "hunter2"), ESSLSupportMode.ClearText, null);

    private string TempFile(string content = null)
    {
        string path = Path.Combine(Path.GetTempPath(), "raftps-" + Guid.NewGuid().ToString("N"));
        tempFiles.Add(path);
        if (content != null) File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        client.Dispose();
        server.Dispose();
        foreach (string f in tempFiles)
        {
            try { File.Delete(f); } catch { }
        }
    }

    [Fact]
    public void ConnectLogsInAndNegotiatesTheSession()
    {
        Assert.Equal("User logged in, proceed.", Connect());

        Assert.Contains("USER alice", server.ReceivedCommands);
        Assert.Contains("PASS hunter2", server.ReceivedCommands);
        Assert.True(server.Received("FEAT"));
        Assert.True(server.Received("TYPE I"));
        Assert.True(server.Received("OPTS UTF8 ON"));
        Assert.Equal(ETransferMode.Binary, client.TransferMode);
        Assert.Equal(ETextEncoding.UTF8, client.TextEncoding);
    }

    [Fact]
    public void ConnectFallsBackToAnonymousCredentials()
    {
        client.Connect(server.Address.ToString(), server.Port, null, ESSLSupportMode.ClearText, null);

        Assert.Contains("USER anonymous", server.ReceivedCommands);
    }

    // A 230 reply means the server wants no password at all.
    [Fact]
    public void ConnectSkipsThePasswordWhenTheServerAcceptsTheUserOutright()
    {
        server.UserReplyCode = 230;

        Assert.Equal("User logged in, proceed.", Connect());
        Assert.Equal(0, server.CountOf("PASS"));
    }

    [Fact]
    public void ExposesTheParsedFeatureList()
    {
        Connect();

        Assert.Equal(new[] { "SIZE", "MDTM", "UTF8", "CLNT" }, client.GetFeatures());
    }

    [Fact]
    public void SurvivesAServerThatAdvertisesNoFeatures()
    {
        server.ErrorReplies["FEAT"] = "500 Not understood.";

        Connect();

        Assert.Null(client.GetFeatures());
    }

    // Real servers qualify FEAT lines with parameters, which an exact match never recognised.
    [Fact]
    public void RecognisesFeaturesAdvertisedWithParameters()
    {
        server.Features = new List<string> { "AUTH TLS;SSL", "MDTM 20031111015806", "SIZE", "REST STREAM" };
        server.FileContent = Encoding.ASCII.GetBytes("0123456789");

        Connect();

        Assert.Equal(10uL, client.GetFileTransferSize("f.txt"));
        Assert.NotNull(client.GetFileModificationTime("f.txt"));
    }

    [Fact]
    public void ReturnsNullForFeaturesTheServerDoesNotAdvertise()
    {
        server.Features = new List<string> { "UTF8" };

        Connect();

        Assert.Null(client.GetFileTransferSize("f.txt"));
        Assert.Null(client.GetFileModificationTime("f.txt"));
    }

    [Fact]
    public void ReadsTheCurrentDirectory()
    {
        Connect();

        Assert.Equal("/home/user", client.GetCurrentDirectory());
    }

    [Fact]
    public void PushAndPopRestoreTheWorkingDirectory()
    {
        Connect();

        Assert.Equal("/home/user", client.PushCurrentDirectory());
        client.SetCurrentDirectory("/tmp");
        Assert.Equal("/tmp", client.GetCurrentDirectory());
        Assert.Equal("/home/user", client.PopCurrentDirectory());
        Assert.Equal("/home/user", client.GetCurrentDirectory());
    }

    [Fact]
    public void ListsADirectoryOverAPassiveDataConnection()
    {
        server.ListingText =
            "total 8\r\n" +
            "drwxr-xr-x 2 o g 4096 May 31 12:00 sub\r\n" +
            "-rw-r--r-- 1 o g 1234 May 31 12:00 file.txt\r\n";

        Connect();
        IList<DirectoryListItem> list = client.GetDirectoryList();

        Assert.True(server.Received("PASV"));
        Assert.Collection(list,
            i => { Assert.Equal("sub", i.Name); Assert.True(i.IsDirectory); },
            i => { Assert.Equal("file.txt", i.Name); Assert.Equal(1234uL, i.Size); });
    }

    [Fact]
    public void ReturnsTheShortDirectoryListing()
    {
        server.ShortListingText = "one.txt\r\ntwo.txt\r\n";

        Connect();

        Assert.Equal(new[] { "one.txt", "two.txt" }, client.GetShortDirectoryList());
    }

    // End-to-end cover for non-ASCII listings. Where the reads actually split is up to the network,
    // so DataDecodingTests is what pins the split-sequence behaviour down deterministically.
    [Fact]
    public void DecodesNonAsciiNamesInALargeListing()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 3000; i++)
        {
            sb.Append($"-rw-r--r-- 1 o g 5 May 31 12:00 café-piñata-é中{i}.txt\r\n");
        }
        server.ListingText = sb.ToString();

        Connect();
        string raw = client.GetDirectoryListUnparsed();

        Assert.True(Encoding.UTF8.GetByteCount(server.ListingText) > 81920, "listing must exceed one buffer");
        Assert.DoesNotContain('�', raw);
        Assert.Equal(server.ListingText, raw);
    }

    [Fact]
    public void DownloadsAFileToDisk()
    {
        server.FileContent = Encoding.UTF8.GetBytes("hello from the server");
        string local = TempFile();

        Connect();
        ulong written = client.GetFile("remote.txt", local);

        Assert.Equal((ulong)server.FileContent.Length, written);
        Assert.Equal("hello from the server", File.ReadAllText(local));
    }

    [Fact]
    public void DownloadsAFileLargerThanTheTransferBuffer()
    {
        var payload = new byte[250_000];
        new Random(1234).NextBytes(payload);
        server.FileContent = payload;
        string local = TempFile();

        Connect();
        client.GetFile("big.bin", local);

        Assert.Equal(payload, File.ReadAllBytes(local));
    }

    [Fact]
    public void ReportsDownloadProgressAndCompletion()
    {
        server.FileContent = new byte[200_000];
        string local = TempFile();
        var actions = new List<ETransferActions>();

        Connect();
        client.GetFile("big.bin", local, (FTPSClient s, ETransferActions a, string l, string r, ulong sent, ulong? total, ref bool cancel) =>
        {
            actions.Add(a);
        });

        Assert.Contains(ETransferActions.FileDownloadingStatus, actions);
        Assert.Equal(ETransferActions.FileDownloaded, actions[actions.Count - 1]);
    }

    // 4 MB so the server is still writing when the client tears the data connection down and answers
    // with a 426, which must not replace the cancellation the caller is expecting.
    [Fact]
    public void CancellingFromTheCallbackAbortsTheDownload()
    {
        server.FileContent = new byte[4_000_000];
        string local = TempFile();

        Connect();

        Assert.Throws<FTPOperationCancelledException>(() =>
            client.GetFile("big.bin", local, (FTPSClient s, ETransferActions a, string l, string r, ulong sent, ulong? total, ref bool cancel) =>
            {
                cancel = true;
            }));
        Assert.Equal("/home/user", client.GetCurrentDirectory());
        Assert.Equal(1, server.AbortedTransfers);
    }

    [Fact]
    public void UploadsAFile()
    {
        string local = TempFile("contents to upload");

        Connect();
        ulong sent = client.PutFile(local, "remote.txt");

        Assert.Equal(18uL, sent);
        Assert.Equal("contents to upload", Encoding.UTF8.GetString(server.LastUpload));
        Assert.True(server.Received("STOR remote.txt"));
    }

    [Fact]
    public void UploadsAFileLargerThanTheTransferBuffer()
    {
        var payload = new byte[300_000];
        new Random(99).NextBytes(payload);
        string local = TempFile();
        File.WriteAllBytes(local, payload);

        Connect();
        client.PutFile(local, "big.bin");

        Assert.Equal(payload, server.LastUpload);
    }

    // The reply parser used to chop the last character off the generated name.
    [Fact]
    public void ReturnsTheCompleteNameFromAUniqueUpload()
    {
        server.StouName = "/home/user/generated-name.txt";
        string local = TempFile("payload");

        Connect();
        client.PutUniqueFile(local, out string remoteName);

        Assert.Equal("/home/user/generated-name.txt", remoteName);
    }

    [Fact]
    public void AppendsToARemoteFile()
    {
        string local = TempFile("appended");

        Connect();
        client.AppendFile(local, "remote.txt");

        Assert.True(server.Received("APPE remote.txt"));
        Assert.Equal("appended", Encoding.UTF8.GetString(server.LastUpload));
    }

    [Fact]
    public void SendsTheSimpleFileManagementCommands()
    {
        Connect();

        client.DeleteFile("gone.txt");
        client.MakeDir("newdir");
        client.RemoveDir("olddir");
        client.RenameFile("before.txt", "after.txt");

        Assert.True(server.Received("DELE gone.txt"));
        Assert.True(server.Received("MKD newdir"));
        Assert.True(server.Received("RMD olddir"));
        Assert.True(server.Received("RNFR before.txt"));
        Assert.True(server.Received("RNTO after.txt"));
    }

    [Fact]
    public void SurfacesServerErrorsWithTheirReplyCode()
    {
        server.ErrorReplies["DELE"] = "550 Permission denied.";

        Connect();
        var ex = Assert.Throws<FTPCommandException>(() => client.DeleteFile("protected.txt"));

        Assert.Equal(550, ex.ErrorCode);
        Assert.Equal("Permission denied.", ex.Message);
    }

    // A name carrying a line break must never reach the control channel.
    [Fact]
    public void RefusesToSendAnInjectedCommand()
    {
        Connect();

        Assert.Throws<FTPException>(() => client.DeleteFile("harmless.txt\nRETR /etc/passwd"));
        Assert.Equal(0, server.CountOf("RETR"));
        Assert.Equal(0, server.CountOf("DELE"));
    }

    // Reading a null line used to surface as a NullReferenceException from the reply regex.
    [Fact]
    public void ReportsAControlConnectionClosedByTheServer()
    {
        server.DropConnectionOn = "PWD";

        Connect();
        var ex = Assert.Throws<FTPException>(() => client.GetCurrentDirectory());

        Assert.Contains("closed the control connection", ex.Message);
    }

    [Fact]
    public void RaisesLogEventsWithThePasswordMasked()
    {
        var commands = new List<string>();
        var replies = new List<int>();
        client.LogCommand += (s, e) => commands.Add(e.CommandText);
        client.LogServerReply += (s, e) => replies.Add(e.ServerReply.Code);

        Connect();

        Assert.Contains("USER alice", commands);
        Assert.Contains("PASS ****", commands);
        Assert.DoesNotContain("PASS hunter2", commands);
        Assert.Contains(230, replies);
    }

    // Servers that answer a transfer without a 1xx preliminary reply owe no completion reply;
    // reading one unconditionally blocked until the socket timed out.
    [Fact]
    public void HandlesAServerThatSkipsThePreliminaryReply()
    {
        server.SuppressPreliminaryReply = true;
        server.ListingText = "-rw-r--r-- 1 o g 5 May 31 12:00 only.txt\r\n";

        Connect();
        IList<DirectoryListItem> list = client.GetDirectoryList();

        Assert.Equal("only.txt", Assert.Single(list).Name);
    }

    [Fact]
    public void SendsACustomCommand()
    {
        Connect();
        FTPReply reply = client.SendCustomCommand("SYST");

        Assert.Equal(215, reply.Code);
        Assert.Equal("UNIX Type: L8", reply.Message);
    }

    [Fact]
    public void RejectsOperationsBeforeConnecting()
    {
        Assert.Throws<FTPException>(() => client.GetCurrentDirectory());
    }

    [Fact]
    public void ClosingIsIdempotent()
    {
        Connect();

        client.Close();
        client.Close();
        client.Dispose();
    }

    [Fact]
    public void ReconnectsAfterClosing()
    {
        Connect();
        client.Close();

        Assert.Equal("User logged in, proceed.", Connect());
        Assert.Equal(2, server.CountOf("USER"));
    }
}

public class ActiveModeTests : IDisposable
{
    private readonly FakeFtpServer server = new();
    private readonly FTPSClient client = new();

    public void Dispose()
    {
        client.Dispose();
        server.Dispose();
    }

    // The fake server does not dial back, so an active transfer must fail on the accept timeout
    // rather than blocking forever, which is what AcceptTcpClient did with no timeout at all.
    [Fact]
    public void AnActiveTransferTimesOutInsteadOfBlockingForever()
    {
        client.Connect(server.Address.ToString(), server.Port, new NetworkCredential("alice", "hunter2"),
            ESSLSupportMode.ClearText, null, null, 0, 0, 0, 1500, true, EDataConnectionMode.Active);

        var started = DateTime.UtcNow;
        Assert.ThrowsAny<FTPException>(() => client.GetDirectoryList());

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20), "the accept did not honour the timeout");
        Assert.True(server.ReceivedCommands.Any(c => c.StartsWith("PORT ")), "expected a PORT command");
    }
}

public class IPv6Tests : IDisposable
{
    private FakeFtpServer server;
    private readonly FTPSClient client = new();

    public void Dispose()
    {
        client.Dispose();
        server?.Dispose();
    }

    // EPSV replies carry only a port: the address has to come from the control channel's remote end.
    [Fact]
    public void UsesExtendedPassiveModeOverIPv6()
    {
        if (!Socket.OSSupportsIPv6) return;

        server = new FakeFtpServer(useIPv6: true);
        server.ListingText = "-rw-r--r-- 1 o g 5 May 31 12:00 v6.txt\r\n";

        client.Connect("::1", server.Port, new NetworkCredential("alice", "hunter2"), ESSLSupportMode.ClearText, null);
        IList<DirectoryListItem> list = client.GetDirectoryList();

        Assert.True(server.Received("EPSV"));
        Assert.Equal("v6.txt", Assert.Single(list).Name);
    }
}
