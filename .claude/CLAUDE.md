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
```

There is no test project. To exercise `internal` parsers without a live server, compile the sources
into a throwaway console project (`<Compile Include=".../RaFtpsClient/*.cs" />` with
`EnableDefaultCompileItems=false`) so `internal` members stay reachable; the installed runtime is
.NET 10, so target `net10.0`.

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
