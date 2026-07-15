"""Length-prefixed JSON framing — identical to the Windows named-pipe protocol.

Wire format:
  4 bytes  signed int32, little-endian  – payload byte count
  N bytes  UTF-8 JSON                   – message body

Max message: 1 MiB (matches Windows Service limit).
"""
from __future__ import annotations

import asyncio
import struct

MAX_MESSAGE_BYTES = 1_048_576


async def read_message(reader: asyncio.StreamReader) -> str:
    raw_len = await reader.readexactly(4)
    length = struct.unpack('<i', raw_len)[0]
    if length <= 0 or length > MAX_MESSAGE_BYTES:
        raise ValueError(f"Invalid message length: {length}")
    data = await reader.readexactly(length)
    return data.decode('utf-8')


async def write_message(writer: asyncio.StreamWriter, message: str) -> None:
    data = message.encode('utf-8')
    writer.write(struct.pack('<i', len(data)) + data)
    await writer.drain()


def read_message_sync(sock) -> str:
    """Synchronous version used by the tray-app IPC client."""
    raw_len = _recv_exactly(sock, 4)
    length = struct.unpack('<i', raw_len)[0]
    if length <= 0 or length > MAX_MESSAGE_BYTES:
        raise ValueError(f"Invalid message length: {length}")
    return _recv_exactly(sock, length).decode('utf-8')


def write_message_sync(sock, message: str) -> None:
    data = message.encode('utf-8')
    sock.sendall(struct.pack('<i', len(data)) + data)


def _recv_exactly(sock, n: int) -> bytes:
    buf = bytearray()
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            raise EOFError(f"Connection closed after {len(buf)}/{n} bytes")
        buf += chunk
    return bytes(buf)
