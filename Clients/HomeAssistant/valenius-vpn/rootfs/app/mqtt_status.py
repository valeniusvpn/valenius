"""Publishes a Home Assistant MQTT-discovery binary_sensor for VPN connectivity.

This has no equivalent in the Linux client -- it's new, add-on-only code. HA add-ons
have no direct "create an entity" API; MQTT discovery (a retained config payload under
`homeassistant/binary_sensor/<id>/config`, per Home Assistant's MQTT integration) is the
standard way an add-on surfaces state as a real entity. Requires an MQTT broker (e.g. the
official Mosquitto add-on) -- see run.py._mqtt_settings for how the broker is located.
"""
from __future__ import annotations

import json
import logging

import paho.mqtt.client as mqtt

log = logging.getLogger(__name__)

_DISCOVERY_PREFIX = 'homeassistant'


class MqttStatus:
    def __init__(self, client_id: str, display_name: str, host: str, port: int,
                 username: str | None, password: str | None):
        self._object_id = f'valenius_{client_id.replace("-", "")}'
        self._display_name = display_name
        self._state_topic = f'valenius/{client_id}/status'
        self._availability_topic = f'valenius/{client_id}/availability'
        self._client = mqtt.Client(client_id=f'valenius-vpn-{client_id}')
        if username:
            self._client.username_pw_set(username, password or None)
        self._client.will_set(self._availability_topic, payload='offline', retain=True)
        self._host = host
        self._port = port
        self._device_id = client_id

    def connect(self) -> None:
        try:
            self._client.connect(self._host, self._port, keepalive=60)
            self._client.loop_start()
            self._client.publish(self._availability_topic, payload='online', retain=True)
            self._publish_discovery()
        except Exception:
            log.warning("MQTT connect to %s:%s failed", self._host, self._port, exc_info=True)

    def _publish_discovery(self) -> None:
        config_topic = f'{_DISCOVERY_PREFIX}/binary_sensor/{self._object_id}/config'
        payload = {
            'name': f'{self._display_name} connected',
            'unique_id': f'{self._object_id}_connected',
            'state_topic': self._state_topic,
            'availability_topic': self._availability_topic,
            'payload_on': 'ON',
            'payload_off': 'OFF',
            'device_class': 'connectivity',
            'device': {
                'identifiers': [self._device_id],
                'name': self._display_name,
                'manufacturer': 'Stranto Business Solutions GmbH',
                'model': 'Valenius VPN (Home Assistant add-on)',
            },
        }
        self._client.publish(config_topic, payload=json.dumps(payload), retain=True)

    def publish_state(self, connected: bool, verified: bool) -> None:
        # `verified` (gateway/handshake-confirmed, not just "wg-quick up succeeded") is
        # available for a future richer entity (e.g. an extra attribute); the binary_sensor
        # itself tracks plain connected/disconnected for now, matching what was asked.
        self._client.publish(self._state_topic, payload='ON' if connected else 'OFF', retain=True)

    def disconnect(self) -> None:
        try:
            self._client.publish(self._availability_topic, payload='offline', retain=True)
            self._client.loop_stop()
            self._client.disconnect()
        except Exception:
            pass
