using System.Net;
using System.Text;

namespace RaFtpsClient.Tests;

public class AsyncClientTests : IDisposable
{
    private readonly FakeFtpServer server = new();
    private readonly FTPSClient client = new();
    private readonly List<string> tempFiles = new();

    private Task<string> ConnectAsync(CancellationToken cancellationToken = default) =>
        client.ConnectAsync(server.Address.ToString(), server.Port,
            new NetworkCredential("alice", "hunter2"), ESSLSupportMode.ClearText, cancellationToken: cancellationToken);

    private string TempFile(string content = null)
    {
        string path = Path.Combine(Path.GetTempPath(), "raftps-async-" + Guid.NewGuid().ToString("N"));
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
    public async Task ConnectsLogsInAndNegotiatesTheSession()
    {
        Assert.Equal("User logged in, proceed.", await ConnectAsync());

        Assert.Contains("USER alice", server.ReceivedCommands);
        Assert.Contains("PASS hunter2", server.ReceivedCommands);
        Assert.True(server.Received("FEAT"));
        Assert.True(server.Received("TYPE I"));
        Assert.True(server.Received("OPTS UTF8 ON"));
        Assert.Equal(ETransferMode.Binary, client.TransferMode);
        Assert.Equal(new[] { "SIZE", "MDTM", "UTF8", "CLNT" }, client.GetFeatures());
    }

    [Fact]
    public async Task UsesTheDefaultPortForTheMode()
    {
        // The short overload picks 21 for clear text; the fake listens elsewhere, so the connect must fail.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            client.ConnectAsync("127.0.0.1", new NetworkCredential("a", "b"), ESSLSupportMode.ClearText));
        Assert.IsAssignableFrom<FTPException>(ex);
    }

    [Fact]
    public async Task SkipsThePasswordWhenTheServerAcceptsTheUserOutright()
    {
        server.UserReplyCode = 230;

        Assert.Equal("User logged in, proceed.", await ConnectAsync());
        Assert.Equal(0, server.CountOf("PASS"));
    }

    [Fact]
    public async Task ReadsAndChangesTheWorkingDirectory()
    {
        await ConnectAsync();

        Assert.Equal("/home/user", await client.GetCurrentDirectoryAsync());
        Assert.Equal("/home/user", await client.PushCurrentDirectoryAsync());
        await client.SetCurrentDirectoryAsync("/tmp");
        Assert.Equal("/tmp", await client.GetCurrentDirectoryAsync());
        Assert.Equal("/home/user", await client.PopCurrentDirectoryAsync());
    }

    [Fact]
    public async Task ListsADirectory()
    {
        server.ListingText =
            "drwxr-xr-x 2 o g 4096 May 31 12:00 sub\r\n" +
            "-rw-r--r-- 1 o g 1234 May 31 12:00 file.txt\r\n";

        await ConnectAsync();
        IList<DirectoryListItem> list = await client.GetDirectoryListAsync();

        Assert.True(server.Received("PASV"));
        Assert.Collection(list,
            i => { Assert.Equal("sub", i.Name); Assert.True(i.IsDirectory); },
            i => { Assert.Equal("file.txt", i.Name); Assert.Equal(1234uL, i.Size); });
    }

    [Fact]
    public async Task ReturnsTheShortListing()
    {
        server.ShortListingText = "one.txt\r\ntwo.txt\r\n";

        await ConnectAsync();

        Assert.Equal(new[] { "one.txt", "two.txt" }, await client.GetShortDirectoryListAsync());
    }

    [Fact]
    public async Task DecodesNonAsciiNamesInALargeListing()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 3000; i++)
        {
            sb.Append($"-rw-r--r-- 1 o g 5 May 31 12:00 café-中文-{i}.txt\r\n");
        }
        server.ListingText = sb.ToString();

        await ConnectAsync();

