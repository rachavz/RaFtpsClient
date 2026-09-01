using System.Diagnostics;
using System.Net;
using System.Text;

namespace RaFtpsClient.Tests;

public class KeepAliveTests : IDisposable
{
    private readonly FakeFtpServer server = new();
    private readonly FTPSClient client = new();

    private void Connect() =>
        client.Connect(server.Address.ToString(), server.Port,
            new NetworkCredential("alice", "hunter2"), ESSLSupportMode.ClearText, null);

    private static void WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(25);
        }
    }

    public void Dispose()
    {
        client.Dispose();
        server.Dispose();
    }

    [Fact]
    public void SendsNoopsWhileStarted()
    {
        Connect();
        client.StartKeepAlive();

        Assert.True(client.KeepAliveStarted);
        WaitUntil(() => server.CountOf("NOOP") > 0);
        Assert.True(server.CountOf("NOOP") > 0);

        client.StopKeepAlive();
        Assert.False(client.KeepAliveStarted);
    }

    [Fact]
    public void RefusesToStartTwice()
    {
        Connect();
        client.StartKeepAlive();

        Assert.Throws<FTPException>(() => client.StartKeepAlive());
    }

    [Fact]
    public void RefusesToStartBeforeConnecting()
    {
        Assert.Throws<FTPException>(() => client.StartKeepAlive());
    }

    [Fact]
    public void StoppingIsIdempotent()
    {
        Connect();
        client.StartKeepAlive();
        client.StopKeepAlive();
        client.StopKeepAlive();

        Assert.False(client.KeepAliveStarted);
    }

    // The latch that stops the thread was never reset, so keep-alive could only ever run once.
    [Fact]
    public void RestartsAfterBeingStopped()
    {
        Connect();
        client.StartKeepAlive();
        WaitUntil(() => server.CountOf("NOOP") > 0);
        client.StopKeepAlive();

        int before = server.CountOf("NOOP");
        client.StartKeepAlive();
        WaitUntil(() => server.CountOf("NOOP") > before);

        Assert.True(server.CountOf("NOOP") > before, "no NOOP arrived after keep-alive was restarted");
    }

    // Connect closes any previous session, which stopped the keep-alive latch for good.
    [Fact]
    public void StillWorksAfterAReconnect()
    {
        Connect();
        client.StartKeepAlive();
        WaitUntil(() => server.CountOf("NOOP") > 0);
        client.Close();

        Connect();
        int before = server.CountOf("NOOP");
        client.StartKeepAlive();
        WaitUntil(() => server.CountOf("NOOP") > before);

        Assert.True(server.CountOf("NOOP") > before, "no NOOP arrived after reconnecting");
    }

    // A NOOP slipped between the data read and the completion reply used to consume the transfer's
    // own 226, desynchronising every command that followed.
    [Fact]
    public void DoesNotDisturbTransfersInFlight()
    {
        server.FileContent = new byte[400_000];
        server.ListingText = "-rw-r--r-- 1 o g 5 May 31 12:00 during.txt\r\n";

        Connect();
        client.KeepAliveTimeout = 5;
        client.StartKeepAlive();
        WaitUntil(() => server.CountOf("NOOP") > 0);

        for (int i = 0; i < 5; i++)
        {
            string local = Path.Combine(Path.GetTempPath(), "raftps-ka-" + Guid.NewGuid().ToString("N"));
            try
            {
                client.GetFile("big.bin", local);
                Assert.Equal(400_000, new FileInfo(local).Length);
                Assert.Equal("during.txt", Assert.Single(client.GetDirectoryList()).Name);
                Assert.Equal("/home/user", client.GetCurrentDirectory());
            }
            finally
            {
                try { File.Delete(local); } catch { }
            }
        }

        client.StopKeepAlive();
    }
}
