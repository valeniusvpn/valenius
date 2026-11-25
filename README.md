# Valenius

**Valenius** is a centrally managed platform for WireGuard® VPN access. Its free, self-hosted
Community Edition lets administrators distribute VPN configurations while users connect from
**Windows, Linux, macOS, or Android** — plus an always-on **Home Assistant** add-on. After the
desktop client is installed, users do not need local administrator privileges to connect. Valenius is designed for business and MSP environments.

<img src="https://www.valenius.com/images/screenshots/dashboard-full.webp" alt="Valenius admin dashboard" width="800">

## Quick Start

### Prerequisites

You need a Linux host with:

- Git
- Docker Engine
- Docker Compose (`docker compose`)

### Install

Clone the repository and run the interactive Community Edition installer:

```bash
git clone https://github.com/valeniusvpn/valenius.git
cd valenius
./install.sh
```

The installer builds the backend image, creates the Docker Compose deployment in `valenius/`,
generates the required secrets, and starts Valenius with PostgreSQL. During setup it asks for the
bootstrap administrator email and the backend port (`9001` by default).

### First login

When installation completes, the installer prints the login URL and a generated administrator
password. With the default port, Valenius is available locally at:

```text
http://localhost:9001/
```

Use the printed administrator email and password to log in. The credentials are also stored in
`valenius/.env`; keep this file secure and do not commit it. Change the bootstrap administrator
password after your first login.

> [!IMPORTANT]
> The Quick Start uses HTTP for local access. Before exposing Valenius outside the local host or a
> trusted network, configure HTTPS with a TLS-terminating reverse proxy. OIDC login and secure
> cookies require HTTPS. See the [Community self-hosting guide](https://documentation.valenius.com/self-hosting/community/).

### First steps

1. Change the bootstrap administrator password under **Admin → Users**, or configure OIDC/SSO
   under **Admin → Settings**.
2. Create your first customer under **Admin → Customers**.
3. Install a desktop client and assign its device to the customer, or pair the mobile client using
   the customer's one-time QR code.
4. Open the client's **Details → VPN** page and upload its WireGuard® `.conf` file.
5. Connect to the VPN from the Valenius client.

For the complete client installation and pairing workflow, see the
[Valenius documentation](https://documentation.valenius.com/).

### Manage the installation

Run these commands from the repository root.

Restart the deployment:

```bash
docker compose -f valenius/docker-compose.yml up -d
```

Stop the deployment:

```bash
docker compose -f valenius/docker-compose.yml down
```

Pull the latest source, rebuild, and restart the deployment:

```bash
./update.sh
```

## Editions

- **Community Edition** — free, self-hosted, **AGPLv3**. Full admin panel, manual `.conf` upload,
  QR device pairing, OIDC/TOTP sign-in, per-OS auto-update with stable/beta channels, client
  diagnostics and remote service restart, audit log, configuration encryption at rest,
  simultaneous multi-VPN, standalone mode (no backend) and connect-before-login device tunnels on
  the desktop clients, and a Prometheus/Zabbix monitoring endpoint.
- **Pro** — commercial. Adds automated WireGuard peer provisioning through a managed WireGuard
  server, MFA session gating (TOTP and push-to-approve), server-side tunnel liveness detection,
  per-client traffic accounting, server connectivity testing, a Management API with an MCP server
  for AI assistants, multi-tenant / MSP features, and appliance fleet reporting.

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
| `Clients/` | Windows, Linux, macOS, mobile (Flutter), and Home Assistant clients |
| `Shared/`  | Shared IPC wire types |
| `integrations/` | Official Zabbix template for the monitoring endpoint |
| `install.sh`, `update.sh` | Community Edition installer and upgrade script |

---

WireGuard® is a registered trademark of Jason A. Donenfeld. Valenius builds on genuine,
unmodified WireGuard® and is **not** a project of, nor endorsed by, Jason A. Donenfeld or the
WireGuard project.
