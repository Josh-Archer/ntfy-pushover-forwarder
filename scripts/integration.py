#!/usr/bin/env python3
"""Black-box compatibility tests and Linux resource benchmark; stdlib only.

Tests never contact ntfy or Pushover outside the local fake HTTP servers.
"""
import argparse
import email.parser
import email.policy
import json
import os
from pathlib import Path
import statistics
import socket
import subprocess
import tempfile
import threading
import time
import unittest
import urllib.error
import urllib.parse
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


def wait_for(predicate, timeout=10):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        result = predicate()
        if result:
            return result
        time.sleep(0.02)
    raise AssertionError('Timed out waiting for expected state')


class FakeAPI:
    def __init__(self):
        self.condition = threading.Condition()
        self.history = {}
        self.posts = []
        self.subscriptions = []
        self.downloads = []
        self.responses = []
        self.drop_streams = 0
        api = self

        class Handler(BaseHTTPRequestHandler):
            protocol_version = 'HTTP/1.1'

            def setup(self):
                super().setup()
                self.connection.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)

            def log_message(self, *args):
                pass

            def do_GET(self):
                path = urllib.parse.urlsplit(self.path)
                if path.path.endswith('/json'):
                    topic = path.path.split('/')[-2]
                    since = urllib.parse.parse_qs(path.query).get('since', [None])[0]
                    with api.condition:
                        api.subscriptions.append((topic, since, self.headers.get('Authorization')))
                        history = api.history.setdefault(topic, [])
                        index = len(history)
                        if since:
                            index = next((i + 1 for i, m in enumerate(history) if m['id'] == since), 0)
                        generation = api.drop_streams
                    self.send_response(200)
                    self.send_header('Content-Type', 'application/x-ndjson')
                    self.send_header('Connection', 'close')
                    self.end_headers()
                    try:
                        self.wfile.write(b'{"event":"open"}\n')
                        self.wfile.flush()
                        while True:
                            with api.condition:
                                api.condition.wait_for(lambda: index < len(history) or generation != api.drop_streams, timeout=0.25)
                                if generation != api.drop_streams:
                                    break
                                batch = history[index:]
                                index = len(history)
                            for message in batch:
                                self.wfile.write(json.dumps(message).encode() + b'\n')
                            self.wfile.flush()
                    except (BrokenPipeError, ConnectionResetError):
                        pass
                    self.close_connection = True
                    return
                api.downloads.append((self.path, self.headers.get('Authorization')))
                if path.path == '/redirect':
                    self.send_response(302)
                    self.send_header('Location', '/png')
                    self.send_header('Content-Length', '0')
                    self.end_headers()
                    return
                body = b'fake image'
                mime = 'image/png'
                if path.path == '/large':
                    body = b'x' * 2048
                elif path.path == '/text':
                    mime = 'text/plain'
                elif path.path == '/empty':
                    body = b''
                self.send_response(200)
                self.send_header('Content-Type', mime)
                if path.path == '/chunked':
                    self.send_header('Transfer-Encoding', 'chunked')
                    self.end_headers()
                    try:
                        for _ in range(3):
                            self.wfile.write(b'400\r\n' + b'x' * 1024 + b'\r\n')
                        self.wfile.write(b'0\r\n\r\n')
                        self.wfile.flush()
                    except (BrokenPipeError, ConnectionResetError):
                        pass
                else:
                    self.send_header('Content-Length', str(len(body)))
                    self.end_headers()
                    self.wfile.write(body)

            def do_POST(self):
                if self.headers.get('Transfer-Encoding') == 'chunked':
                    chunks = []
                    while True:
                        length = int(self.rfile.readline().strip(), 16)
                        if not length:
                            self.rfile.readline()
                            break
                        chunks.append(self.rfile.read(length))
                        self.rfile.read(2)
                    body = b''.join(chunks)
                else:
                    body = self.rfile.read(int(self.headers['Content-Length']))
                parsed = email.parser.BytesParser(policy=email.policy.default).parsebytes(
                    b'Content-Type: ' + self.headers['Content-Type'].encode() + b'\r\n\r\n' + body)
                fields = {}
                for part in parsed.iter_parts():
                    name = part.get_param('name', header='content-disposition')
                    fields[name] = part.get_payload(decode=True).decode()
                    if part.get_filename():
                        fields['filename'] = part.get_filename()
                        fields['mime'] = part.get_content_type()
                with api.condition:
                    code = api.responses.pop(0) if api.responses else 200
                    api.posts.append((fields, code))
                    api.condition.notify_all()
                data = json.dumps({'status': int(code == 200)}).encode()
                self.send_response(code)
                self.send_header('Content-Type', 'application/json')
                self.send_header('Content-Length', str(len(data)))
                self.end_headers()
                self.wfile.write(data)

        self.server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        self.server.daemon_threads = True
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        self.url = f'http://127.0.0.1:{self.server.server_port}'

    def send(self, topic='alerts', **fields):
        with self.condition:
            history = self.history.setdefault(topic, [])
            message = {'id': f'{topic}-{len(history)}', 'event': 'message', 'message': 'test', **fields}
            history.append(message)
            self.condition.notify_all()
        return message['id']

    def close(self):
        with self.condition:
            self.drop_streams += 1
            self.condition.notify_all()
        self.server.shutdown()
        self.server.server_close()