        Assert.Equal(server.ListingText, await client.GetDirectoryListUnparsedAsync());
    }

    [Fact]
    public async Task DownloadsAFileLargerThanTheTransferBuffer()
    {
        var payload = new byte[250_000];
        new Random(7).NextBytes(payload);
        server.FileContent = payload;
        string local = TempFile();

        await ConnectAsync();
        ulong written = await client.GetFileAsync("big.bin", local);

        Assert.Equal((ulong)payload.Length, written);
        Assert.Equal(payload, File.ReadAllBytes(local));
    }

    [Fact]
    public async Task UploadsAFileLargerThanTheTransferBuffer()
    {
        var payload = new byte[300_000];
        new Random(11).NextBytes(payload);
        string local = TempFile();
        File.WriteAllBytes(local, payload);

        await ConnectAsync();
        ulong sent = await client.PutFileAsync(local, "big.bin");

        Assert.Equal((ulong)payload.Length, sent);
        Assert.Equal(payload, server.LastUpload);
        Assert.True(server.Received("STOR big.bin"));
    }

    [Fact]
    public async Task AppendsToARemoteFile()
    {
        string local = TempFile("appended");

        await ConnectAsync();
        await client.AppendFileAsync(local, "remote.txt");

        Assert.True(server.Received("APPE remote.txt"));
        Assert.Equal("appended", Encoding.UTF8.GetString(server.LastUpload));
    }

    [Fact]
    public async Task UploadsUnderAServerGeneratedName()
    {
        server.StouName = "/home/user/generated.txt";
        string local = TempFile("payload");

        await ConnectAsync();
        (ulong bytes, string remoteName) = await client.PutUniqueFileAsync(local);

        Assert.Equal(7uL, bytes);
        Assert.Equal("/home/user/generated.txt", remoteName);
        Assert.Equal("payload", Encoding.UTF8.GetString(server.LastUpload));
    }

    [Fact]
    public async Task ReportsProgressAndCompletion()
    {
        server.FileContent = new byte[200_000];
        string local = TempFile();
        var actions = new List<ETransferActions>();

        await ConnectAsync();
        await client.GetFileAsync("big.bin", local, (FTPSClient s, ETransferActions a, string l, string r, ulong sent, ulong? total, ref bool cancel) =>
        {
            actions.Add(a);
        });

        Assert.Contains(ETransferActions.FileDownloadingStatus, actions);
        Assert.Equal(ETransferActions.FileDownloaded, actions[actions.Count - 1]);
    }

    [Fact]
    public async Task TheCallbackCanStillCancel()
    {
        server.FileContent = new byte[500_000];
        string local = TempFile();

        await ConnectAsync();

        await Assert.ThrowsAsync<FTPOperationCancelledException>(() =>
            client.GetFileAsync("big.bin", local, (FTPSClient s, ETransferActions a, string l, string r, ulong sent, ulong? total, ref bool cancel) =>
            {
                cancel = true;
            }));
    }

    // Cancelling mid-transfer must leave the control channel usable: the server's 426 for the
    // aborted transfer has to be consumed, not left for the next command to read as its reply.
    [Fact]
    public async Task CancellingADownloadLeavesTheSessionUsable()
    {
        server.FileContent = new byte[4_000_000];
        server.ListingText = "-rw-r--r-- 1 o g 5 May 31 12:00 after.txt\r\n";
        string local = TempFile();
        using var cts = new CancellationTokenSource();

        await ConnectAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetFileAsync("huge.bin", local, (FTPSClient s, ETransferActions a, string l, string r, ulong sent, ulong? total, ref bool cancel) =>
            {
                if (sent > 100_000) cts.Cancel();
            }, cts.Token));

        Assert.Equal("/home/user", await client.GetCurrentDirectoryAsync());
        Assert.Equal("after.txt", Assert.Single(await client.GetDirectoryListAsync()).Name);
        Assert.Equal(1, server.AbortedTransfers);
    }

    [Fact]
    public async Task APreCancelledTokenRefusesTheOperation()
    {
        await ConnectAsync();
        var token = new CancellationToken(canceled: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetCurrentDirectoryAsync(token));
        Assert.Equal(0, server.CountOf("PWD"));
        Assert.Equal("/home/user", await client.GetCurrentDirectoryAsync());
    }

    // ReadTimeout only governs synchronous reads; the async path has to enforce the configured
    // timeout itself, or a silent server would hang the caller until the token fires.
    [Fact]
    public async Task ASilentServerTimesOutTheAsyncCall()
    {
        server.HangOn = "SYST";

        await client.ConnectAsync(server.Address.ToString(), server.Port, new NetworkCredential("alice", "hunter2"),
            ESSLSupportMode.ClearText, timeout: 700);
        var started = DateTime.UtcNow;
        var ex = await Assert.ThrowsAsync<FTPException>(() => client.GetSystemAsync());

        Assert.Contains("Timeout waiting for the server reply", ex.Message);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "the timeout did not fire");
    }

    [Fact]
    public async Task TheCallersTokenWinsOverTheTimeout()
    {
        server.HangOn = "SYST";
        using var cts = new CancellationTokenSource(200);

        await client.ConnectAsync(server.Address.ToString(), server.Port, new NetworkCredential("alice", "hunter2"),
            ESSLSupportMode.ClearText, timeout: 30_000);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetSystemAsync(cts.Token));
    }

    [Fact]
    public async Task ConnectCanBeCancelled()
    {
        server.DropConnectionOn = null;
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ConnectAsync(new CancellationToken(canceled: true)));
        Assert.Null(client.WelcomeMessage);
    }

    [Fact]
    public async Task SendsTheSimpleManagementCommands()
    {
        await ConnectAsync();

        await client.DeleteFileAsync("gone.txt");
        await client.MakeDirAsync("newdir");
        await client.RemoveDirAsync("olddir");
        await client.RenameFileAsync("before.txt", "after.txt");
        await client.ChangeToUpperDirAsync();
        await client.SetLanguageAsync("en-US");
        Assert.Equal("UNIX Type: L8", await client.GetSystemAsync());
        Assert.Equal(215, (await client.SendCustomCommandAsync("SYST")).Code);

        foreach (string expected in new[] { "DELE gone.txt", "MKD newdir", "RMD olddir", "RNFR before.txt", "RNTO after.txt", "CDUP", "LANG en-US" })
        {
            Assert.True(server.Received(expected), expected);
        }
    }

    [Fact]
    public async Task QueriesSizeAndModificationTime()
    {
        server.FileContent = new byte[42];

        await ConnectAsync();

        Assert.Equal(42uL, await client.GetFileTransferSizeAsync("f.txt"));
        Assert.Equal(2022, (await client.GetFileModificationTimeAsync("f.txt"))?.Year);
    }

    [Fact]
    public async Task SurfacesServerErrorsWithTheirReplyCode()
    {
        server.ErrorReplies["DELE"] = "550 Permission denied.";

        await ConnectAsync();
        var ex = await Assert.ThrowsAsync<FTPCommandException>(() => client.DeleteFileAsync("protected.txt"));

        Assert.Equal(550, ex.ErrorCode);
    }

    [Fact]
    public async Task RefusesToSendAnInjectedCommand()
    {
        await ConnectAsync();

        await Assert.ThrowsAsync<FTPException>(() => client.DeleteFileAsync("harmless.txt\nRETR /etc/passwd"));
        Assert.Equal(0, server.CountOf("RETR"));
    }

    [Fact]
    public async Task ReportsAControlConnectionClosedByTheServer()
    {
        server.DropConnectionOn = "PWD";

        await ConnectAsync();
        var ex = await Assert.ThrowsAsync<FTPException>(() => client.GetCurrentDirectoryAsync());

        Assert.Contains("closed the control connection", ex.Message);
    }

    [Fact]
    public async Task HandlesAServerThatSkipsThePreliminaryReply()
    {
        server.SuppressPreliminaryReply = true;
        server.ListingText = "-rw-r--r-- 1 o g 5 May 31 12:00 only.txt\r\n";

        await ConnectAsync();

        Assert.Equal("only.txt", Assert.Single(await client.GetDirectoryListAsync()).Name);
    }

    [Fact]
    public async Task RaisesLogEventsWithThePasswordMasked()
    {
        var commands = new List<string>();
        client.LogCommand += (s, e) => commands.Add(e.CommandText);

        await ConnectAsync();

        Assert.Contains("PASS ****", commands);
        Assert.DoesNotContain("PASS hunter2", commands);
    }

    // The two paths share one session, so a caller may mix them freely.
    [Fact]
    public async Task SyncAndAsyncCallsShareTheSession()
    {
        server.ListingText = "-rw-r--r-- 1 o g 5 May 31 12:00 shared.txt\r\n";

        client.Connect(server.Address.ToString(), server.Port, new NetworkCredential("alice", "hunter2"), ESSLSupportMode.ClearText, null);

        Assert.Equal("shared.txt", Assert.Single(await client.GetDirectoryListAsync()).Name);
        Assert.Equal("/home/user", client.GetCurrentDirectory());
        Assert.Equal("/home/user", await client.GetCurrentDirectoryAsync());
    }

    [Fact]
    public async Task RecursiveDownloadWalksTheTree()
    {
        server.ListingText = "-rw-r--r-- 1 o g 5 May 31 12:00 a.txt\r\n-rw-r--r-- 1 o g 5 May 31 12:00 b.txt\r\n";
        server.FileContent = Encoding.ASCII.GetBytes("hello");
        string localDir = Path.Combine(Path.GetTempPath(), "raftps-async-dir-" + Guid.NewGuid().ToString("N"));

        try
        {
            await ConnectAsync();
            await client.GetFilesAsync(null, localDir);

            Assert.Equal(new[] { "a.txt", "b.txt" }, Directory.GetFiles(localDir).Select(Path.GetFileName).OrderBy(n => n));
            Assert.Equal("hello", File.ReadAllText(Path.Combine(localDir, "a.txt")));
        }
        finally
        {
            try { Directory.Delete(localDir, true); } catch { }
        }
    }

    [Fact]
    public async Task RecursiveUploadWalksTheTree()
    {
        string localDir = Path.Combine(Path.GetTempPath(), "raftps-async-up-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(localDir, "sub"));
        File.WriteAllText(Path.Combine(localDir, "top.txt"), "top");
        File.WriteAllText(Path.Combine(localDir, "sub", "nested.txt"), "nested");

        try
        {
            await ConnectAsync();
            await client.PutFilesAsync(localDir, "/home/user/dest", recursive: true);

            Assert.True(server.Received("STOR /home/user/dest/top.txt"));
            Assert.True(server.Received("STOR /home/user/dest/sub/nested.txt"));
        }
        finally
        {
            try { Directory.Delete(localDir, true); } catch { }
        }
    }
}

