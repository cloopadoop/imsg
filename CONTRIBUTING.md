# Contributing to WinIMsg

Thank you for helping improve WinIMsg.

## Before opening a change

- Keep examples synthetic. Use reserved domains such as `example.invalid`, RFC documentation IP ranges, and neutral test identities.
- Never include real messages, contacts, hostnames, usernames, local paths, tokens, support bundles, databases, screenshots, or configuration files.
- Keep generated build output, logs, caches, and IDE state out of commits.
- Add or update tests for behavioral changes.

## Build and test

Initialize the public `imsg` submodule, then run:

```powershell
git submodule update --init --recursive
dotnet test windows/WinIMsg.Tests/WinIMsg.Tests.csproj
dotnet build windows/WinIMsg.App/WinIMsg.App.csproj -c Debug -p:Platform=x86
```

## Submitting a change

Open a pull request with a clear description of the problem and the proposed solution. Keep each change focused, document user-visible behavior, and include tests when practical. Maintainers may request revisions before merging.

## Pull-request checklist

- The change contains no personal or operational data.
- Tests pass locally, or the pull request explains what could not be run.
- User-visible behavior and setup changes are documented.
- New dependencies are public, pinned appropriately, and compatible with the project's license.
