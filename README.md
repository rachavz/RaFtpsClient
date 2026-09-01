# RaFtpsClient

Free FTP/FTPS client and class library available on any platform supporting .NET Standard 2.0+.

Written to overcome the limits of .NET's `System.Net.FTPWebRequest` in terms of FTPS support.

## Supported Frameworks

Targets .NET Standard 2.0, so it can be referenced from .NET Framework 4.6.1+, .NET Core 2.0+ and
.NET 5 and later.

## Features

- FTP and FTPS (FTP over SSL/TLS) support
- Explicit and implicit SSL/TLS connections
- Active and passive data connection modes
- File upload/download with progress callbacks
- Directory listing and navigation
- File and directory management (rename, delete, create)
- Keep-alive support
- Custom FTP commands
- Certificate validation callbacks

## Quick Start

```csharp
using RaFtpsClient;
using System.Net;

using var client = new FTPSClient();
client.Connect("ftp.example.com", new NetworkCredential("user", "pass"),
    ESSLSupportMode.CredentialsRequired);

var files = client.GetDirectoryList();
foreach (var f in files)
    Console.WriteLine($"{f.Name} ({f.Size} bytes)");

client.GetFile("remote.txt", "local.txt");
client.Close();
```

## TLS defaults

The TLS version is left to the operating system (`SslProtocols.None`), and server certificates are
checked against their revocation list. Both are configurable before `Connect`:

```csharp
client.SslProtocols = SslProtocols.Tls12;   // pin a version
client.SslCheckCertRevocation = false;      // only if the CRL/OCSP endpoint is unreachable
```

## License

LGPL-3.0 - see [LICENSE](LICENSE) for details.

Derived from [AlexFTPS](https://github.com/alexpilotti/AlexFTPS) by Alessandro Pilotti, also
LGPL-3.0.
