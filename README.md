# NetRoute

Two internet connections, one PC. NetRoute keeps your games on one of them and your
downloads on the other, so a Steam download can't cost you the game you're playing.

You pick which connection is 🎮 **Gaming** and which is ⬇ **Downloads**, then assign apps
with one click. Nothing is injected into any process and no game file is touched.

[![Build](https://github.com/SloppiestPorkins/netroute/actions/workflows/build.yml/badge.svg)](https://github.com/SloppiestPorkins/netroute/actions/workflows/build.yml)
![Windows 10/11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4)
![.NET 8](https://img.shields.io/badge/.NET-8-512BD4)
[![MIT](https://img.shields.io/badge/licence-MIT-green)](LICENSE)

## Why it exists

A download doesn't slow your game by using "your bandwidth" — it slows it by filling the
queue in front of your router, so every packet your game sends waits behind a megabyte of
someone else's. Bufferbloat. The fix isn't a bandwidth limit, it's a different line.

## How it works

- **Keeping apps off the wrong connection** is Windows Filtering Platform: user-mode
  filters at `ALE_AUTH_CONNECT` and `ALE_AUTH_RECV_ACCEPT`, IPv4 and IPv6, matched on the
  app's own identity (`ALE_APP_ID`, `ALE_PACKAGE_ID` for Store and Game Pass titles, and
  service SIDs via `ALE_USER_ID` for Windows Update, the Store and Xbox).
- **Moving apps onto the right one** is Mullvad's Microsoft-signed split-tunnel driver
  (MPL-2.0, unmodified, talked to through its device interface). Downloads is whatever
  Windows already treats as the default route; Gaming apps are the ones the driver moves.
- **Proving it worked** is real traffic, not configuration: NetRoute watches connections
  and only says *Verified* once it has seen an app use the network it was put on.
  **Prove it** goes further and measures your gaming line's latency while it pulls a real
  download down the other one.

No DLL injection, no hooking, no packet capture in the path traffic takes.

## What's in the box

| Piece | What it is |
| --- | --- |
| `NetRoute.App` | The window and the tray icon. Runs as you. |
| `NetRoute.Service` | Enforcement, verification, history. Runs as LocalSystem. |
| `netroute` | The same things from a terminal. |
| `NetRoute.Setup` | Installer and uninstaller, payload embedded. |

The service and the app talk over a named pipe. App discovery deliberately runs in the
app, in your session — LocalSystem can't see your Store packages.

## Building

```bash
dotnet test tests/NetRoute.Tests/NetRoute.Tests.csproj
```

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-installer.ps1
```

That publishes everything self-contained, verifies the driver's Mullvad signature, packs
it into a single `dist\NetRoute-Setup-<version>.exe`, and writes `dist\updates.json`
ready to publish beside it. The installer is unsigned, so SmartScreen warns on other PCs.

## Installing

Run the installer. If NetRoute is already there it says so and offers **Update**,
**Repair** or **Go back**, alongside **Remove** — an update keeps your apps and settings,
and rolls the old version back if anything fails. `/quiet`, `/update`, `/repair` and
`/remove` skip the questions; a quiet run that needs a restart to finish exits `3010`.

## From a terminal

```
netroute status                      networks, apps, and whether enforcement is on
netroute add "Halo Infinite" gaming  route an app
netroute selftest                    prove the separation, end to end
netroute pause-downloads on          hold download apps while a game is running
netroute quiet-hours 18 23           hold them in the evening whatever else is happening
netroute update --feed <url>         where to look for a newer NetRoute
netroute emergency-disable           remove every NetRoute filter from Windows, now
```

## Updating itself

NetRoute reads [`updates.json`](updates.json) from this repository once a day and offers
whatever is on the [releases page](https://github.com/SloppiestPorkins/netroute/releases).
Point it somewhere else, or clear it and it never calls anywhere. The feed is one JSON
document over https:

```json
{ "version": "1.2.0",
  "url": "https://example.com/NetRoute-Setup-1.2.0.exe",
  "sha256": "5E88...", "size": 76128256, "notes": "One line for the banner." }
```

The service fetches into a folder only SYSTEM and administrators can write to and proves
the bytes match the digest — a download that hashes differently is deleted, not kept. It
never installs anything: the app or the CLI runs setup and Windows asks for administrator.
No sha256 means no download, only a link to the release page, because the installer isn't
code-signed and that digest is the only thing tying the bytes to the version you trusted.

Releasing a new version is: `scripts\build-installer.ps1`, publish the exe as the
`v<version>` release, copy `dist\updates.json` to the repository root.

## Honest limits

- Apps that **host** a LAN game (Minecraft and friends) are never moved. The driver
  rewrites binds, and a server that can't bind `0.0.0.0` doesn't start. NetRoute knows
  this list and leaves them on Windows routing.
- A third connection can only **pin** apps, never move them onto it: the driver takes
  exactly one destination address.
- Two connections tied as Windows' default is a real problem NetRoute detects and offers
  to fix, because Windows will otherwise split traffic across both.
- Mullvad VPN's own background service is turned off while NetRoute runs — only one
  program can hold the driver. Removing NetRoute turns it back on.

## Licence

MIT — see [LICENSE](LICENSE). Fork it, ship it, sell it; keep the copyright notice.
That covers NetRoute's own code. The split-tunnel driver it ships stays under Mullvad's
MPL-2.0, unmodified.

## Third party

Mullvad's split-tunnel driver (MPL-2.0), the .NET runtime, TraceEvent and
CommunityToolkit.Mvvm. See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt). NetRoute
is not made by, affiliated with, or endorsed by Mullvad VPN AB.

The full specification this was built against is [docs/PRODUCT-BRIEF.md](docs/PRODUCT-BRIEF.md).
