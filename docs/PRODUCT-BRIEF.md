# NetRoute — product brief

> Source of truth for product behaviour. Section numbers (§) are cited throughout the
> code, commits and task briefs. This is the owner's brief, condensed only where it
> repeated itself; UI mockups are kept verbatim.

**In one sentence:** NetRoute lets me choose which network adapter is my Gaming connection
and which is my Download connection, then assign applications and games to either one
with one click — while both run simultaneously.

```text
                 INTERNET
                /        \
       GAMING ISP       DOWNLOAD ISP
            |                |
        Ethernet            Wi-Fi
            ↓                ↓
       🎮 GAMING          ⬇ DOWNLOADS
            ↓                ↓
       Game.exe          Steam.exe
```

## §1 Development priority
Build a working product quickly. Order: adapter selection → application identification →
actual per-application routing → traffic verification → simple UI → Game Pass → Steam/Win32
→ service/background enforcement → robustness → advanced features. First milestone: two
applications, two adapters, actual traffic verified.

## §2 Not a ForceBindIP clone
No DLL injection, binary modification, Winsock patching, game memory manipulation, or API
hooking inside games. Use modern Windows mechanisms: WFP, ALE, application identity, WFP
callouts where necessary, connect/bind redirection, IP Helper, interface LUID/GUID,
package/application identity.

## §3 Architecture principle
Separate USER INTENT → LOGICAL NETWORK ROLE → APPLICATION POLICY → WINDOWS ENFORCEMENT.
Rules reference the logical role, not the adapter. Changing Gaming from Ethernet to Wi-Fi
moves every Gaming app automatically.

## §4 Roles
MVP: 🎮 Gaming, ⬇ Downloads, 🌐 Default. Later: 🔒 VPN, 💼 Work, 📺 Streaming, custom.

## §5 User must choose the adapters
Never silently decide which adapter is Gaming or Downloads. The app may recommend; the user
approves.

```text
┌─────────────────────────────────────────────┐
│              SET UP NETROUTE                │
│ Choose your network connections             │
│ 🎮 Gaming network                            │
│ [ Ethernet — Intel I225-V             ▼ ]  │
│ ⬇ Download network                           │
│ [ Wi-Fi — Intel AX200                ▼ ]   │
│                 [Continue]                  │
└─────────────────────────────────────────────┘
```

## §6 Adapter discovery
Detect Ethernet, Wi-Fi, USB Ethernet, cellular/5G, VPN, WireGuard, OpenVPN, Hyper-V, WSL,
VMware, VirtualBox, other virtual/tunnel adapters. Show friendly info (name, hardware, speed,
🟢 Connected). Interface index / LUID / GUID belong in Advanced Diagnostics only.

## §7 Stable adapter identity
Persist roles by interface LUID/GUID, never by name or IP. Resolve name, IP, gateway, DNS,
state, link speed dynamically.

## §8 Adapter status
Each role has health:

```text
🎮 GAMING
Ethernet
🟢 Connected
12 ms · 0.1% packet loss · 1 Gbps · Internet available
```

Offline:

```text
🎮 GAMING
Ethernet
🔴 Offline
3 games are configured to use Gaming.
Strict mode is protecting them from falling back to another connection.
[Change Gaming Network]
```

## §9 Application model
Normal Win32: executable path, app identity, publisher, process identity.
Packaged/Store/Game Pass: package identity, package family name, application identity,
AUMID, executable/process info. Package identity ≠ application identity. Persist the most
stable identity available.

## §10 Game Pass is first-class
Add Game Pass games without finding an executable. Add App offers 🎮 Games, 📦 Game Pass /
Microsoft Store, ⬇ Download apps, 📁 Browse for EXE. Click the game → "Where should it
connect?" → 🎮 Gaming (Ethernet) / ⬇ Downloads (Wi-Fi) / 🌐 Default → done. Never require
access to `C:\Program Files\WindowsApps`.

