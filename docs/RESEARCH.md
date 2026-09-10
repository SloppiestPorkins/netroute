# NetRoute — routing mechanism research

Status: **routing primitive proven on real hardware (TCP + UDP, simultaneous).**

## The question

Can NetRoute drive per-application traffic out of a chosen adapter without DLL
injection, binary patching, Winsock hooking, or in-process API hooks (§2)?

## Answer

Yes, but the work splits across two mechanisms with very different costs.

| Capability | Mechanism | Kernel driver? |
| --- | --- | --- |
| Strict mode, kill switch, per-app block | User-mode WFP filters at `FWPM_LAYER_ALE_AUTH_CONNECT_V4/V6`, keyed on `FWPM_CONDITION_ALE_APP_ID` + `FWPM_CONDITION_IP_LOCAL_INTERFACE` | No |
| Leak detection, VERIFIED state | WFP net events + `GetExtendedTcpTable`/`GetExtendedUdpTable` + egress probing | No |
| **Actual redirection** | WFP callout at `FWPM_LAYER_ALE_BIND_REDIRECT_V4/V6` | **Yes** |

### Why the driver is unavoidable

Bind and connect redirection are callout layers. A callout must be registered with
`FwpsCalloutRegister1` or higher, which is a kernel-mode-only API — there is no
user-mode equivalent, and user-mode callouts exist only at the RPC layers, which
are irrelevant here.

This matters because blocking is not redirection. A user-mode-only build could stop
Steam from using Ethernet, but it could not move Steam onto Wi-Fi — Steam would
simply fail to connect. The headline use case in §34 requires redirection, so the
driver is required for the product to do what it claims.

Development can proceed under test-signing. Production distribution needs an EV
certificate and Microsoft attestation signing; that is a lead-time item, not a
technical blocker, and it should be started early.

## What was proven (§50)

`src/NetRoute.Poc` binds two sockets to two different adapter source addresses
concurrently and independently confirms where each one actually egressed, by
asking the far end what public address it saw.

Measured on the development machine, which has two genuinely separate uplinks:

```
Gaming    TCP  bound 192.168.0.51   egress 84.67.213.242
Downloads TCP  bound 192.168.7.6    egress 10.142.17.4
Gaming    UDP  bound 192.168.0.51   egress 84.67.213.242
Downloads UDP  bound 192.168.7.6    egress 82.132.184.210

TCP: PROVEN   UDP: PROVEN
```

Source-address binding does steer egress, on both protocols, at the same time.
That is the primitive the bind-redirect callout will supply to unmodified
applications, so the remaining risk sits in the driver, not in the routing model.

The Wi-Fi TCP and UDP probes disagree (`10.142.17.4` vs `82.132.184.210`) because
that uplink is behind carrier-grade NAT with a transparent HTTP proxy. Both differ
from Ethernet, so the proof holds, but it is a useful reminder that the TCP probe
alone is not a trustworthy verification signal on every network. Verification
should prefer the UDP path and treat RFC 6598 space as "proxied, inconclusive".

## What was proven: WFP enforcement

`netroute-poc wfp`, run elevated. Pins `curl.exe` to Gaming (Ethernet), then runs
curl bound to each adapter in turn. A working baseline is taken first so a later
failure can be attributed to enforcement rather than to the network.

```
Baseline          Ethernet OK (84.67.213.242)   Wi-Fi OK (82.132.184.210)
12 filters installed across 1 rule
Enforced          Ethernet OK (84.67.213.242)   Wi-Fi BLOCKED
After teardown    Wi-Fi OK (82.132.184.210)
```

Per-application, per-interface enforcement works, and removing the filters restores
normal networking. Strict mode, the kill switch and leak prevention rest on this and
are therefore real today.

## What was proven: moving an unmodified app (10 Sept 2026)

`netroute-poc split`, run elevated by the owner, with Secure Boot on and Mullvad's
Microsoft-signed split-tunnel driver (see docs/SPLIT-TUNNEL.md). `curl` made an ordinary
connection with no `--interface`:

```
Baseline          Ethernet 84.67.213.242   Wi-Fi 2 82.132.230.153   unbound curl 84.67.213.242
Driver state      Engaged
Split on          unbound curl 82.132.230.153  -> moved onto Wi-Fi 2   PROVEN (TCP)
After Reset       unbound curl 84.67.213.242   -> back on Ethernet
```

This is the mechanism the headline use case depends on (§34), and it works on the owner's
hardware without a self-built driver. The driver engaged with a physical adapter's address
as its "tunnel" address, which the driver's source did not obviously guarantee.

The first run's UDP check was invalid, and revealing. `nslookup` queried `ns1.google.com`
by name, resolved it to IPv6, and sent the query over IPv6 via Ethernet, the only adapter
with IPv6. It was not moved, because the driver can only move IPv6 onto a connection that
has IPv6. That is the §23 bypass happening for real: in production, NetRoute's WFP filters
must block IPv6 for apps pinned to an IPv4-only role, so they fall back to IPv4 and get
moved. The proof now queries ns1.google.com's IPv4 address for the UDP check and reports
IPv6 separately.

## Verification design note (§24)

The probe measures *observed* egress. A WFP filter existing is only *configured*.
The product is not allowed to report "protected" on the strength of the filter
alone, so these stay separate states and the UI shows which one it has.

## Open finding: IPv6 asymmetry (§23)

On this machine only Ethernet has an IPv6 default route; Wi-Fi is IPv4-only.

Any app assigned to a role resolving to an IPv4-only adapter will send IPv6 traffic
out of whichever adapter does have a v6 route — silently bypassing policy. This is
the exact failure §23 forbids. Enforcement must explicitly block IPv6 for roles on
IPv4-only adapters rather than leaving it to routing, and the UI has to surface
that the role is IPv4-only.

`AdapterDiscovery` already detects this and the POC reports it.

## Application discovery findings

Measured against the development machine (18 Xbox titles, 5 launchers, ~150 desktop apps).

### Game Pass detection must not be inferred

The first implementation guessed, by looking for an `XboxGames` path or a Gaming
Services package dependency. It found **zero** of the 17 installed titles, and did so
silently. Neither signal works: the packages live under `WindowsApps` like any other
Store app, their content sits in a separate `XboxGames` tree, and they declare no
Gaming Services dependency.

The authoritative source is
`HKLM\SOFTWARE\Microsoft\GamingServices\PackageRepository\Root`, where Xbox records
the package full name of everything it installed. Reading it is unprivileged and
survives title updates. Converting those full names to family names is covered by
tests, because getting it wrong reproduces the original silent zero-match failure.

DLC, skin packs and art collections also appear in that repository. They are excluded
by requiring a launchable application entry — they have no AUMID, so there is no
process and nothing to route.

### One executable per application is the wrong model for games

Resolving a desktop app to a single executable is unreliable in a way that matters.
Uninstall entries do not record the main binary, so it has to be inferred from the
install directory, and games ship several executables side by side:

```
DayZ\  ->  CrashReporter.exe   first implementation picked this
           DayZDiag_x64.exe    after excluding support executables
           DayZ_BE.exe         after ranking by folder-name prefix
           DayZ_x64.exe        the process that actually carries game traffic
```

Filtering support executables and ranking by name gets closer, but the last step is
not reachable by naming heuristics — `DayZ_BE.exe` is a legitimate launcher, and
which of the two carries traffic is only observable at runtime.

This is the §12/§13 problem, and the resolution is process discovery rather than
better guessing: identify the processes an application actually spawns, and determine
which are generating network traffic. Until that exists, a rule created from registry
discovery alone can look correct, report as "configured", and protect nothing —
exactly the outcome §24 exists to prevent. Heuristic tuning was stopped here
deliberately rather than pursued to diminishing returns.

## Rejected approaches

- **Network compartments** — genuine per-process routing tables, but assigning an
  arbitrary process to a compartment is not publicly documented. Too fragile.
- **NDIS filter / packet mangling (WinDivert-style)** — would work without a WFP
  callout, but puts packet inspection in the enforcement path, which §46 rules out,
  and SNAT-style rewriting interacts badly with anti-cheat and stateful flows.
- **Route metric manipulation** — global, not per-application. Wrong tool.
