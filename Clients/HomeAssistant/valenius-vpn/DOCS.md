# Valenius VPN

Keeps a [Valenius](https://github.com/valeniusvpn/valenius) WireGuard VPN connected at
all times while authorized. The add-on reconnects on drops without an "away from the
office" policy, since a Home Assistant instance is a fixed-location server.

## Installing this add-on

1. In Home Assistant: **Settings → Add-ons → Add-on Store → ⋮ (top right) →
   Repositories**, and add `https://github.com/valeniusvpn/valenius`.
2. Refresh the store — **Valenius VPN** appears under a new "Valenius" section.
3. Click it → **Install**.

## Setup

1. Ask your Valenius administrator for your server's **Backend URL**.
2. Set `backend_url`, leave `api_key` blank for a fresh install, then start the add-on.
   It receives and privately persists its individual credential while pending.
3. On first start the add-on registers itself with the backend as a **pending** client,
   named after `display_name` if you set one (otherwise a generated `valenius-ha-…` name
   — either way, unique per install). Your administrator needs to find it by that name in
   **Admin → Clients** and activate it (assign it to a customer), exactly like any other
   Valenius client.
4. Once activated, the add-on claims whatever WireGuard profile the backend assigns —
   your customer's own integrated-server profile if you use the built-in Valenius
   sidecar, or an admin-uploaded config otherwise — and connects it. From then on it
   just stays connected.

## Options

| Option | Required | Description |
|---|---|---|
| `backend_url` | yes | Your Valenius backend URL, e.g. `https://vpn.example.com` |
| `api_key` | no | Individual `vlnd_` credential for administrator migration/recovery; blank for a fresh install |
| `display_name` | no | Friendly name shown in Home Assistant's MQTT entity **and** the name this install registers under in the backend's Admin → Clients. Strongly recommended if you run more than one Valenius add-on install — leave it unset and every install still gets a unique (but less readable) generated name |
| `profile_name` | no | Pin a specific profile name instead of the first one the backend assigns |
| `offline_access_days` | no | Days a successful backend approval allows new managed connections while offline (default **7**, integer **1–365**); restart after changing it |
| `mqtt_host` / `mqtt_port` / `mqtt_username` / `mqtt_password` | no | Only needed for an external MQTT broker. If the official **Mosquitto broker** add-on is installed, connection details are discovered automatically and these can be left blank |

## Backend outages and upgrades

After a successful online refresh, cached profiles and approval allow reconnects
for `offline_access_days` from that check. Restarting the add-on never extends the
deadline. Explicit deactivation blocks new connections; MFA still needs its own
valid authorization and expires at its original deadline. Always-on mode cannot
bypass either check. This deadline gates new connections; an established non-MFA
tunnel is not disconnected merely because the cached approval expires.

Upgrade while the backend is reachable: older caches need one online refresh.
Keep `/data/valenius` (identity, encrypted profiles and encryption key). If the log
reports unreadable registration or a persistence failure, repair storage before
restarting; do not delete registration to work around it. A failed save can leave
older approval on disk until its original deadline. An empty backend URL stops
startup. Updates remain managed by Home Assistant Supervisor.

## Existing devices: credential migration

The backend rejects shared installation keys by default. With add-on **1.0.8**, an
administrator can enable the backend's temporary migration window so an existing
add-on automatically saves and adopts an individual credential on its next check-in.
Legacy access remains available until the saved credential is first used or the
window expires. Failed saves can retry. Shared-key impersonation remains possible
for unmigrated devices during that window; coordinate it with your administrator.

Without that window, use administrator recovery. Preserve `/data/valenius` and
all profiles/encryption keys. In the backend, an administrator opens this client's
**Access → Download replacement credential**. Enter that individual `vlnd_` value in
`api_key`, restart the add-on, verify a successful check-in, then clear the option.
The protected persistent key file retains it. A replacement invalidates the previous
credential immediately; do not reuse it for another add-on or device.

## Home Assistant entity

If an MQTT broker is available (the Mosquitto add-on, or manually configured), a
`binary_sensor` reporting VPN connected/disconnected is published automatically via MQTT
discovery — no extra configuration needed inside Home Assistant itself.

## Requirements

Needs `NET_ADMIN`/`NET_RAW` capabilities and host networking to create the WireGuard
interface — both are already declared in this add-on's configuration.

## Response-size limits

Version 1.0.7 bounds backend responses to 16 MiB. Oversized responses fail the check-in
rather than consuming unbounded memory. Preserve the existing identity/credential and
profiles when upgrading. Supervisor continues to own add-on updates.
