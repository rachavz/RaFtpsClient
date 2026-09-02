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
- Synchronous and asynchronous APIs over one session, with `CancellationToken` support

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

Every operation has an `*Async` counterpart. The two share the session, so they can be mixed:

```csharp
using var client = new FTPSClient();
await client.ConnectAsync("ftp.example.com", new NetworkCredential("user", "pass"),
    ESSLSupportMode.CredentialsRequired, cancellationToken: ct);

foreach (var f in await client.GetDirectoryListAsync(cancellationToken: ct))
    Console.WriteLine($"{f.Name} ({f.Size} bytes)");

await client.GetFileAsync("remote.txt", "local.txt", cancellationToken: ct);
```

The configured timeout applies to both paths: synchronous calls through the socket timeouts,
asynchronous ones through an internal deadline that surfaces as `FTPException`. Cancelling a
transfer mid-way leaves the session usable; the server's abort reply is consumed for you.

## TLS defaults

The TLS version is left to the operating system (`SslProtocols.None`), and server certificates are
checked against their revocation list. Both are configurable before `Connect`:

```csharp
client.SslProtocols = SslProtocols.Tls12;   // pin a version
client.SslCheckCertRevocation = false;      // only if the CRL/OCSP endpoint is unreachable
```

## License

LGPL-3.0 - see [LICENSE](LICENSE) for details.

Portions Copyright (c) 2008 Alessandro Pilotti, licensed under the LGPL.
