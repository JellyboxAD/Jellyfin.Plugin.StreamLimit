# Jellyfin Stream Limiter Plugin

A Jellyfin plugin that limits the number of simultaneous streams per user.

Compatible with **Jellyfin 10.11** (default build, `net9.0`) and **Jellyfin 12** (`net10.0` build), from the same source.

## Manifest URL

One manifest for every server version (Dashboard > Plugins > Repositories):

```
https://raw.githubusercontent.com/JellyboxAD/Jellyfin.Plugin.StreamLimit/main/manifest.json
```

Your server picks the right build automatically: Jellyfin **10.11** installs `1.1.0.0` (net9), Jellyfin **12** installs `1.1.1.0` (net10) — same features, one build per server generation.

> ⚠️ **BETA** — the HTTP-level hard block and the custom web message introduced in v1.1.x are new. They ship enabled by default and can each be turned off in the plugin settings to fall back to the classic behaviour.

## Features

- 🎮 Per-user stream limits, plus a default limit for everyone else
- 🚧 **Hard block at the HTTP level** *(BETA)*: media requests over the limit are rejected with `403` before a single byte is served — works for **every** client (Swiftfin, Infuse, external players, custom apps), because a stream that is never served cannot be played. `PlaybackInfo` is answered with a proper "playback not allowed" error so clients show a clean dialog.
- 💬 **Custom block message on web clients** *(BETA)*: the plugin injects a small script into the web client so a blocked stream shows **one clean popup with your own title/text** instead of the built-in generic dialog — works in every language. Web clients only; native apps keep their own error screen (no plugin can change their UI).
- 🛑 Layered reactive enforcement as a safety net:
  1. Stop playstate command (well-behaved clients)
  2. Server-side transcode kill (clients that ignore commands)
  3. Optional device logout (revokes the token, also cuts direct play)
- 🔁 Automatic one-time migration of pre-1.1 configurations (legacy entries normalized, corrupted entries dropped)
- 🔒 Admin-only management API

## Example

![Example](Readme-data/Example.gif)

## Installation

1. Add the manifest URL above as a plugin repository (Dashboard > Plugins > Repositories), or download the zip from the releases and extract it into your server's `plugins` folder
2. Restart Jellyfin
3. Configure the plugin in Dashboard > My Plugins > Stream Limiter

## Configuration

![Plugin Configuration](Readme-data/Config.png)

The settings page has three sections. Defaults are safe: install, set a limit, done.

**Stream limits**
- **Per-user limit**: select a user and set their max simultaneous streams. Empty or 0 removes the entry (the default applies). A paused stream still counts as active.
- **Default limit**: applied to every user without an explicit limit. 0 = unlimited.

**Enforcement**
- **Hard block at the HTTP level** *(BETA, on)*: rejects over-limit media requests before any byte is served — works on every client. Turn off to fall back to the classic stop-command behaviour only.
- **Refuse playback negotiation** *(BETA, on)*: denies the stream during setup (`PlaybackInfo`) so clients fail cleanly before loading. Requires the hard block.
- **Kill transcode jobs** *(on)*: stops the server-side ffmpeg job of a blocked stream — safety net for clients that ignore stop commands.
- **Log out the offending device** *(off)*: revokes the device token. Most aggressive option — also cuts direct-play, but the user must sign in again on that device.
- **Strict mode** *(BETA, off)*: also rejects media requests whose user cannot be identified (no usable token). By default those are let through (fail-open) to avoid breaking exotic clients.

**Blocked message**
- **Title / Text**: your wording, shown when a stream is blocked.
- **Show my message on web clients** *(BETA, on)*: replaces the web client's built-in "not allowed" dialog with a single popup carrying your title/text. Web clients only (browser, Android webview, iOS web); a browser refresh is needed after changing it. Falls back silently to the native dialog if the web files cannot be written (e.g. read-only Docker mounts).
- **Also push a server message** *(off, advanced)*: sends a Jellyfin `DisplayMessage` command. Only some native apps show it, and it adds a second popup on web clients.

> **Upgrading from 1.0.x?** Nothing to do: on first start the plugin migrates your existing configuration (legacy dashed user ids and corrupted entries are cleaned automatically).