## §11 Packaged application model
Game Pass ≠ UWP. A package can contain multiple applications; AUMID names the application.
Discovery: installed app → packaged? → (package/app identity → AUMID) or (EXE identity) →
runtime process discovery.

## §12 Process discovery
Identify the actual processes of an app at launch. Launcher.exe ≠ Game.exe. Determine which
process actually generates network traffic.

## §13 Process tree
Show related processes when useful:

```text
HALO INFINITE
✓ Halo.exe            Gaming
✓ NetworkProcess.exe  Gaming
○ CrashReporter.exe   Default
```

Allow "☑ Apply to related processes", but don't blindly route every child of a launcher.

## §14 Application rule
Full rule: Application, Destination role, Resolved adapter, Mode (Strict), Kill switch,
IPv4, IPv6, TCP, UDP. The user normally sees only: `Halo Infinite → 🎮 Gaming ✓ Protected`.

## §15 Strict mode (main mode for games)
Only use the selected role. If the adapter goes offline, game traffic is blocked — never
silently use the Download adapter.

## §16 Default mode
NetRoute does not override Windows routing for that app.

## §17 Preferred mode (later)
Try the role first; if unavailable, use normal Windows routing. Not the default for games.

## §18 Kill switch

```text
🔴 Halo Infinite
Gaming connection unavailable.
Traffic blocked to prevent accidental fallback onto another network.
[Change Network]
```

On return:

```text
🟢 Gaming restored
Halo Infinite
✓ Traffic restored
✓ Ethernet verified
```

## §19 Real-time enforcement
Rules apply to new connections without restarting NetRoute. Investigate ALE
reauthorization. Do not promise transparent migration of existing connections unless
testing proves it. Be honest:

```text
Existing connections: 12
New connections: Downloads
Restart Steam to move existing connections.
```

## §20–22 TCP, UDP, IPv4
TCP with verified egress. UDP from the beginning — mandatory for gaming; a TCP-only MVP is
not gaming-compatible. IPv4.

## §23 IPv6
Never allow IPv4 → Gaming while IPv6 → Downloads because IPv6 bypassed policy. If full IPv6
enforcement isn't available for a configuration, make that visible.

## §24 Traffic verification (defining feature)
Three states — CONFIGURED, VERIFIED, OBSERVED:

```text
Halo Infinite
Policy: 🎮 Gaming   Resolved: Ethernet   Actual traffic: Ethernet   Status: 🟢 VERIFIED
```

Never call a rule "protected" just because a WFP filter exists.

## §25 Leak detection

```text
🔴 NETWORK LEAK
Application: Game.exe   Expected: Ethernet   Observed: Wi-Fi   Protocol: UDP   Action: BLOCKED
```

Must be a real detection mechanism, not UI theatre.

## §26 Network diagnostics (advanced users)
Application, PID, Executable, Package, AUMID, Interface, Interface LUID, Interface index,
Local IP, Gateway, Remote IP, Protocol, Port, WFP policy, Verification.

## §27 Simple main UI

```text
┌─────────────────────────────────────────────────┐
│ NetRoute                            🟢 Protected│
├─────────────────────────────────────────────────┤
│ YOUR NETWORKS                                   │
│ 🎮 GAMING                  ⬇ DOWNLOADS          │
│ Ethernet                  Wi-Fi                │
│ 1 Gbps                    866 Mbps             │
│ 12 ms                     18 ms                │
│ 🟢 Connected              🟢 Connected          │
│ [Change]                  [Change]             │
├─────────────────────────────────────────────────┤
│ YOUR APPS                                       │
│ Halo Infinite        🎮 Gaming      🟢 Verified │
│ Steam                ⬇ Downloads    🟢 Verified │
│ Discord              🎮 Gaming      🟢 Verified │
│ Chrome               ⬇ Downloads    🟢 Verified │
│                [+ Add App]                      │
└─────────────────────────────────────────────────┘
```

## §28 Add App flow

```text
What do you want to route?
🎮 Games
📦 Game Pass / Microsoft Store
⬇ Download apps
🌐 Browser
📁 Choose application
```

then

