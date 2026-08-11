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

## Rejected approaches

- **Network compartments** — genuine per-process routing tables, but assigning an
  arbitrary process to a compartment is not publicly documented. Too fragile.
- **NDIS filter / packet mangling (WinDivert-style)** — would work without a WFP
  callout, but puts packet inspection in the enforcement path, which §46 rules out,
  and SNAT-style rewriting interacts badly with anti-cheat and stateful flows.
- **Route metric manipulation** — global, not per-application. Wrong tool.
