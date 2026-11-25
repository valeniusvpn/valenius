import asyncio
import json
import struct
import tempfile
import unittest
from pathlib import Path
from unittest.mock import AsyncMock, patch

from daemon import ipc_server
from shared.messages import PipeResponse
from shared.protocol import read_message


class IpcLimitsTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.path = str(Path(self.directory.name) / 'ipc.sock')
        self.patch = patch.object(ipc_server, 'SOCKET_PATH', self.path)
        self.patch.start()
        self.addCleanup(self.patch.stop)
        self.server = ipc_server.IpcServer(None)
        self.server._dispatch = AsyncMock(return_value=PipeResponse(Success=True))
        self.clients = []
        await self.server.start()

    async def asyncTearDown(self):
        for _, writer in self.clients:
            writer.close()
        await self.server.stop()
        for _ in range(100):
            if not self.server._active:
                break
            await asyncio.sleep(.01)
        self.assertEqual(self.server._active, 0)

    async def connect(self):
        client = await asyncio.open_unix_connection(self.path)
        self.clients.append(client)
        return client

    async def test_admission_is_bounded_and_recovers(self):
        for _ in range(32):
            await self.connect()
        await asyncio.sleep(.05)
        self.assertEqual(self.server._active, 32)
        reader, _ = await self.connect()
        self.assertEqual(await asyncio.wait_for(reader.read(), 1), b'')
        self.server._dispatch.assert_not_called()
        self.clients[0][1].close()
        await asyncio.sleep(.05)
        reader, writer = await self.connect()
        data = json.dumps({'Command': 2}).encode()
        writer.write(struct.pack('<i', len(data)) + data)
        await writer.drain()
        self.assertTrue(json.loads(await asyncio.wait_for(read_message(reader), 1))['Success'])
        self.server._dispatch.assert_awaited_once()

    async def test_absolute_frame_deadline_rejects_trickle_before_dispatch(self):
        with patch.object(ipc_server, 'FRAME_TIMEOUT', .15):
            reader, writer = await self.connect()
            writer.write(b'\x10')
            await writer.drain()
            await asyncio.sleep(.1)
            writer.write(b'\x00')
            await writer.drain()
            await asyncio.wait_for(reader.read(), 1)
            self.server._dispatch.assert_not_called()