```text
Halo Infinite
Where should it connect?
🎮 Gaming      Ethernet
⬇ Downloads   Wi-Fi
🌐 Default     Windows routing
```

One click.

## §29 Quick actions
Every app: [Change Network] [Pause] [Remove]. Change Network offers 🎮 Gaming / ⬇ Downloads /
🌐 Default. No complicated settings page.

## §30 "Why?"

```text
Halo Infinite is using Gaming because:
✓ You assigned this game to Gaming.
✓ Gaming currently points to Ethernet.
✓ Ethernet is connected.
✓ Strict mode is enabled.
✓ Actual traffic was verified on Ethernet.
🟢 Everything looks good.
```

A major usability feature.

## §31 User-friendly errors

```text
We couldn't apply this rule.
Gaming Ethernet is currently offline.
Your game is protected by Kill Switch, so traffic has been blocked.
[Reconnect] [Choose Another Network] [Advanced Details]
```

Raw `FWP_E_...` codes belong in Advanced Details only.

## §32 Profiles
Keep simple (e.g. Gaming Profile: Games → Gaming, Discord → Gaming, Steam → Downloads,
Browsers → Downloads). One-click activation.

## §33 Do not auto-reassign apps
Changing a role changes the adapter behind the role, never an application's policy.
Downloads apps stay on Downloads when Gaming is repointed.

## §34–36 Core use cases
Gaming: Ethernet, Downloads: Wi-Fi. Game → Gaming, Steam → Downloads: Steam downloads at
full speed while the game stays on Ethernet. Same for Xbox/Game Pass downloads vs the game,
and for Steam vs a Steam-launched game.

## §37 Launcher / game separation
Launcher → Downloads, actual game → Gaming, for Steam, Xbox, Epic, Battle.net, EA, Ubisoft
and others. The launcher never automatically inherits the game's policy.

## §38 Adapter change
Update the role → recalculate affected policies → apply → verify new connections → show:

```text
✓ Gaming network changed
Gaming is now: Wi-Fi
3 applications updated.
```

## §39 Network disconnect
Adapter disappears → role shows 🔴 Offline; strict apps become 🔴 Blocked. Never silently
migrate them to Downloads.

## §40 Network return
Adapter returns → apps automatically regain policy; verify actual traffic.

## §41 System tray

```text
NetRoute
🎮 Gaming      🟢 Ethernet
⬇ Downloads    🟢 Wi-Fi
Gaming Profile: Active
[Open] [Pause Enforcement] [Emergency Disable] [Exit GUI]
```

The enforcement service keeps running when the GUI closes.

## §42 Windows service
GUI → secure IPC → NetRoute Service → networking enforcement. The service owns policy
state. GUI crashes never remove enforcement.

## §43 Emergency Disable
Always available. Disables NetRoute policies and restores normal Windows networking.
Reliable even if the GUI is broken.

## §44 Installer (after MVP works)
Installs app, service, required networking component, startup, shortcuts; uninstalls
cleanly, removing only NetRoute's networking state. Never modify unrelated firewall/WFP
rules.

## §44a Updating (added after the installer)
Setup recognises what is already on the PC and offers what fits: **Update** to a newer
build, **Repair** the same one, **Go back** when the installer is older than what is
installed, or **Remove**. An update keeps the app list and settings, backs the old
version up, and rolls it back if anything fails. `/update`, `/repair`, `/remove` and
`/quiet` pick an action without asking.

In-app updating is opt-in and points wherever the user says. The feed is one JSON
document over https:

```json
{ "version": "1.2.0",
  "url": "https://example.com/NetRoute-Setup-1.2.0.exe",
  "sha256": "5E88...", "size": 76128256, "notes": "One line for the banner." }
```

`scripts\build-installer.ps1` writes this file, filled in, to `dist\updates.json`.

Rules, in order of importance:
- The **service never installs anything**. It checks, downloads and verifies; the app or
  the CLI runs setup, and Windows asks for administrator. A LocalSystem service that can
  replace its own program on the strength of a web address is a worse thing to own than a
  manual update.
