#!/usr/bin/env python3
"""Temporary in-cluster ntfy publisher and Pushover-shaped test sink.

Credentials stay in the pod environment. Recorded multipart fields redact any
accidentally supplied real Pushover credentials. The sink never calls Pushover.
"""
import base64
import email.parser
import email.policy
import hashlib
import json
import os
import threading
import urllib.error
import urllib.parse
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

NTFY = os.environ['NTFY_URL'].rstrip('/')
TOPIC = os.environ['TEST_TOPIC']
TOKEN = os.environ.get('NTFY_TOKEN', '')
PNG = base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jG1sAAAAASUVORK5CYII=')
state = {'posts': [], 'responses': [], 'images': [], 'replays': 0}
lock = threading.Lock()


def ntfy_request(path, data=None, headers=None, method=None):
    headers = headers or {}
    if TOKEN:
        headers['Authorization'] = 'Bearer ' + TOKEN
    return urllib.request.urlopen(urllib.request.Request(NTFY + path, data=data, headers=headers, method=method), timeout=15)


class Handler(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'

    def log_message(self, *args):
        pass

    def respond(self, data, code=200, content_type='application/json'):
        if isinstance(data, (dict, list)):
            data = json.dumps(data).encode()
        self.send_response(code)
        self.send_header('Content-Type', content_type)
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def read_body(self):
        if self.headers.get('Transfer-Encoding') == 'chunked':
            data = bytearray()
            while True:
                length = int(self.rfile.readline().split(b';')[0], 16)
                if length == 0:
                    self.rfile.readline()
                    return bytes(data)
                data.extend(self.rfile.read(length))
                self.rfile.read(2)
                if len(data) > 4 * 1024 * 1024:
                    raise ValueError('test request too large')
        return self.rfile.read(min(int(self.headers.get('Content-Length', 0)), 4 * 1024 * 1024))

    def do_GET(self):
        if self.path in ('/healthz', '/state'):
            with lock:
                self.respond(state if self.path == '/state' else {'status': 'healthy'})
        elif self.path == '/image.png':
            with lock:
                state['images'].append({'authorization_present': bool(self.headers.get('Authorization'))})
            self.respond(PNG, content_type='image/png')
        elif self.path == f'/replay/{TOPIC}/json':
            # Replay actual cached ntfy events as a deterministic fixture for testing
            # durable dedupe after a pod restart. No production topic is read.
            try:
                with ntfy_request(f'/{TOPIC}/json?poll=1&since=all') as response:
                    events = [json.loads(line) for line in response.read(1024 * 1024).splitlines() if line.strip()]
                    data = b''.join(json.dumps(event).encode() + b'\n' for event in events if event.get('title') == 'rust-canary-persisted')
                with lock:
                    state['replays'] += 1
                self.respond(data, content_type='application/x-ndjson')
            except urllib.error.HTTPError as error:
                self.respond({'error': 'ntfy replay failed', 'status': error.code}, 502)
        else:
            self.respond({'error': 'not found'}, 404)

    def do_POST(self):
        try:
            data = self.read_body()
            if self.path == '/notify':
                parsed = email.parser.BytesParser(policy=email.policy.default).parsebytes(
                    b'Content-Type: ' + self.headers['Content-Type'].encode() + b'\r\n\r\n' + data)
                fields = {}
                for part in parsed.iter_parts():
                    name = part.get_param('name', header='content-disposition')
                    payload = part.get_payload(decode=True)
                    if part.get_filename():
                        fields[name] = {'bytes': len(payload), 'sha256': hashlib.sha256(payload).hexdigest(),
                                        'filename': part.get_filename(), 'mime': part.get_content_type()}
                    elif name in ('token', 'user'):
                        fields[name] = 'fake' if payload.decode().startswith('canary-fake-') else 'REDACTED'
                    else:
                        fields[name] = payload.decode()
                with lock:
                    code = state['responses'].pop(0) if state['responses'] else 200
                    state['posts'].append({'fields': fields, 'status': code})
                self.respond({'status': int(code == 200)}, code)
            elif self.path == '/responses':
                statuses = json.loads(data)
                assert all(isinstance(n, int) and 200 <= n < 600 for n in statuses)
                with lock:
                    state['responses'] = statuses
                self.respond({'ok': True})
            elif self.path == '/publish':
                fields = json.loads(data)
                fields['topic'] = TOPIC
                with ntfy_request('/', data=json.dumps(fields).encode(), headers={'Content-Type': 'application/json'}) as response:
                    result = json.load(response)
                self.respond({k: result[k] for k in ('id', 'event', 'topic', 'time') if k in result})
            elif self.path == '/upload':
                with ntfy_request('/' + TOPIC, data=PNG, headers={'Content-Type': 'image/png', 'Filename': 'canary.png',
                                  'Title': 'rust-canary-upload', 'Priority': '4'}, method='PUT') as response:
                    result = json.load(response)
                self.respond({k: result[k] for k in ('id', 'event', 'topic', 'time') if k in result})
            else:
                self.respond({'error': 'not found'}, 404)
        except urllib.error.HTTPError as error:
            self.respond({'error': 'ntfy request failed', 'status': error.code}, 502)
        except Exception:
            self.respond({'error': 'test request failed'}, 500)


ThreadingHTTPServer(('0.0.0.0', 8080), Handler).serve_forever()
