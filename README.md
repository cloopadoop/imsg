# WinIMsg

Native Windows shell for iMessage, backed by upstream `imsg` running on a Mac over SSH.

> **Pre-release software:** WinIMsg has not reached a stable public release. Expect incomplete features, breaking configuration changes, and manual setup on both Windows and macOS.

The Windows app lives under `windows/`. Upstream `imsg` is tracked as the `imsg/` submodule so the bridge contract can be updated independently without keeping a copied Swift package at the repository root.

## Current Shape

- WinUI 3 desktop app with profile-based Mac connection settings and explicit LAN/VPN, Tailscale, tunnel/proxy, or direct-SSH access models.
- SSH-launched `imsg rpc` bridge for chat list, history, watch, send, and advanced actions when supported.
- Cache-first local state under `%LOCALAPPDATA%\WinIMsg`.
- Redacted diagnostics support bundles under `%LOCALAPPDATA%\WinIMsg\support-bundles`.
- Attachment transfer through OpenSSH SFTP.
- Optional loopback web companion (Settings > General) serving a token-protected page and JSON API for browsers and Ferdium; recipe in `ferdium-recipe/win-imsg`.
- Repeatable Windows smoke scenarios in `windows\smoke-win.cmd`.

## Requirements

- Windows 10 version 2004 or newer, or Windows 11.
- A Mac signed into Messages and reachable from Windows over SSH.
- The pinned upstream `imsg` dependency initialized from this repository's public submodule.
- Full Disk Access and Automation permissions on the Mac as described in the [Mac setup guide](https://github.com/cloopadoop/win-imsg/wiki/Mac-Setup-and-Advanced-Features).

## Build

```powershell
git submodule update --init --recursive
dotnet test windows/WinIMsg.Tests/WinIMsg.Tests.csproj
dotnet build windows/WinIMsg.App/WinIMsg.App.csproj -c Debug -p:Platform=x86
```

See [windows/README.md](windows/README.md) and the [project wiki](https://github.com/cloopadoop/win-imsg/wiki) for setup and architecture details.

## Privacy and security

WinIMsg has no hosted relay service. Message content, contacts, connection profiles, credentials, attachments, and cache data remain on the user's Windows and Mac systems, subject to the local software and services the user chooses to connect. The optional web companion binds to loopback and requires a per-install access token.

Read [PRIVACY.md](PRIVACY.md) for the data-flow details and [SECURITY.md](SECURITY.md) before reporting a vulnerability. Review generated support bundles before sharing them.

## Contributing

Contributions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for build instructions, testing expectations, and the pull-request checklist.

## Publish

```cmd
windows\publish-win.cmd x64 Release
```

See [Packaging and Installation](https://github.com/cloopadoop/win-imsg/wiki/Packaging-and-Installation) for signing, install, uninstall, and update notes.

## Diagnostics

Use Settings > Diagnostics > Export support bundle to collect a redacted zip with app status, capability data, cache metadata, and the app log tail. Scripted smoke runs write predictable logs under `windows\logs\smoke-*.log`:

```cmd
windows\smoke-win.cmd notification
windows\smoke-win.cmd cache-reconciliation
windows\smoke-win.cmd connect test-mac.example.invalid testuser /opt/homebrew/bin/imsg
```