- **No sha256, no download.** NetRoute's installer is not code-signed, so the digest in
  the feed is the only thing tying the bytes to the version the user chose to trust. A
  file that hashes differently is deleted, not kept. Without a digest the user gets a
  link.
- Downloads land in `%ProgramData%\NetRoute\updates`, writable only by SYSTEM and
  administrators, and are hashed again immediately before being run elevated.
- Nothing is configured out of the box: with no feed address, NetRoute never calls
  anywhere.

## §45 Security
Never disable Defender or the Windows Firewall, disable anti-cheat, modify game files,
inject DLLs, bypass Windows protections, or take ownership of WindowsApps.

## §46 Performance
Don't inspect every packet in the GUI. WFP → enforcement; service → aggregated state;
GUI → display. No packet capture in the core path unless technically necessary.

## §47 Later features (don't delay the MVP)
Bandwidth limits, per-app up/down limits, QoS, automatic interface health selection, VPN
rules, DNS policies, LAN exceptions, schedules, temporary rules, domain/IP rules,
connection and quality graphs, CLI, local API, Stream Deck, better game detection.

## §48 Do not build (v1)
Accounts, cloud sync, telemetry, subscriptions, ads, remote admin, cloud databases, "AI
networking", social features, unnecessary packet inspection, complicated rule languages.
A small local Windows utility.

## §49 Technology
C#/.NET, WinUI 3 or WPF for UI, C# service, C++ only where necessary, native WFP where
required, named pipes, JSON config.

## §50 Prove the routing mechanism early
POC: TestApp A → Ethernet, TestApp B → Wi-Fi simultaneously, with verified egress. Don't
build the GUI around an unproven mechanism.

## §51 Acceptance test
1. Open NetRoute. 2. 🎮 Gaming → Ethernet. 3. ⬇ Downloads → Wi-Fi. 4. Add a Game Pass game →
Gaming. 5. Add Steam → Downloads. 6. Start the game. 7. Start a large Steam download.
8. See `Game 🎮 Gaming Ethernet 🟢 Verified` and `Steam ⬇ Downloads Wi-Fi 🟢 Verified`.
9. Disconnect Ethernet → `Game 🔴 Blocked — Gaming network unavailable. Kill Switch active.`
while Steam continues on Wi-Fi. 10. Reconnect → `Game 🟢 Restored, Ethernet ✓ Verified`.
11. Close and restart the game → `Game → 🎮 Gaming ✓ Automatically enforced`.

## §52 First-run experience (< 1 minute)

```text
WELCOME
Let's set up your connections.
🎮 Gaming      [ Ethernet ▼ ]
⬇ Downloads    [ Wi-Fi ▼ ]
        [Finish]
```

then

```text
✓ You're ready.
Games → Ethernet
Downloads → Wi-Fi
```

## §53 Normal user rule
A normal user never needs to understand WFP, ALE, AUMID, package identity, LUID, route
metrics, gateways, PIDs, filters or callouts. They understand 🎮 Gaming, ⬇ Downloads,
🌐 Default. That's it.

## §54 Advanced user rule
Everything important is inspectable in Diagnostics: app identity, package identity, AUMID,
process, PID, adapter, LUID, index, local IP, gateway, remote endpoint, protocol, WFP
state, verification state.

## §55 Workflow
Build → run → test → verify → commit → continue. Keep the app runnable throughout.

## §56 First task (done)
Inspect, research, choose the simplest reliable architecture, decide whether a driver is
required, build adapter discovery, build the smallest routing POC, verify traffic, then
build the UI. See `docs/RESEARCH.md`.

## §57 Philosophy
Simple enough for anyone. Powerful enough for advanced users. Fast to configure. Actually
enforced at the Windows networking level. Honest about what is and isn't verified.

```text
                    NETROUTE
        🎮 GAMING              ⬇ DOWNLOADS
        Ethernet               Wi-Fi
     ┌─────┴─────┐         ┌─────┴─────┐
    Game       Discord    Steam      Browser
```