public class AsyncActiveModeTests : IDisposable
{
    private readonly FakeFtpServer server = new();
    private readonly FTPSClient client = new();

    public void Dispose()
    {
        client.Dispose();
        server.Dispose();
    }

    [Fact]
    public async Task AnActiveTransferTimesOutInsteadOfBlockingForever()
    {
        await client.ConnectAsync(server.Address.ToString(), server.Port, new NetworkCredential("alice", "hunter2"),
            ESSLSupportMode.ClearText, timeout: 1500, dataConnectionMode: EDataConnectionMode.Active);

        var started = DateTime.UtcNow;
        var ex = await Assert.ThrowsAsync<FTPException>(() => client.GetDirectoryListAsync());

        Assert.Contains("Timeout waiting for the server", ex.Message);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20), "the accept did not honour the timeout");
        Assert.True(server.ReceivedCommands.Any(c => c.StartsWith("PORT ")), "expected a PORT command");
    }
}

public class AsyncKeepAliveTests : IDisposable
{
    private readonly FakeFtpServer server = new();
    private readonly FTPSClient client = new();

    public void Dispose()
    {
        client.Dispose();
        server.Dispose();
    }

    // The keep-alive thread takes the same lock as the async path, so a NOOP can never land between
    // an async transfer's data and its completion reply either.
    [Fact]
    public async Task DoesNotDisturbAsyncTransfersInFlight()
    {
        server.FileContent = new byte[400_000];
        server.ListingText = "-rw-r--r-- 1 o g 5 May 31 12:00 during.txt\r\n";

        await client.ConnectAsync(server.Address.ToString(), server.Port, new NetworkCredential("alice", "hunter2"), ESSLSupportMode.ClearText);
        client.KeepAliveTimeout = 5;
        client.StartKeepAlive();

        for (int i = 0; i < 5; i++)
        {
            string local = Path.Combine(Path.GetTempPath(), "raftps-aka-" + Guid.NewGuid().ToString("N"));
            try
            {
                await client.GetFileAsync("big.bin", local);
                Assert.Equal(400_000, new FileInfo(local).Length);
                Assert.Equal("during.txt", Assert.Single(await client.GetDirectoryListAsync()).Name);
                Assert.Equal("/home/user", await client.GetCurrentDirectoryAsync());
            }
            finally
            {
                try { File.Delete(local); } catch { }
            }
        }

        client.StopKeepAlive();
        Assert.True(server.CountOf("NOOP") > 0, "keep-alive never ran");
    }
}
