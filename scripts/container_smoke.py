#!/usr/bin/env python3
"""Validate the built Linux container against local fake services."""
import json
import socket
import subprocess
import sys
import tempfile
import urllib.request
import uuid
from pathlib import Path
from integration import FakeAPI, wait_for

image = sys.argv[1] if len(sys.argv) > 1 else 'ntfy-forwarder:test'
api = FakeAPI()
name = f'ntfy-smoke-{uuid.uuid4().hex[:12]}'
with socket.socket() as sock:
    sock.bind(('127.0.0.1', 0))
    port = sock.getsockname()[1]
try:
    with tempfile.TemporaryDirectory(prefix='ntfy-container-') as directory:
        config = Path(directory, 'appsettings.json')
        config.write_text(json.dumps({'Forwarder': dict(
            NtfyUrl=api.url, Topics=['alerts'], PushoverUrl=api.url + '/pushover',
            PushoverUserKey='fake-user', PushoverDefaultToken='fake-token', HttpPort=port,
            DeduplicationStorePath='/data/dedupe.json', QueueEnabled=True,
            QueueStorePath='/data/queue.json', SendRecoveryAlerts=False)}))
        subprocess.run(['docker', 'run', '-d', '--name', name, '--network', 'host',
                        '--read-only', '--cap-drop=ALL', '--tmpfs', '/data:uid=1654,gid=1654,mode=0700',
                        '--mount', f'type=bind,src={config},dst=/app/appsettings.json,readonly', image],
                       check=True, stdout=subprocess.DEVNULL)
        try:
            def healthy():
                try:
                    with urllib.request.urlopen(f'http://127.0.0.1:{port}/healthz', timeout=0.5) as response:
                        return json.load(response)['status'] == 'Healthy'
                except OSError:
                    return False

            wait_for(healthy)
            api.send(id='container-smoke')
            wait_for(lambda: api.posts)
            wait_for(lambda: subprocess.run(['docker', 'exec', name, 'test', '-s', '/data/dedupe.json']).returncode == 0)
            def checkpointed():
                try:
                    snapshot = json.loads(subprocess.check_output(['docker', 'exec', name, 'cat', '/data/queue.json']))
                    cursors = json.loads(subprocess.check_output(['docker', 'exec', name, 'cat', '/data/cursors.json']))
                    return (snapshot['completed'].get('alerts') == 'container-smoke'
                            and cursors.get('alerts') == 'container-smoke' and not snapshot['entries'])
                except (subprocess.CalledProcessError, KeyError, json.JSONDecodeError):
                    return False

            wait_for(checkpointed)
            info = json.loads(subprocess.check_output(['docker', 'inspect', name]))[0]
            assert info['Config']['User'] == '1654:1654'
            subprocess.run(['docker', 'kill', '--signal=TERM', name], check=True, stdout=subprocess.DEVNULL)
            assert subprocess.check_output(['docker', 'wait', name], text=True).strip() == '0'
            print('Container passed forwarding, health, persistence, non-root identity and SIGTERM checks')
        finally:
            subprocess.run(['docker', 'rm', '-f', name], stdout=subprocess.DEVNULL)
finally:
    api.close()