class Process:
    def __init__(self, api, command, persistent=False, **options):
        self.temp = tempfile.TemporaryDirectory(prefix='ntfy-test-')
        # A bound socket supplies an unused ephemeral health port.
        import socket
        with socket.socket() as sock:
            sock.bind(('127.0.0.1', 0))
            port = sock.getsockname()[1]
        self.url = f'http://127.0.0.1:{port}'
        self.config = dict(NtfyUrl=api.url, NtfyToken='test-ntfy-token', PushoverUrl=api.url + '/pushover',
                           PushoverUserKey='test-user', PushoverDefaultToken='test-default', Topics=['alerts'],
                           SendRecoveryAlerts=False, HttpPort=port, MaxRetries=2, RetryDelayMilliseconds=10,
                           ReconnectInitialDelayMilliseconds=50, **{})
        if persistent:
            self.config['DeduplicationStorePath'] = str(Path(self.temp.name, 'dedupe.json'))
        self.config.update(options)
        Path(self.temp.name, 'appsettings.json').write_text(json.dumps({'Forwarder': self.config}))
        env = {k: v for k, v in os.environ.items() if not k.lower().startswith('forwarder__')}
        env['RUST_LOG'] = 'error'
        env['Logging__LogLevel__Default'] = 'Error'
        self.log = tempfile.TemporaryFile()
        self.process = subprocess.Popen(command, cwd=self.temp.name, env=env, stdout=self.log, stderr=self.log)
        self.api = api
        try:
            wait_for(lambda: self.health() == 'Healthy')
        except Exception:
            self.log.seek(0)
            print(self.log.read().decode())
            self.close()
            raise

    def health(self):
        if self.process.poll() is not None:
            raise AssertionError(f'Forwarder exited: {self.process.returncode}')
        try:
            with urllib.request.urlopen(self.url + '/healthz', timeout=0.5) as response:
                return json.load(response)['status']
        except (OSError, urllib.error.URLError):
            return None

    def get_metrics(self):
        with urllib.request.urlopen(self.url + '/metrics') as response:
            return response.read().decode()

    def resources(self):
        status = Path(f'/proc/{self.process.pid}/status').read_text()
        rss = next(int(line.split()[1]) for line in status.splitlines() if line.startswith('VmRSS:'))
        fields = Path(f'/proc/{self.process.pid}/stat').read_text().split()
        cpu = (int(fields[13]) + int(fields[14])) / os.sysconf('SC_CLK_TCK')
        return rss, cpu

    def close(self):
        self.process.terminate()
        try:
            self.process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait()
        self.log.close()
        self.temp.cleanup()


COMMAND = []


