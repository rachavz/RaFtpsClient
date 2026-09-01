using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace RaFtpsClient.Tests;

public class TlsTests : IDisposable
{
    private readonly FakeFtpServer server = new();
    private readonly FTPSClient client = new();

    /// <summary>Accepts anything. The server certificate is self-signed, so without a callback the
    /// client is expected to refuse it.</summary>
    private static bool AcceptAny(object s, X509Certificate c, X509Chain chain, SslPolicyErrors e) => true;

    private string Connect(ESSLSupportMode mode, RemoteCertificateValidationCallback validate) =>
        client.Connect("127.0.0.1", server.Port, new NetworkCredential("alice", "hunter2"), mode, validate);

    private void UseExplicitTls()
    {
        server.TlsMode = FakeTlsMode.Explicit;
        server.Features = new List<string> { "AUTH TLS;SSL", "PBSZ", "PROT", "SIZE", "MDTM", "UTF8" };
    }

    private void UseImplicitTls()
    {
        server.TlsMode = FakeTlsMode.Implicit;
        server.Features = new List<string> { "AUTH TLS;SSL", "PBSZ", "PROT", "SIZE", "MDTM", "UTF8" };
    }

    public void Dispose()
    {
        client.Dispose();
        server.Dispose();
    }

    [Fact]
    public void EncryptsTheControlChannelWithExplicitTls()
    {
        UseExplicitTls();

        Assert.Equal("User logged in, proceed.", Connect(ESSLSupportMode.CredentialsRequired, AcceptAny));

        Assert.True(server.Received("AUTH TLS"));
        Assert.NotNull(client.SslInfo);
        Assert.NotNull(client.RemoteCertificate);
        Assert.Equal("CN=localhost", client.RemoteCertificate.Subject);
    }

    [Fact]
    public void EncryptsBothChannelsWhenRequested()
    {
        UseExplicitTls();
        server.ListingText = "-rw-r--r-- 1 o g 5 May 31 12:00 secret.txt\r\n";

        Connect(ESSLSupportMode.ControlAndDataChannelsRequired, AcceptAny);
        IList<DirectoryListItem> list = client.GetDirectoryList();

        Assert.True(server.Received("PBSZ 0"));
        Assert.True(server.Received("PROT P"));
        Assert.True(server.DataChannelWasEncrypted);
        Assert.Equal("secret.txt", Assert.Single(list).Name);
    }

    [Fact]
    public void LeavesTheDataChannelClearWhenOnlyCredentialsAreProtected()
    {
        UseExplicitTls();
        server.ListingText = "-rw-r--r-- 1 o g 5 May 31 12:00 public.txt\r\n";

        Connect(ESSLSupportMode.CredentialsRequired, AcceptAny);
        client.GetDirectoryList();

        Assert.Equal(0, server.CountOf("PROT"));
        Assert.False(server.DataChannelWasEncrypted);
    }

    [Fact]
    public void TransfersFilesOverAnEncryptedDataChannel()
    {
        UseExplicitTls();
        server.FileContent = Encoding.UTF8.GetBytes(new string('s', 200_000));
        string local = Path.Combine(Path.GetTempPath(), "raftps-tls-" + Guid.NewGuid().ToString("N"));

        try
        {
            Connect(ESSLSupportMode.ControlAndDataChannelsRequired, AcceptAny);
            client.GetFile("secret.bin", local);

            Assert.True(server.DataChannelWasEncrypted);
            Assert.Equal(server.FileContent, File.ReadAllBytes(local));
        }
        finally
        {
            try { File.Delete(local); } catch { }
        }
    }

