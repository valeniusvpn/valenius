# Valenius

**Valenius** is a management platform for WireGuard® VPNs that lets **non-administrator users**
connect to a company VPN without elevated privileges. An admin installs it once; users click a
tray icon — or tap the mobile app — and the VPN connects. It is built for MSP operations and
Windows-centric SMB environments, with clients for **Windows, Linux, macOS, and Android**.

## Editions

- **Community Edition** — free, self-hosted, **AGPLv3**. Full admin panel, manual `.conf` upload,
  QR device pairing, OIDC/TOTP sign-in, per-OS auto-update, client diagnostics, audit log,
  configuration encryption at rest, and simultaneous multi-VPN on the desktop clients.
- **Pro** — commercial. Adds automated WireGuard peer provisioning, MFA session gating,
  per-client traffic accounting, multi-tenant / MSP features, and appliance fleet management.

## Documentation

Full technical documentation (English & German): **https://documentation.valenius.com/**

## Download & commercial offerings

Product information, the Pro edition, and managed/cloud offerings:
**https://www.valenius.com/** — the mobile app is available on Google Play.

## License

The Community Edition is licensed under the **GNU Affero General Public License v3.0** — see
[`LICENSE`](LICENSE). Unlimited endpoints, no cost.

## Repository layout

| Path | Contents |
|------|----------|
| `Backend/` | ASP.NET Core backend |
| `Clients/` | Windows, Linux, macOS, and mobile (Flutter) clients |
| `Shared/`  | Shared IPC wire types |

---

WireGuard® is a registered trademark of Jason A. Donenfeld. Valenius builds on genuine,
unmodified WireGuard® and is **not** a project of, nor endorsed by, Jason A. Donenfeld or the
WireGuard project.
