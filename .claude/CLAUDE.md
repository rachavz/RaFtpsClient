# RaFtpsClient

FTP/FTPS client library. Single assembly, no dependencies.

## Origin

The source is a decompilation of **AlexFTPS** (Alessandro Pilotti, LGPL-3.0), cleaned up and
re-namespaced. `temp/RaFtpsClient_decompiled.cs` is the raw ILSpy output kept for reference; it is
outside the csproj glob and is not compiled. Decompiler residue is still visible in the live source
(unused locals/constants, `num`/`text2` names, redundant assignments) — normal to clean up on touch.

## Build / test

```bash
dotnet build                # netstandard2.0, must stay warning-clean
dotnet test                 # tests/RaFtpsClient.Tests, net10.0, xunit
dotnet test --filter FullyQualifiedName~KeepAliveTests
```

`tests/RaFtpsClient.Tests` covers the parsers directly and drives the client end to end against
`FakeFtpServer`, an in-process server on a loopback socket that speaks enough of RFC 959 and RFC 4217
for login, FEAT, AUTH TLS, PBSZ/PROT, PASV/EPSV, LIST/NLST, RETR/STOR/APPE/STOU and the error paths.
Extend the fake rather than mocking sockets: its knobs (`ErrorReplies`, `SuppressPreliminaryReply`,
`DropConnectionOn`, `UserReplyCode`, `Features`, `TlsMode`, `RejectProt`, `DataCertificate`) exist so
protocol edge cases stay expressible as data.

`TestCertificate` generates self-signed certificates in memory, so no fixture files are needed. It
also exposes `Twin` — a certificate sharing `Server`'s issuer name and serial number but with a
different key, which is what makes the framework's `X509Certificate.Equals` treat the two as
identical and is the substitution the pinning check has to catch.

One gap is deliberate and documented in `TlsTests`: that `SslCheckCertRevocation` reaches
`AuthenticateAsClient` is not covered, because a chain over a self-signed certificate stops at
`UntrustedRoot` before revocation is consulted.

The library exposes its pure helpers as `internal` and grants `InternalsVisibleTo` from the
hand-written `Properties/AssemblyInfo.cs` — the SDK's `InternalsVisibleTo` item does nothing here
because `GenerateAssemblyInfo` is off.

Tests are only worth what a mutation proves: when fixing a bug, break the fix again and confirm the
new test fails before keeping it. Two connection-level regressions were caught this way, including a
dual-mode IPv6 socket that silently switched every IPv4 session to EPSV/EPRT.

## Conventions

- `netstandard2.0` target with `LangVersion latest`: file-scoped namespaces and `using var` are fine,
  but nothing from netstandard2.1+ (`Span` overloads on `Stream`, `SslProtocols.Tls13`, `Encoding`
  span APIs) — cast numerics when a newer TLS constant is needed.
- Public API is XML-documented; `GenerateDocumentationFile` is on, so every new public member needs
  a doc comment.
- Version lives in **two** places that must be kept in sync: `RaFtpsClient.csproj` `<Version>` and the
  hand-written `Properties/AssemblyInfo.cs` (`GenerateAssemblyInfo` is off).

## Architecture

- `FTPSClient` — connection state machine, one `*Cmd()` private method per FTP verb; every command
  funnels through `HandleCmd` → `GetReply`, both `[MethodImpl(Synchronized)]`.
- Control channel: `TcpClient` + `StreamReader/Writer`, optionally wrapped in `SslStream`.
- Data channel: separate `TcpClient` per transfer, torn down by the `FTPStream` close callback.
- `DirectoryListParser` — heuristic LIST parsing (Unix `ls -l` and Windows styles); no MLSD support.
