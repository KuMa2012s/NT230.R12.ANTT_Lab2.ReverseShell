# NT230.R12.ANTT_Lab2.ReverseShell

A source archive for an educational Windows Service lab built with C# and .NET Framework 4.7.2.

The service performs an HTTP connectivity check, opens a reverse TCP connection to a loopback or private-network listener, mutually authenticates with nonce-HMAC-SHA256, and exposes a restricted managed command set.

## Scope

- Windows Service lifecycle with `OnStart` and `OnStop`
- HTTP/HTTPS connectivity check
- Private-network or loopback TCP endpoint policy
- Mutual nonce-HMAC-SHA256 authentication
- Managed commands: `help`, `hostname`, `whoami`, `pwd`, `dir`, `echo`, `time`, `exit`
- Reconnect, cancellation, file logging, and an end-to-end self-test

The implementation does not spawn `cmd.exe`, PowerShell, or arbitrary external processes.

## Requirements

- Windows 10 or Windows 11
- Visual Studio with .NET desktop development tools
- .NET Framework 4.7.2

## Build

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' `
  '.\NT230.R12.ANTT_Lab2.ReverseShell\NT230.R12.ANTT_Lab2.ReverseShell.csproj' `
  /t:Rebuild /p:Configuration=Release /nologo /v:minimal
```

## Self-test

```powershell
& '.\NT230.R12.ANTT_Lab2.ReverseShell\bin\Release\NT230.R12.ANTT_Lab2.ReverseShell.exe' --self-test
```

## Listener

Set a private token in `App.config`, then run the listener on a loopback or private IP:

```powershell
& '.\NT230.R12.ANTT_Lab2.ReverseShell\bin\Release\NT230.R12.ANTT_Lab2.ReverseShell.exe' `
  --listener 127.0.0.1 19090 '<PRIVATE_TOKEN>'
```

The same token must be configured in `App.config` before starting the Windows Service.

## Configuration

| Key | Purpose |
|---|---|
| `ConnectivityCheckUrl` | HTTP/HTTPS endpoint used for the connectivity check |
| `TcpHost` | Loopback or private listener IP |
| `TcpPort` | Listener TCP port |
| `SharedToken` | Shared secret used for mutual HMAC authentication |
| `ConnectTimeoutMilliseconds` | HTTP/TCP connection timeout |
| `ReconnectDelayMilliseconds` | Delay before reconnecting |
| `LogPath` | Service log file path |
| `CommandRoot` | Directory exposed to `pwd` and `dir` |

## Repository contents

- `NT230.R12.ANTT_Lab2.ReverseShell.slnx`: Visual Studio solution
- `NT230.R12.ANTT_Lab2.ReverseShell/`: C# project and source files

Build output, Visual Studio metadata, reports, verification files, and local runtime artifacts are intentionally excluded from this archive.

## Disclaimer

This repository is provided solely as an educational source-code archive for a controlled laboratory exercise. It is intended to demonstrate Windows Service lifecycle management, authenticated network communication, and restricted command handling.

Use this project only in environments that you own or are explicitly authorized to test. It is not intended for unauthorized access, persistence, disruption, surveillance, or any harmful activity against third-party systems.

The software is provided “as is,” without warranty. Users are responsible for complying with applicable rules and regulations and for any use or modification of the source code.
