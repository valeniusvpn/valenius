# Valenius VPN

Keeps a [Valenius](https://github.com/valeniusvpn/valenius) WireGuard VPN connected at
all times — unlike the roaming desktop clients, this add-on connects unconditionally and
reconnects aggressively on any drop, since a Home Assistant instance is a fixed-location
server, not a laptop that needs an "away from the office" policy.

## Installing this add-on

1. In Home Assistant: **Settings → Add-ons → Add-on Store → ⋮ (top right) →
   Repositories**, and add `https://github.com/valeniusvpn/valenius`.
2. Refresh the store — **Valenius VPN** appears under a new "Valenius" section.
3. Click it → **Install**.

## Setup

1. Ask your Valenius administrator for your server's **Backend URL** and **API key**
   (Admin → Settings on the backend, or the value baked into your fleet's installers).
2. Set `backend_url` and `api_key` in this add-on's Configuration tab, then start it.
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
| `api_key` | yes | The fleet's client API key |
| `display_name` | no | Friendly name shown in Home Assistant's MQTT entity **and** the name this install registers under in the backend's Admin → Clients. Strongly recommended if you run more than one Valenius add-on install — leave it unset and every install still gets a unique (but less readable) generated name |
| `profile_name` | no | Pin a specific profile name instead of the first one the backend assigns |
| `mqtt_host` / `mqtt_port` / `mqtt_username` / `mqtt_password` | no | Only needed for an external MQTT broker. If the official **Mosquitto broker** add-on is installed, connection details are discovered automatically and these can be left blank |

## Home Assistant entity

If an MQTT broker is available (the Mosquitto add-on, or manually configured), a
`binary_sensor` reporting VPN connected/disconnected is published automatically via MQTT
discovery — no extra configuration needed inside Home Assistant itself.

## Requirements

Needs `NET_ADMIN`/`NET_RAW` capabilities and host networking to create the WireGuard
interface — both are already declared in this add-on's configuration.