class Compatibility(unittest.TestCase):
    def setUp(self):
        self.api = FakeAPI()
        self.worker = None

    def tearDown(self):
        if self.worker:
            self.worker.close()
        self.api.close()

    def start(self, **options):
        self.worker = Process(self.api, COMMAND, **options)

    def posts(self, count):
        wait_for(lambda: len(self.api.posts) >= count)
        return [fields for fields, status in self.api.posts]

    def metric(self, name, topic='alerts'):
        prefix = f'ntfy_forwarder_{name}{{topic="{topic}",destination="pushover"}} '
        return next(float(line[len(prefix):]) for line in self.worker.get_metrics().splitlines()
                    if line.startswith(prefix))

    def test_delivery_visibility_tracks_pending_retries_and_success(self):
        self.start(MaxRetries=1, ReconnectInitialDelayMilliseconds=1500)
        self.assertEqual(self.metric('last_successful_delivery_timestamp_seconds'), 0)
        self.api.responses = [500, 200]
        self.api.send(id='pending')
        wait_for(lambda: self.metric('transient_delivery_failures_total') == 1)
        self.assertEqual(self.metric('pending_messages'), 1)
        first_age = self.metric('oldest_pending_age_seconds')
        time.sleep(0.1)
        self.assertGreater(self.metric('oldest_pending_age_seconds'), first_age)
        self.assertEqual(self.metric('last_successful_delivery_timestamp_seconds'), 0)
        self.posts(2)
        wait_for(lambda: self.metric('last_successful_delivery_timestamp_seconds') > 0)
        self.assertEqual(self.metric('pending_messages'), 0)
        self.assertEqual(self.metric('oldest_pending_age_seconds'), 0)
        self.assertEqual(self.metric('delivery_attempts_total'), 2)
        self.assertEqual(self.metric('permanent_delivery_failures_total'), 0)

    def test_skips_and_rejections_do_not_mark_delivery_success(self):
        self.start(MinimumPriority=3)
        self.api.send(id='filtered', priority=1)
        wait_for(lambda: 'ntfy_forwarder_messages_dropped_priority_total{topic="alerts"} 1' in self.worker.get_metrics())
        self.assertEqual(self.metric('delivery_attempts_total'), 0)
        self.assertEqual(self.metric('last_successful_delivery_timestamp_seconds'), 0)
        self.api.responses = [400, 200]
        self.api.send(id='rejected')
        wait_for(lambda: self.metric('permanent_delivery_failures_total') == 1)
        self.assertEqual(self.metric('pending_messages'), 0)
        self.assertEqual(self.metric('last_successful_delivery_timestamp_seconds'), 0)
        self.api.send(id='accepted')
        wait_for(lambda: self.metric('last_successful_delivery_timestamp_seconds') > 0)
        timestamp = self.metric('last_successful_delivery_timestamp_seconds')
        time.sleep(1.1)
        self.api.send(id='accepted')
        wait_for(lambda: 'ntfy_forwarder_messages_dropped_dedupe_total{topic="alerts"} 1' in self.worker.get_metrics())
        self.assertEqual(self.metric('last_successful_delivery_timestamp_seconds'), timestamp)
        self.assertEqual(self.metric('delivery_attempts_total'), 2)
        self.assertEqual(self.metric('pending_messages'), 0)

    def test_delivery_visibility_resets_after_restart(self):
        with tempfile.TemporaryDirectory(prefix='ntfy-metrics-') as directory:
            path = str(Path(directory, 'dedupe.json'))
            self.start(DeduplicationStorePath=path)
            self.api.send(id='delivered')
            wait_for(lambda: self.metric('last_successful_delivery_timestamp_seconds') > 0)
            wait_for(lambda: Path(directory, 'cursors.json').exists())
            self.worker.close()
            self.worker = None
            self.start(DeduplicationStorePath=path)
            for metric in ['last_successful_delivery_timestamp_seconds', 'pending_messages',
                           'oldest_pending_age_seconds', 'delivery_attempts_total',
                           'permanent_delivery_failures_total', 'transient_delivery_failures_total']:
                self.assertEqual(self.metric(metric), 0)

    def test_routing_formatting_priority_and_dedupe(self):
        self.start(TopicTokens={'alerts': 'specific'}, SoundMap={'siren': 'siren'}, MinimumPriority=3)
        self.api.send(priority=2)
        self.api.send(id='unique', title='title', message='<hi> `code`', priority=5,
                      tags=['md', 'device:phone', 'siren'], click='https://example.com')
        self.api.send(id='unique', message='duplicate')
        self.api.send(id='different', message='test')
        posts = self.posts(2)
        self.assertEqual(posts[0]['token'], 'specific')
        self.assertEqual(posts[0]['message'], '&lt;hi&gt; <code>code</code>')
        for key, value in dict(priority='2', retry='60', expire='3600', html='1', device='phone', sound='siren', url='https://example.com').items():
            self.assertEqual(posts[0][key], value)
        wait_for(lambda: 'ntfy_forwarder_messages_dropped_dedupe_total{topic="alerts"} 1' in self.worker.get_metrics())
        self.assertEqual(len(self.api.posts), 2)

    def test_actions_forward_as_pushover_url_and_url_title(self):
        self.start()
        # 1. Action with url and label maps to url and url_title
        self.api.send(id='action-1', message='msg1',
                      actions=[{'action': 'view', 'label': 'Open Site', 'url': 'https://example.com/site'}])
        # 2. Action with url and no label maps to url without url_title
        self.api.send(id='action-2', message='msg2',
                      actions=[{'action': 'view', 'url': 'https://example.com/nolabel'}])
        # 3. Action overrides click
        self.api.send(id='action-3', message='msg3', click='https://example.com/click',
                      actions=[{'action': 'view', 'label': 'Action Preferred', 'url': 'https://example.com/action'}])
        # 4. Action without url falls back to click
        self.api.send(id='action-4', message='msg4', click='https://example.com/fallback',
                      actions=[{'action': 'broadcast', 'label': 'Broadcast Only'}])
        # 5. Overlong url (620 chars) and overlong label (150 chars) truncated to 512 and 100
        long_url = 'https://example.com/' + 'u' * 600
        long_title = 'Button ' + 't' * 143
        self.api.send(id='action-5', message='msg5',
                      actions=[{'action': 'view', 'label': long_title, 'url': long_url}])
        # 6. Broadcast action carrying a url appears before a view action: view action wins
        self.api.send(id='action-6', message='msg6',
                      actions=[{'action': 'broadcast', 'label': 'Broadcast With URL', 'url': 'https://example.com/broadcast'},
                               {'action': 'view', 'label': 'View Wins', 'url': 'https://example.com/view-wins'}])
        # 7. Http action maps to url and url_title
        self.api.send(id='action-7', message='msg7',
                      actions=[{'action': 'http', 'label': 'Webhook', 'url': 'https://example.com/webhook'}])
        # 8. Broadcast action carrying a url falls back to click
        self.api.send(id='action-8', message='msg8', click='https://example.com/click-fallback',
                      actions=[{'action': 'broadcast', 'label': 'Broadcast With URL', 'url': 'https://example.com/broadcast-ignored'}])
        # 9. Overlong click url (600 chars) truncated to 512
        click_long = 'https://example.com/click/' + 'c' * 600
        self.api.send(id='action-9', message='msg9', click=click_long)

        posts = self.posts(9)
        self.assertEqual(posts[0]['url'], 'https://example.com/site')
        self.assertEqual(posts[0]['url_title'], 'Open Site')
        self.assertEqual(posts[1]['url'], 'https://example.com/nolabel')
        self.assertNotIn('url_title', posts[1])
        self.assertEqual(posts[2]['url'], 'https://example.com/action')
        self.assertEqual(posts[2]['url_title'], 'Action Preferred')
        self.assertEqual(posts[3]['url'], 'https://example.com/fallback')
        self.assertNotIn('url_title', posts[3])
        self.assertEqual(posts[4]['url'], long_url[:512])
        self.assertEqual(len(posts[4]['url']), 512)
        self.assertEqual(posts[4]['url_title'], long_title[:100])
        self.assertEqual(len(posts[4]['url_title']), 100)
        self.assertEqual(posts[5]['url'], 'https://example.com/view-wins')
        self.assertEqual(posts[5]['url_title'], 'View Wins')
        self.assertEqual(posts[6]['url'], 'https://example.com/webhook')
        self.assertEqual(posts[6]['url_title'], 'Webhook')
        self.assertEqual(posts[7]['url'], 'https://example.com/click-fallback')
        self.assertNotIn('url_title', posts[7])
        self.assertEqual(posts[8]['url'], click_long[:512])
        self.assertEqual(len(posts[8]['url']), 512)
        self.assertNotIn('url_title', posts[8])

    def test_transient_failure_retries_first_message_then_replays_in_order(self):
        self.start()
        self.api.responses = [500, 429, 200, 200]
        self.api.send(id='first', message='first')
        self.api.send(id='second', message='second')
        posts = self.posts(4)
        self.assertEqual([p['message'] for p in posts], ['first', 'first', 'first', 'second'])
        self.assertTrue(any(since == 'first' for _, since, _ in self.api.subscriptions))

    def test_durable_pending_recovers_after_kill_and_history_expiry(self):
        with tempfile.TemporaryDirectory(prefix='ntfy-queue-') as directory:
            path = str(Path(directory, 'queue.json'))
            self.start(QueueStorePath=path, MaxRetries=1, ReconnectInitialDelayMilliseconds=5000)
            self.api.responses = [500]
            self.api.send(id='expired', message='durable payload')
            self.posts(1)
            wait_for(lambda: Path(path).exists())
            self.worker.process.kill()
            self.worker.process.wait()
            self.worker.close()
            self.worker = None
            with self.api.condition:
                self.api.history['alerts'].clear()
            self.start(QueueStorePath=path)
            self.assertEqual(self.posts(2)[1]['message'], 'durable payload')
            wait_for(lambda: not json.loads(Path(path).read_text())['pending'])

    def test_permanent_failure_and_missing_id_do_not_block_following_messages(self):
        self.start()
        self.api.responses = [400, 200]
        self.api.send(id='', message='invalid')
        self.api.send(message='bad')
        self.api.send(message='good')
        self.assertEqual([p['message'] for p in self.posts(2)], ['bad', 'good'])
        self.assertEqual(len(self.api.posts), 2)

    def test_attachment_limits_content_types_auth_and_redirects(self):
        self.start(MaxAttachmentBytes=1024)
        for path in ['/large', '/chunked', '/text', '/empty', '/redirect', '/png']:
            self.api.send(attachment={'url': path})
        posts = self.posts(6)
        self.assertTrue(all('attachment' not in p for p in posts[:5]))
        self.assertEqual(posts[5]['attachment'], 'fake image')
        self.assertEqual(posts[5]['filename'], 'attachment.png')
        self.assertTrue(all(token == 'Bearer test-ntfy-token' for _, token in self.api.downloads))
        self.assertEqual(len(self.api.downloads), 6)

    def test_foreign_origin_image_never_receives_ntfy_token(self):
        foreign = FakeAPI()
        try:
            self.start()
            self.api.send(attachment={'url': foreign.url + '/png'})
            self.posts(1)
            self.assertEqual(foreign.downloads, [('/png', None)])
        finally:
            foreign.close()

    def test_logos_cache_and_do_not_receive_auth(self):
        self.start(LogoMap={'mag': self.api.url + '/png', 'tv': self.api.url + '/png'})
        self.api.send(tags=['tv'])
        self.api.send(tags=['tv'])
        self.posts(2)
        self.assertEqual(self.api.downloads, [('/png', None)])

    def test_persistence_suppresses_replayed_id_after_restart(self):
        with tempfile.TemporaryDirectory(prefix='ntfy-dedupe-') as directory:
            path = str(Path(directory, 'dedupe.json'))
            self.start(DeduplicationStorePath=path)
            self.api.send(id='persisted')
            self.posts(1)
            wait_for(lambda: Path(path).exists())
            self.worker.close()
            self.worker = None
            self.start(DeduplicationStorePath=path)
            self.api.send(id='persisted')
            self.api.send(id='new')
            self.posts(2)
            self.assertEqual(len(self.api.posts), 2)

    def test_concurrent_topics_share_duplicate_reservation(self):
        self.start(Topics=['alerts', 'other'])
        self.api.send('alerts', id='shared')
        self.api.send('other', id='shared')
        self.posts(1)
        wait_for(lambda: sum(int(line.split("}", 1)[1].split()[0]) for line in self.worker.get_metrics().splitlines()
                            if line.startswith('ntfy_forwarder_messages_dropped_dedupe_total{')) >= 1)
        self.assertEqual(len(self.api.posts), 1)

    def test_recovery_after_clean_eof_uses_cursor_and_sends_alert(self):
        self.start(SendRecoveryAlerts=True)
        self.api.send(id='checkpoint')
        self.posts(1)
        with self.api.condition:
            self.api.drop_streams += 1
            self.api.condition.notify_all()
        posts = self.posts(2)
        self.assertEqual(posts[1]['title'], 'ntfy recovered: alerts')
        self.assertTrue(any(since == 'checkpoint' for _, since, _ in self.api.subscriptions))
        self.assertTrue(all(auth == 'Bearer test-ntfy-token' for _, _, auth in self.api.subscriptions))


