# WinIMsg privacy notes

WinIMsg is a local desktop client. The project does not operate a hosted message relay, account service, analytics endpoint, or telemetry collector.

## Data handled by the app

Depending on the features used, WinIMsg handles message and chat metadata, contact identities, attachments, connection profiles, local cache state, and diagnostic logs. This information is stored on the Windows PC under the current user's local application-data directory. SSH credentials remain in the user's configured Windows/OpenSSH facilities; secrets that WinIMsg owns are protected with Windows DPAPI.

The Mac-side `imsg` process reads and acts on Messages data under the permissions granted by the Mac user. WinIMsg communicates with that process over the SSH route configured by the user.

## Optional web companion

The web companion is disabled by default. When enabled, it listens only on the Windows loopback interface and requires a per-install bearer token. The token grants access to local message features and must be treated as sensitive. Ferdium stores the companion URL in its own local service configuration.

## Diagnostics

Support bundles are designed to redact direct identifiers and exclude raw settings and message databases. Automated redaction cannot guarantee that every contextual detail is harmless. Inspect a bundle before sharing it and use a private channel whenever possible.

## Third-party activity

Apple Messages, macOS, SSH/OpenSSH, the upstream `imsg` dependency, Ferdium, links opened in a browser, and optional FaceTime actions are outside this project's control and have their own privacy behavior. WinIMsg does not change the policies of those products or services.
