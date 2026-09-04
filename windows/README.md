# win-imsg Windows shell

This folder contains the Windows-native shell for `win-imsg`. Upstream `imsg` is tracked as the `imsg/` submodule; the Windows app treats `imsg rpc` as the versioned Mac-side contract.

## Build

```cmd
windows\build-win.cmd
```

The helper runs the core tests and builds the WinUI app for `x64`, writing details to `windows\logs\build-win.log`.

## Publish And Install

```cmd
windows\publish-win.cmd x64 Release
windows\install-published-win.cmd "windows\WinIMsg.App\bin\Release\net9.0-windows10.0.19041.0\win-x64\publish" --launch
```

Publishing writes `windows\artifacts\win-imsg-Release-x64.zip` and `windows\logs\publish-win-x64.log`. Installing copies the self-contained app to `%LOCALAPPDATA%\Programs\WinIMsg` and registers the Start menu identity used for notification attribution and activation. See [Packaging and Installation](https://github.com/cloopadoop/win-imsg/wiki/Packaging-and-Installation).

## Runtime Shape

- `WinIMsg.App` is an unpackaged WinUI 3 app with single-instance activation, tray menu, minimize-to-tray, startup shortcut registration, app notifications, and a native chat shell.
- `WinIMsg.Core` owns SSH process execution, JSON-RPC framing, imsg DTOs, capability checks, SFTP attachment transfer, JSON settings, DPAPI secret storage, and SQLite cache state.
- The app launches `ssh <target> <imsg> rpc` for the long-running stream. Probe checks use `imsg --version` and `imsg status --json`.

## Mac Prereqs

- macOS signed into Messages.
- `imsg` installed and reachable in the configured SSH session.
- Full Disk Access for the Mac Remote Login/OpenSSH entry that launches SSH commands.
- Automation permission for sends.
- Advanced actions such as typing, read state, edit/unsend, rich sends, and group changes require `imsg status --json` to advertise the corresponding RPC methods.

## Mac Probe

```cmd
windows\probe-mac.cmd test-mac.example.invalid testuser /opt/homebrew/bin/imsg
```

The helper checks SSH, `imsg --version`, `imsg status --json`, and a `chats.list` RPC request, writing details to `windows\logs\probe-mac.log`.

## Diagnostics And Smoke

Settings > Diagnostics can export a redacted support bundle to `%LOCALAPPDATA%\WinIMsg\support-bundles`. The bundle includes status text, capability summaries, cache table/file metadata, a log index, and a redacted tail of `win-imsg.log`; it does not copy raw `settings.json`.

Repeatable smoke scenarios write one log per subsystem under `windows\logs`:

```cmd
windows\smoke-win.cmd notification
windows\smoke-win.cmd cache-reconciliation
windows\smoke-win.cmd connect test-mac.example.invalid testuser /opt/homebrew/bin/imsg
windows\smoke-win.cmd selected-history test-mac.example.invalid testuser /opt/homebrew/bin/imsg 7
```

Live send scenarios are guarded. Set `WINIMSG_SMOKE_ALLOW_SEND=1` before `send-text` or `send-attachment` so accidental test messages are not sent.
