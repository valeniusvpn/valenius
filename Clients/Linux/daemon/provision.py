"""Administrator-only initial provisioning through the running daemon.

Installed use: sudo python3 -I /usr/lib/valenius/daemon/provision.py backend vpn.example.com
No configuration, credentials or profiles are written by this helper itself.
"""
from __future__ import annotations

import argparse
import os
from pathlib import Path
import socket
import sys

# -I omits the working directory/PYTHONPATH; import only this installation's modules.
if __name__ == '__main__':
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from shared.messages import CommandType, PipeCommand, PipeResponse
from shared.protocol import read_message_sync, write_message_sync


def main(arguments=None, *, socket_path='/run/valenius.sock') -> int:
    if os.geteuid() != 0:
        print('Provisioning requires root. Ask an administrator to run this command with sudo.', file=sys.stderr)
        return 1
    parser = argparse.ArgumentParser(description='Provision a backend or standalone mode as administrator')
    commands = parser.add_subparsers(dest='mode', required=True)
    commands.add_parser('backend').add_argument('address')
    commands.add_parser('standalone')
    options = parser.parse_args(arguments)
    command = PipeCommand(Command=CommandType.SetBackendUrl, BackendDns=options.address) if options.mode == 'backend' else PipeCommand(Command=CommandType.SetStandaloneMode)
    try:
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
            connection.settimeout(15)
            connection.connect(socket_path)
            write_message_sync(connection, command.to_json())
            response = PipeResponse.from_json(read_message_sync(connection))
        if not response.Success:
            print(response.Error or 'Provisioning refused.', file=sys.stderr)
            return 1
        print(response.Error or 'Provisioning saved.')
        return 0
    except (OSError, ValueError) as error:
        print(f'Provisioning failed: {error}', file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
