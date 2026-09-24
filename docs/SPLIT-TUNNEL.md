# Moving apps onto another adapter: the split-tunnel driver

NetRoute's own WFP filters can **block** an app from using the wrong network (strict mode,
kill switch). They cannot **move** an app onto an adapter Windows wouldn't choose for it.
Moving it means rewriting the socket's local address at WFP bind/connect redirect, and only
a kernel callout driver can do that.

## Why not NetRoute's own driver (yet)

The owner's PC has Secure Boot on and runs BattlEye (DayZ), EA Javelin (Battlefield 6) and
Easy Anti-Cheat (Halo Infinite). A self-built driver would only load in test-signing mode.
That mode needs Secure Boot off, which Javelin forbids, and BattlEye and EAC refuse to run
under it. A NetRoute driver has to be Microsoft attestation-signed (EV certificate plus
Partner Center) before anyone can use it day to day.

## The driver we use

[mullvad/win-split-tunnel](https://github.com/mullvad/win-split-tunnel), the open-source
driver behind Mullvad VPN's split tunnelling. It is dual-licensed MPL-2.0 / GPL-3.0 and
ships Microsoft-signed inside Mullvad VPN. NetRoute talks to it the way Mullvad's daemon
does (`talpid-core/src/split_tunnel/windows/driver.rs`), over its IOCTL interface:
`\\.\MULLVADSPLITTUNNEL`, device type `0x8000`.

What it does:
- It splits processes whose image path is configured, **and their descendants**, because
  exclusion is inherited.
- It rebinds split processes' wildcard binds and connects to the *internet* address.
- It blocks split processes from the *tunnel* address.
- It leaves every other process alone.

### How NetRoute's roles map onto it

Inheritance decides the mapping. If Steam were the split process, every game Steam launches
would inherit the split and be dragged onto the Download network, which breaks brief §36/§37.
So:

| Driver concept | NetRoute |
|---|---|
| tunnel address (kept off) | the **Downloads** adapter, which is also Windows' default route |
| internet address (moved onto) | the **Gaming** adapter |
| split processes | apps assigned to **Gaming** |

Steam, browsers and Windows Update follow the default route to Downloads. Games assigned to
Gaming, and anything they launch, are moved to Gaming and blocked from Downloads. A game that
Steam launches is split by its own path, not by Steam's.

### Hazards NetRoute handles

- **The device is exclusive.** Only one handle can be open, so Mullvad's daemon must not be
  running. `scripts/split-tunnel-setup.ps1` disables it.
- **Never stop the driver unless it has been reset first.** Unloading it while it is
  initialised makes it call `KeBugCheckEx` deliberately. NetRoute turns splitting off with
  the `Reset` IOCTL and never stops the driver service.
- **Splitting survives a NetRoute crash.** The driver is not reset when its handle closes.
  Emergency Disable reopens the device and resets it.
- **The driver needs two WFP sublayers to exist.** NetRoute creates them as persistent,
  low-weight sublayers (`SplitTunnelSublayers`). Low weight keeps the driver's hard permits
  from overriding the Windows Firewall (§45). Uninstall removes them only after a reset.

### Known limits (from the driver's README)

- DNS lookups go through the DNS Client service, so they follow the default route.
- Split apps can't use UDP loopback without an explicit bind.
- Multicast joins on the wildcard address don't work for split apps.

## Setup (owner, one time)

1. Install Mullvad VPN from <https://mullvad.net/download>. You don't need an account and
   shouldn't connect.
2. In an administrator PowerShell, run `scripts\split-tunnel-setup.ps1`. It verifies the
   driver is validly signed by Mullvad, disables Mullvad's daemon, and starts the driver.
3. Prove it works: `dotnet run --project src\NetRoute.Poc -- split`, from an administrator
   terminal. It moves an **unbound** `curl` (TCP) and `nslookup` (UDP) onto the non-default
   adapter and checks where they actually egress.

## Status

- Protocol serialisation: unit-tested against the layouts in Mullvad's client.
- **TCP proven on the owner's machine (10 Sept 2026):** an unbound `curl` moved from
  Ethernet to Wi-Fi 2 with the driver Engaged, and went back to Ethernet after Reset.
  See docs/RESEARCH.md.
- **UDP proven too (second run, same day):** an unbound `nslookup` query to ns1.google.com
  over IPv4 left from Wi-Fi 2's ISP (82.132.230.153), not Ethernet's (84.67.213.242). The
  first run had tested IPv6 by mistake (see RESEARCH.md).
- IPv6 is not moved when the target connection has no IPv6. NetRoute must block IPv6 for
  those apps (§23).
- Not yet checked: the Windows Firewall sublayer's weight on this machine, which would
  confirm the "low weight" claim, and anti-cheat behaviour with the driver loaded.

## What moving an app cannot do

Moving an app onto a connection means the driver rewrites that app's socket binds. Mullvad's
README lists the consequences, and says there are no generally applicable mitigations:

- an excluded (moved) app **cannot bind `inaddr_any`/`in6addr_any`** — wildcard binds are
  redirected to the address of the interface it was moved onto;
- **multicast reception breaks**: the group join still happens on `inaddr_any` while the socket
  is bound to one interface, so arriving traffic doesn't match;
- an excluded app **cannot reach localhost over UDP** unless it bound `127.0.0.1` itself;
- exclusion is **inherited by child processes**, so a launcher's games inherit it too.

That rules out moving any app that hosts or finds things on the local network. Minecraft is the
worked example, and the reason `LocalHostingApps` exists: a LAN world is announced from a
wildcard UDP socket to the multicast address `224.0.2.60:4445`, the game runs as a child of the
launcher, and the launcher is built on CEF, which talks to localhost. Moving it breaks LAN
worlds and can stop it starting (seen on 24 September 2026: sockets rewritten onto the Ethernet
IPv6 address, and `Minecraft.exe` faulting inside `libcef.dll`).

NetRoute therefore resolves those apps to "no enforcement" and explains that in "Why?", rather
than appearing to protect an app it has broken. Everything else still applies to them: they are
never blocked, and they use whichever connection Windows picks.

Source: <https://github.com/mullvad/win-split-tunnel> (README, "Limitations").
