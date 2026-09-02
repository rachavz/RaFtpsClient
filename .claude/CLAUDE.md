# RaFtpsClient

FTP/FTPS client library. Single assembly, no dependencies.

## Origin

The source is a decompilation of **AlexFTPS** (Alessandro Pilotti, LGPL-3.0), cleaned up and
re-namespaced. `temp/` holds the raw ILSpy output and the original 22.5.31.2 nupkg for reference; it
is git-ignored and local only, never part of the source or the package. Decompiler residue is still visible in the live source
(unused locals/constants, `num`/`text2` names, redundant assignments) — normal to clean up on touch.

## Build / test

```bash
dotnet build                # netstandard2.0, must stay warning-clean
dotnet test                 # tests/RaFtpsClient.Tests, net10.0, xunit
dotnet test --filter FullyQualifiedName~KeepAliveTests
dotnet run -c Release --project tests/RaFtpsClient.Benchmarks   # parser/reader/path micro-benchmarks
```

Performance claims get a number, not an adjective: run the benchmarks before and after on the same
machine and quote both. The parser, `LocalPathAllocator`, `PathCheck` and `ControlChannelReader`
are the CPU-bound parts; the transport is network-bound and not benchmarked.

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

The library exposes its pure helpers as `internal` and grants `InternalsVisibleTo` through the
csproj item.

Tests are only worth what a mutation proves: when fixing a bug, break the fix again and confirm the
new test fails before keeping it. Two connection-level regressions were caught this way, including a
dual-mode IPv6 socket that silently switched every IPv4 session to EPSV/EPRT.

## Conventions

- `netstandard2.0` target with `LangVersion latest`: file-scoped namespaces and `using var` are fine,
  but nothing from netstandard2.1+ (`Span` overloads on `Stream`, `SslProtocols.Tls13`, `Encoding`
  span APIs) — cast numerics when a newer TLS constant is needed.
- Public API is XML-documented; `GenerateDocumentationFile` is on, so every new public member needs
  a doc comment.
- Version lives only in `RaFtpsClient.csproj` `<Version>`, scheme `yy.M.d.build`; the SDK generates
  the assembly attributes. Behaviour changes go in `<PackageReleaseNotes>` there too.
- `dotnet pack RaFtpsClient/RaFtpsClient.csproj -c Release` builds the nupkg; check it with `unzip -l`.

## Architecture

`FTPSClient` is a partial class split by responsibility, and every operation exists in a
synchronous and an asynchronous form that sit next to each other in the same file. When you change
one, the other is visibly missing the change — that adjacency is the whole point of the layout.

- `FTPSClient.cs` — state, properties, events, `Connect`/`ConnectAsync`, keep-alive thread, TLS
  negotiation policy, session settings. `PrepareConnection` is the single definition of "a fresh
  session"; the policy decisions (`OnAuthRefused`, `OnProtRefused`) are shared helpers so the
  sync/async pairs only sequence commands.
- `FTPSClient.ControlChannel.cs` — connect with timeout (one socket per address family, never a
  dual-mode socket: the family decides PASV/PORT vs EPSV/EPRT), TLS wrapping, `HandleCmd`/`GetReply`
  and their async twins. The lock is a `SemaphoreSlim`, which is **not re-entrant**: code that runs
  while it is held must call the `*Core` variants. `ReplyAccumulator` parses multi-line replies for
  both paths.
- `ControlChannelReader` — line reader with sync and async reads over one buffer; replaced
  `StreamReader`, whose `ReadLineAsync` takes no token and whose read-ahead is stranded on the
  AUTH TLS stream swap.
- `FTPSClient.DataChannel.cs` — passive/active setup, `GetDataStream(Async)`, `ReadStreamAsUtf8(Async)`
  with a shared `Decoder`, and `ReadTransferCompletionReply(Async)` gated on `waitingCompletionReply`.
- `FTPSClient.Commands.cs` — `Cmd.*` verb formatters and reply parsers, all pure.
- `FTPSClient.Files.cs` — transfers. The async path settles the control channel through
  `RunTransferAsync`: on failure it consumes the server's 426/451 so the next command does not read a
  stale reply, which is what `FTPStream`'s close callback does for the sync path.
- `FTPSClient.Directories.cs` — listings and working directory.
- `DirectoryListParser` — heuristic LIST parsing (Unix `ls -l` and Windows styles); no MLSD support.
  Records are tokenised by index (`NextToken`/`Rest`) and Unix timestamps are assembled directly,
  with `DateTime.ParseExact` as the fallback: those two were most of the cost of a large listing.
- `LocalPathAllocator` — case-insensitive local path deduplication for recursive downloads, with a
  per-name suffix hint so repeated names stay linear.

Timeouts: the sync path relies on `ReadTimeout`/`WriteTimeout`, which do nothing for async I/O, so the
async path enforces the same `timeout` through `TimeoutScope` (a linked CTS with `CancelAfter`),
re-armed per operation on transfers. A timeout surfaces as `FTPException`; the caller's own token
surfaces as `OperationCanceledException`. Stream-returning async variants (`GetFileAsync(remote)`
returning a stream) are deliberately absent: netstandard2.0 has no `IAsyncDisposable`, so the
completion-reply-on-close contract cannot be expressed cleanly.