## How it works

**Hard block (primary, default on, BETA).** The plugin installs a global request filter inside the Jellyfin server. Every media request (progressive `stream`, HLS playlists and segments, universal audio, Live TV, `/Items/{id}/File` and `/Download`) is matched to a playback *slot* per user and device:

- a device already streaming keeps its slot (item switches, quality changes and seeks are not new streams);
- a new device beyond the limit gets **`403` — no media bytes are ever served**, so it cannot play no matter what client it is; blocked responses carry an `X-StreamLimit: 1` header;
- `PlaybackInfo` returns `ErrorCode: NotAllowed` at the limit, so clients show a proper error dialog instead of a spinner;
- slots free up when the client reports a stop, when its session ends, when its HTTP response ends, or after 60 s without traffic (abandoned slots are evicted early when another device is about to be denied);
- theme songs and video backdrops played while browsing never hold a slot;
- requests that cannot be attributed to a user (API keys, anonymous) are **never blocked** (fail-open, unless strict mode is on).

**Web message (BETA).** At startup the plugin adds a `<script>` tag to the web client's `index.html` (idempotent, reapplied after every server/web update) pointing at `/StreamLimit/inject.js`. That script detects a block (`X-StreamLimit` header or `PlaybackInfo` error), shows one popup with your configured title/text, and suppresses the client's own error dialogs — matching their structure, not their wording, so it works in every language. If the web folder is not writable, the plugin logs a warning and the client simply shows its built-in dialog: the block itself is never affected.

**Reactive safety net.** When a playback report still slips over the limit, the plugin stops the newest stream: stop command → transcode kill → optional server message → optional device logout. Paused streams count as active: they hold a playback slot.

**Native clients.** Swiftfin, AFinity, Streamyfin, Infuse and other native apps are fully blocked, but they display their own generic error screen — no server plugin can change or hide a native client's UI. Custom wording is only possible on web clients.

## API

Management endpoints require **elevated (admin) permissions**:

```http
GET  /StreamLimit/GetUserStreamLimit?userId=<id>
     → { "userId": "<dashless-id>", "streamsAllowed": <effective>, "explicitLimit": <n|null> }
GET  /StreamLimit/GetAllStreamLimits
     → { "defaultMaxStreams": <n>, "limits": { "<dashless-id>": <n>, ... } }
POST /StreamLimit/SetUserStreamLimit?userId=<id>&streamsAllowed=<n>
     → sets the user's limit; 0 removes it (the default applies again)
POST /StreamLimit/SetAlertMessage?alertMessage=<text>&title=<title>
     → updates the blocked-message wording
```

One endpoint is anonymous by design (it is loaded by the web client before login and contains no secrets):

```http
GET  /StreamLimit/inject.js    → the web-message client script
```

User ids are accepted with or without dashes. Swagger UI: `http://your-server/api-docs/swagger` (StreamLimit section).

## Building

```bash
# Jellyfin 10.11 (default)
dotnet publish Jellyfin.Plugin.StreamLimit/Jellyfin.Plugin.StreamLimit.csproj -c Release -o publish/net9

# Jellyfin 12
dotnet publish Jellyfin.Plugin.StreamLimit/Jellyfin.Plugin.StreamLimit.csproj -c Release \
  -p:PluginTargetFramework=net10.0 -p:JellyfinVersion=12.0.0-rc2 -o publish/net10

# Tests
dotnet test tests/Jellyfin.Plugin.StreamLimit.Tests/Jellyfin.Plugin.StreamLimit.Tests.csproj
```

The plugin has no external dependencies (System.Text.Json only), so the release zip contains a single DLL.

## Contributing

1. Fork the project
2. Create a feature branch (`git checkout -b feature/AmazingFeature`)
3. Commit your changes (`git commit -m 'Add some AmazingFeature'`)
4. Push to the branch (`git push origin feature/AmazingFeature`)
5. Open a Pull Request

## License

This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.

## Support

For questions or issues:
1. Check the [Issues](../../issues)
2. Create a new issue if needed
3. Join the Jellyfin community
