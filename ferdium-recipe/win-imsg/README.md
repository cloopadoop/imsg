# iMessage (win-imsg) — Ferdium recipe

Hosts the win-imsg web companion (a localhost-only, token-protected page
served by the running win-imsg Windows app) inside Ferdium, with an unread
badge.

## Prerequisites

1. win-imsg is running on this machine.
2. In win-imsg: Settings → General → check **Enable web companion**. The
   status line below the checkbox shows the companion URL, including the
   access token, e.g. `http://127.0.0.1:8321/?token=abc123...`.

## Install the recipe

1. Copy this `win-imsg` folder to Ferdium's dev recipes directory:
   `%APPDATA%\Ferdium\recipes\dev\win-imsg`
2. Restart Ferdium and add a new service; search for "iMessage (win-imsg)".
3. Set the service's **custom server URL** to the full companion URL from
   win-imsg settings (including `?token=...`).

## Notes

- The token stays in your local Ferdium service config and authorizes only
  the loopback page; the recipe files themselves contain no secrets.
- The badge polls `/api/badge`; live message updates stream into the page
  over server-sent events.
- If win-imsg is not running, the service shows a connection error until the
  app starts again.