    [Fact]
    public void UploadsOverAnEncryptedDataChannel()
    {
        UseExplicitTls();
        string local = Path.Combine(Path.GetTempPath(), "raftps-tls-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(local, "confidential");

        try
        {
            Connect(ESSLSupportMode.ControlAndDataChannelsRequired, AcceptAny);
            client.PutFile(local, "remote.txt");

            Assert.True(server.DataChannelWasEncrypted);
            Assert.Equal("confidential", Encoding.UTF8.GetString(server.LastUpload));
        }
        finally
        {
            try { File.Delete(local); } catch { }
        }
    }

    [Fact]
    public void ConnectsWithImplicitTls()
    {
        UseImplicitTls();
        server.ListingText = "-rw-r--r-- 1 o g 5 May 31 12:00 implicit.txt\r\n";

        Connect(ESSLSupportMode.Implicit, AcceptAny);
        IList<DirectoryListItem> list = client.GetDirectoryList();

        Assert.Equal(0, server.CountOf("AUTH"));
        Assert.NotNull(client.SslInfo);
        Assert.True(server.DataChannelWasEncrypted);
        Assert.Equal("implicit.txt", Assert.Single(list).Name);
    }

    // A refused PROT on an implicit connection used to be swallowed, leaving the client wrapping a
    // data channel the server had just declined to protect.
    [Fact]
    public void FailsLoudlyWhenAnImplicitServerRefusesProt()
    {
        UseImplicitTls();
        server.RejectProt = true;

        var ex = Assert.Throws<FTPSslException>(() => Connect(ESSLSupportMode.Implicit, AcceptAny));

        Assert.Contains("data channel encryption", ex.Message);
    }

    [Fact]
    public void ReportsAServerThatDeniesExplicitTls()
    {
        server.TlsMode = FakeTlsMode.None;

        var ex = Assert.Throws<FTPSslException>(() => Connect(ESSLSupportMode.CredentialsRequired, AcceptAny));

        Assert.Contains("not supported on server", ex.Message);
    }

    [Fact]
    public void FallsBackToClearTextWhenTlsIsOnlyRequested()
    {
        server.TlsMode = FakeTlsMode.None;

        Connect(ESSLSupportMode.CredentialsRequested, AcceptAny);

        Assert.Null(client.SslInfo);
        Assert.Equal(ESSLSupportMode.ClearText, client.SslSupportCurrentMode);
        Assert.Equal("/home/user", client.GetCurrentDirectory());
    }

    // With no callback the default policy applies, and a self-signed certificate must not pass it.
    [Fact]
    public void RejectsAnUntrustedCertificateWhenNoCallbackIsSupplied()
    {
        UseExplicitTls();

        Assert.ThrowsAny<Exception>(() => Connect(ESSLSupportMode.CredentialsRequired, null));
        Assert.Null(client.SslInfo);
    }

    [Fact]
    public void HonoursACallbackThatRejectsTheCertificate()
    {
        UseExplicitTls();

        Assert.ThrowsAny<Exception>(() =>
            Connect(ESSLSupportMode.CredentialsRequired, (s, c, chain, e) => false));
    }

    [Fact]
    public void PassesTheServerCertificateAndPolicyErrorsToTheCallback()
    {
        UseExplicitTls();
        X509Certificate seen = null;
        SslPolicyErrors errors = SslPolicyErrors.None;

        Connect(ESSLSupportMode.CredentialsRequired, (s, c, chain, e) =>
        {
            seen = c;
            errors = e;
            return true;
        });

        Assert.NotNull(seen);
        Assert.Equal("CN=localhost", seen.Subject);
        Assert.True(errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors),
            "a self-signed certificate should raise a chain error");
    }

    // The data channel handshake reuses the decision already made for the control channel, so the
    // same certificate must not send the caller back through validation.
    [Fact]
    public void ValidatesTheSameCertificateOnlyOnce()
    {
        UseExplicitTls();
        server.ListingText = "-rw-r--r-- 1 o g 5 May 31 12:00 a.txt\r\n";
        int callbackCount = 0;

        Connect(ESSLSupportMode.ControlAndDataChannelsRequired, (s, c, chain, e) =>
        {
            callbackCount++;
            return true;
        });
        client.GetDirectoryList();
        client.GetDirectoryList();

        Assert.True(server.DataChannelWasEncrypted);
        Assert.Equal(1, callbackCount);
    }

    // X509Certificate.Equals compares only issuer name and serial number, so a certificate forged to
    // match on both used to slip past the data channel without ever reaching the callback.
    [Fact]
    public void RevalidatesACertificateSubstitutedOnTheDataChannel()
    {
        UseExplicitTls();
        server.DataCertificate = TestCertificate.Twin;
        server.ListingText = "-rw-r--r-- 1 o g 5 May 31 12:00 a.txt\r\n";
        var presented = new List<string>();

        Connect(ESSLSupportMode.ControlAndDataChannelsRequired, (s, c, chain, e) =>
        {
            presented.Add(Convert.ToHexString(c.GetCertHash()));
            return true;
        });
        client.GetDirectoryList();

        Assert.Equal(2, presented.Count);
        Assert.NotEqual(presented[0], presented[1]);
    }

