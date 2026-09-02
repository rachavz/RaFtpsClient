using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace RaFtpsClient.Tests;

public class AsyncTlsTests : IDisposable
{
    private readonly FakeFtpServer server = new();
    private readonly FTPSClient client = new();

    private static bool AcceptAny(object s, X509Certificate c, X509Chain chain, SslPolicyErrors e) => true;

    private Task<string> ConnectAsync(ESSLSupportMode mode, RemoteCertificateValidationCallback validate, CancellationToken cancellationToken = default) =>
        client.ConnectAsync("127.0.0.1", server.Port, new NetworkCredential("alice", "hunter2"), mode, validate, cancellationToken: cancellationToken);

    private void UseTls(FakeTlsMode tlsMode)
    {
        server.TlsMode = tlsMode;
        server.Features = new List<string> { "AUTH TLS;SSL", "PBSZ", "PROT", "SIZE", "MDTM", "UTF8" };
    }

    public void Dispose()
    {
        client.Dispose();
        server.Dispose();
    }

    [Fact]
    public async Task EncryptsBothChannelsWithExplicitTls()
    {
        UseTls(FakeTlsMode.Explicit);
        server.ListingText = "-rw-r--r-- 1 o g 5 May 31 12:00 secret.txt\r\n";

        await ConnectAsync(ESSLSupportMode.ControlAndDataChannelsRequired, AcceptAny);
        IList<DirectoryListItem> list = await client.GetDirectoryListAsync();

        Assert.True(server.Received("AUTH TLS"));
        Assert.True(server.Received("PROT P"));
        Assert.NotNull(client.SslInfo);
        Assert.True(server.DataChannelWasEncrypted);
        Assert.Equal("secret.txt", Assert.Single(list).Name);
    }

    [Fact]
    public async Task ConnectsWithImplicitTls()
    {
        UseTls(FakeTlsMode.Implicit);
        server.FileContent = new byte[100_000];
        string local = Path.Combine(Path.GetTempPath(), "raftps-atls-" + Guid.NewGuid().ToString("N"));

        try
        {
            await ConnectAsync(ESSLSupportMode.Implicit, AcceptAny);
            await client.GetFileAsync("secret.bin", local);

            Assert.Equal(0, server.CountOf("AUTH"));
            Assert.True(server.DataChannelWasEncrypted);
            Assert.Equal(100_000, new FileInfo(local).Length);
        }
        finally
        {
            try { File.Delete(local); } catch { }
        }
    }

    [Fact]
    public async Task UploadsOverAnEncryptedDataChannel()
    {
        UseTls(FakeTlsMode.Explicit);
        string local = Path.Combine(Path.GetTempPath(), "raftps-atls-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(local, "confidential");

        try
        {
            await ConnectAsync(ESSLSupportMode.ControlAndDataChannelsRequired, AcceptAny);
            await client.PutFileAsync(local, "remote.txt");

            Assert.True(server.DataChannelWasEncrypted);
            Assert.Equal("confidential", System.Text.Encoding.UTF8.GetString(server.LastUpload));
        }
        finally
        {
            try { File.Delete(local); } catch { }
        }
    }

    [Fact]
    public async Task FailsLoudlyWhenAnImplicitServerRefusesProt()
    {
        UseTls(FakeTlsMode.Implicit);
        server.RejectProt = true;

        await Assert.ThrowsAsync<FTPSslException>(() => ConnectAsync(ESSLSupportMode.Implicit, AcceptAny));
    }

    [Fact]
    public async Task ReportsAServerThatDeniesExplicitTls()
    {
        server.TlsMode = FakeTlsMode.None;

        await Assert.ThrowsAsync<FTPSslException>(() => ConnectAsync(ESSLSupportMode.CredentialsRequired, AcceptAny));
    }

    [Fact]
    public async Task FallsBackToClearTextWhenTlsIsOnlyRequested()
    {
        server.TlsMode = FakeTlsMode.None;

        await ConnectAsync(ESSLSupportMode.CredentialsRequested, AcceptAny);

        Assert.Null(client.SslInfo);
        Assert.Equal(ESSLSupportMode.ClearText, client.SslSupportCurrentMode);
    }

    [Fact]
    public async Task RejectsAnUntrustedCertificateWhenNoCallbackIsSupplied()
    {
        UseTls(FakeTlsMode.Explicit);

        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(ESSLSupportMode.CredentialsRequired, null));
        Assert.Null(client.SslInfo);
    }

    [Fact]
    public async Task RevalidatesACertificateSubstitutedOnTheDataChannel()
    {
        UseTls(FakeTlsMode.Explicit);
        server.DataCertificate = TestCertificate.Twin;
        server.ListingText = "-rw-r--r-- 1 o g 5 May 31 12:00 a.txt\r\n";
        int callbacks = 0;

        await ConnectAsync(ESSLSupportMode.ControlAndDataChannelsRequired, (s, c, chain, e) => { callbacks++; return true; });
        await client.GetDirectoryListAsync();

        Assert.Equal(2, callbacks);
    }

    [Fact]
    public async Task HonoursTheConfiguredProtocolVersion()
    {
        UseTls(FakeTlsMode.Explicit);
        client.SslProtocols = SslProtocols.Tls12;

        await ConnectAsync(ESSLSupportMode.CredentialsRequired, AcceptAny);

        Assert.Equal(SslProtocols.Tls12, client.SslInfo.SslProtocol);
    }

    [Fact]
    public async Task ReachesAServerThatRequiresTls13()
    {
        UseTls(FakeTlsMode.Explicit);
        server.ServerSslProtocols = SslProtocols.Tls13;

        await ConnectAsync(ESSLSupportMode.CredentialsRequired, AcceptAny);

        Assert.Equal(SslProtocols.Tls13, client.SslInfo.SslProtocol);
    }
}
