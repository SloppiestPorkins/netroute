# mullvad-split-tunnel.sys

Mullvad's Microsoft-signed Windows split-tunnel driver, which NetRoute uses to move apps onto
a chosen connection (see `docs/SPLIT-TUNNEL.md`). The installer bundles it so no Mullvad VPN
install is needed.

| | |
|---|---|
| Version | 1.3.0.0 |
| Signed by | Mullvad VPN AB (Authenticode: Valid) |
| SHA-256 | `10CF25BBCFE51FD663A1FEC88A98E9B858F3A579589BB2EC496B66E4FDD1B201` |
| Taken from | `C:\Program Files\Mullvad VPN\resources\` (Mullvad VPN app install), 14 Sept 2026 |
| Source | https://github.com/mullvad/win-split-tunnel |
| Licence | MPL-2.0 or GPL-3.0-or-later, recipient's choice. NetRoute uses MPL-2.0. |

Unmodified. `scripts/build-installer.ps1` refuses to package it unless its signature is valid
and from Mullvad. To update it, copy the newer file from a Mullvad VPN install, check the
IOCTL protocol in `src/NetRoute.Windows/Split/SplitTunnelProtocol.cs` still matches, and update
this table.
