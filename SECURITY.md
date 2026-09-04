# Security policy

## Supported versions

WinIMsg is pre-release software. Security fixes are applied to the newest release; older versions are not currently supported.

## Reporting a vulnerability

Do not place credentials, access tokens, private hostnames, message content, contact information, support bundles, or exploit details in a public issue.

Use GitHub's private vulnerability-reporting feature when it is available for this repository. If that feature is unavailable, open a minimal public issue asking the maintainer to establish a private reporting channel, without including sensitive or exploitable details.

Include the affected version, operating-system versions, a concise impact description, and safe reproduction steps. Remove or replace all personal data before sending logs or screenshots.

## Security boundaries

- The Windows app relies on the user's SSH configuration and the security of the selected Mac.
- The web companion is intended only for loopback access. Its bearer token must not be exposed or forwarded to another device.
- Support-bundle redaction is defense in depth, not permission to publish a bundle without human review.