def benchmark(command, messages, idle_seconds, persistent):
    api = FakeAPI()
    worker = Process(api, command, persistent=persistent, Topics=['alerts', 'media', 'backup', 'security'])
    try:
        start_rss, start_cpu = worker.resources()
        time.sleep(idle_seconds)
        idle_rss, idle_cpu = worker.resources()
        started = time.monotonic()
        for i in range(messages):
            api.send(['alerts', 'media', 'backup', 'security'][i % 4], id=f'message-{i}')
        wait_for(lambda: len(api.posts) >= messages, timeout=120)
        # Observe the forwarder completing the last response and metrics update.
        wait_for(lambda: sum(int(line.split("}", 1)[1].split()[0]) for line in worker.get_metrics().splitlines()
                            if line.startswith('ntfy_forwarder_messages_forwarded_total{')) == messages)
        elapsed = time.monotonic() - started
        load_rss, load_cpu = worker.resources()
        return dict(start_rss_kib=start_rss, idle_rss_kib=idle_rss,
                    idle_cpu_seconds=round(idle_cpu - start_cpu, 3), idle_seconds=idle_seconds,
                    load_rss_kib=load_rss, load_cpu_seconds=round(load_cpu - idle_cpu, 3),
                    load_wall_seconds=round(elapsed, 3), messages=messages, persistent=persistent)
    finally:
        worker.close()
        api.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--benchmark', action='store_true')
    parser.add_argument('--persistent', action='store_true')
    parser.add_argument('--messages', type=int, default=1000)
    parser.add_argument('--idle-seconds', type=float, default=3)
    parser.add_argument('--runs', type=int, default=3)
    parser.add_argument('command', nargs=argparse.REMAINDER)
    args = parser.parse_args()
    COMMAND = args.command
    if COMMAND and COMMAND[0] == '--':
        COMMAND = COMMAND[1:]
    if not COMMAND:
        parser.error('Specify an absolute executable command after --')
    if args.benchmark:
        results = [benchmark(COMMAND, args.messages, args.idle_seconds, args.persistent) for _ in range(args.runs)]
        print(json.dumps({'runs': results, 'median': {key: statistics.median(r[key] for r in results)
                                                     for key in results[0]}}, indent=2))
    else:
        unittest.main(argv=['integration.py'], verbosity=2)