    [Fact]
    public void TheTwinCertificateWouldFoolTheFrameworkComparison()
    {
        // Guards the premise of the test above: if these ever stop colliding it stops proving
        // anything, because any comparison at all would separate them.
        Assert.Equal(TestCertificate.Server.Issuer, TestCertificate.Twin.Issuer);
        Assert.Equal(TestCertificate.Server.SerialNumber, TestCertificate.Twin.SerialNumber);
        Assert.True(((X509Certificate)TestCertificate.Server).Equals((X509Certificate)TestCertificate.Twin),
            "X509Certificate.Equals should consider the twin identical");
        Assert.NotEqual(TestCertificate.Server.GetCertHash(), TestCertificate.Twin.GetCertHash());
    }

    // The protocol list used to be hardcoded to TLS 1.0/1.1/1.2; the property must actually reach
    // the handshake.
    [Fact]
    public void HonoursTheConfiguredProtocolVersion()
    {
        UseExplicitTls();
        client.SslProtocols = SslProtocols.Tls12;

        Connect(ESSLSupportMode.CredentialsRequired, AcceptAny);

        Assert.Equal(SslProtocols.Tls12, client.SslInfo.SslProtocol);
    }

    // The old hardcoded list topped out at TLS 1.2, so a server that will speak nothing but TLS 1.3
    // could not be reached at all.
    [Fact]
    public void ReachesAServerThatRequiresTls13()
    {
        UseExplicitTls();
        server.ServerSslProtocols = SslProtocols.Tls13;

        Connect(ESSLSupportMode.CredentialsRequired, AcceptAny);

        Assert.Equal(SslProtocols.Tls13, client.SslInfo.SslProtocol);
    }

    // Equally, the deprecated versions must no longer be on offer by default.
    [Fact]
    public void DoesNotFallBackToADeprecatedProtocolVersion()
    {
        UseExplicitTls();

        Connect(ESSLSupportMode.CredentialsRequired, AcceptAny);

        Assert.True(client.SslInfo.SslProtocol >= SslProtocols.Tls12,
            $"negotiated {client.SslInfo.SslProtocol}, expected TLS 1.2 or better");
    }

    [Fact]
    public void FailsWhenClientAndServerShareNoProtocolVersion()
    {
        UseExplicitTls();
        server.ServerSslProtocols = SslProtocols.Tls12;
        client.SslProtocols = SslProtocols.Tls13;

        Assert.ThrowsAny<Exception>(() => Connect(ESSLSupportMode.CredentialsRequired, AcceptAny));
    }

    // Revocation checking is on by default; the property is the documented way to turn it off.
    [Fact]
    public void RevocationCheckingIsOnByDefaultAndCanBeDisabled()
    {
        Assert.True(new FTPSClient().SslCheckCertRevocation);

        UseExplicitTls();
        client.SslCheckCertRevocation = false;

        Connect(ESSLSupportMode.CredentialsRequired, AcceptAny);

        Assert.NotNull(client.SslInfo);
    }

    // Note: that the flag actually reaches AuthenticateAsClient is NOT covered. A chain built over a
    // self-signed certificate stops at UntrustedRoot before revocation is ever consulted, so the
    // setting makes no observable difference here; proving it would need a trusted CA and a live
    // CRL or OCSP responder.


    [Fact]
    public void ReportsTheNegotiatedAlgorithmsThroughSslInfo()
    {
        UseExplicitTls();

        Connect(ESSLSupportMode.CredentialsRequired, AcceptAny);

        Assert.NotNull(client.SslInfo);
        Assert.Contains(client.SslInfo.SslProtocol.ToString(), client.SslInfo.ToString());
    }

    [Fact]
    public void ClearsTheStoredCertificateOnClose()
    {
        UseExplicitTls();

        Connect(ESSLSupportMode.CredentialsRequired, AcceptAny);
        Assert.NotNull(client.RemoteCertificate);

        client.Close();

        Assert.Null(client.RemoteCertificate);
        Assert.Null(client.LocalCertificate);
    }
}
