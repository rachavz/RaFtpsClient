using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RaFtpsClient.Tests;

/// <summary>
/// Self-signed certificates generated once per test run, so the TLS tests need nothing on disk.
/// </summary>
internal static class TestCertificate
{
    private const string PfxPassword = "test";
    private static readonly byte[] SharedSerial = [0x4A, 0x1B, 0x2C, 0x3D, 0x4E, 0x5F, 0x60, 0x71];

    /// <summary>The certificate the fake server presents. Self-signed, so a client that does not
    /// supply a validation callback must reject it.</summary>
    internal static readonly X509Certificate2 Server = Create("CN=localhost", SharedSerial);

    /// <summary>
    /// A different certificate carrying the same issuer name and serial number as
    /// <see cref="Server"/>, with a different key. <c>X509Certificate.Equals</c> compares only those
    /// two fields and so considers this one identical, which is exactly the substitution a pinning
    /// check has to notice.
    /// </summary>
    internal static readonly X509Certificate2 Twin = Create("CN=localhost", SharedSerial);

    private static X509Certificate2 Create(string subject, byte[] serialNumber)
    {
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // server auth

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(System.Net.IPAddress.Loopback);
        sanBuilder.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(sanBuilder.Build());

        // Signing explicitly rather than through CreateSelfSigned is what allows the serial number
        // to be pinned, so two certificates can collide on issuer plus serial.
        X509SignatureGenerator generator = X509SignatureGenerator.CreateForRSA(rsa, RSASignaturePadding.Pkcs1);
        using X509Certificate2 unsigned = request.Create(
            new X500DistinguishedName(subject), generator,
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), serialNumber);
        using X509Certificate2 withKey = unsigned.CopyWithPrivateKey(rsa);

        // SslStream needs the private key bound to the certificate; a PFX round-trip is the portable
        // way to get a handle that works as a server credential on every platform.
        return X509CertificateLoader.LoadPkcs12(
            withKey.Export(X509ContentType.Pfx, PfxPassword), PfxPassword,
            X509KeyStorageFlags.Exportable);
    }
}
