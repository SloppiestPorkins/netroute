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
- The driver has not yet been exercised on this machine. That is what step 3 is for.
- Not yet checked: the Windows Firewall sublayer's weight on this machine, which would
  confirm the "low weight" claim, and anti-cheat behaviour with the driver loaded.
