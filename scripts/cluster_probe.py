#!/usr/bin/env python3
"""Exercise the prepared canary through local-only kubectl port-forwards.

Defaults: sink on 19880 and Rust health/metrics on 19881. The sink is the only
publisher; no ntfy or Pushover credential is read onto the workstation.
"""
import argparse
import base64
import hashlib
import json
import time
import urllib.request
import urllib.error


def request(base, path, data=None):
    body = json.dumps(data).encode() if data is not None else None
    with urllib.request.urlopen(urllib.request.Request(base + path, data=body,
                                headers={'Content-Type': 'application/json'}), timeout=20) as response:
        value = response.read()
        return json.loads(value) if response.headers.get_content_type() == 'application/json' else value.decode()


def wait(predicate, timeout=30):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        value = predicate()
        if value:
            return value
        time.sleep(0.1)
    raise AssertionError('Canary check timed out')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--sink', default='http://127.0.0.1:19880')
    parser.add_argument('--forwarder', default='http://127.0.0.1:19881')
    args = parser.parse_args()
    sink, forwarder = args.sink, args.forwarder
    assert request(forwarder, '/healthz')['status'] == 'Healthy'
    assert request(sink, '/state')['posts'] == [], 'Use a fresh sink for this probe'
    ids = []
    def metric(name):
        return sum(int(line.split('}', 1)[1].split()[0])
                   for line in request(forwarder, '/metrics').splitlines()
                   if line.startswith('ntfy_forwarder_' + name + '_total{'))
    dropped_before = metric('messages_dropped_priority')

    def publish(**fields):
        result = request(sink, '/publish', fields)
        ids.append(result['id'])
        return result['id']

    def posts(count):
        return wait(lambda: (p if len(p := request(sink, '/state')['posts']) >= count else None))

    publish(title='rust-canary-low', message='must be filtered', priority=2)
    publish(title='rust-canary-format', message='<hi> `code`', priority=5,
            tags=['md', 'device:canary', 'siren'], click='https://example.com/canary')
    fields = posts(1)[0]['fields']
    assert fields['message'] == '&lt;hi&gt; <code>code</code>', fields
    for name, value in dict(title='rust-canary-format', priority='2', retry='60', expire='3600',
                            html='1', device='canary', sound='siren', url='https://example.com/canary',
                            user='fake', token='fake').items():
        assert fields[name] == value, (name, fields)
    wait(lambda: metric('messages_dropped_priority') == dropped_before + 1)
    print('PASS real ntfy publication -> Rust -> multipart sink; priority, formatting, routing fields and fake credentials', flush=True)

    request(sink, '/responses', [503, 429, 200, 200])
    publish(title='rust-canary-retry-first', message='retry in order', priority=4)
    publish(title='rust-canary-retry-second', message='second in order', priority=4)
    retry = posts(5)[1:]
    assert [p['status'] for p in retry] == [503, 429, 200, 200], retry
    assert [p['fields']['title'] for p in retry] == ['rust-canary-retry-first'] * 3 + ['rust-canary-retry-second'], retry
    print('PASS bounded retries, 429/503 recovery, ordered replay after reconnect', flush=True)

    request(sink, '/responses', [400, 200])
    publish(title='rust-canary-invalid', message='permanent failure', priority=4)
    publish(title='rust-canary-after-invalid', message='must continue', priority=4)
    permanent = posts(7)[5:]
    assert [p['status'] for p in permanent] == [400, 200], permanent
    assert len(permanent) == 2
    print('PASS permanent failure does not block the next notification', flush=True)

    native_upload = False
    expected_sha = hashlib.sha256(base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jG1sAAAAASUVORK5CYII=')).hexdigest()
    count = 7
    try:
        uploaded = request(sink, '/upload', {})
    except urllib.error.HTTPError as error:
        detail = json.load(error)
        if detail.get('status') != 400:
            raise
        print('LIMIT ntfy server disables file uploads (HTTP 400); leaving server configuration unchanged', flush=True)
    else:
        native_upload = True
        ids.append(uploaded['id'])
        count += 1
        image = posts(count)[-1]['fields']['attachment']
        assert image['sha256'] == expected_sha and image['mime'] == 'image/png', image
        print('PASS native ntfy image upload -> authenticated download -> multipart attachment', flush=True)

    publish(title='rust-canary-external-image', message='foreign image', priority=4,
            attach='http://ntfy-rust-canary-sink.monitoring.svc.cluster.local:8080/image.png')
    foreign = posts(count + 1)[-1]['fields']['attachment']
    assert foreign['sha256'] == expected_sha and foreign['mime'] == 'image/png', foreign
    assert all(not image['authorization_present'] for image in request(sink, '/state')['images'])
    print('PASS real ntfy attachment URL -> exact PNG bytes in multipart; token stays on its own origin', flush=True)

    metrics = request(forwarder, '/metrics')
    result = {'native_upload_available': native_upload, 'message_ids': ids, 'sink_posts': request(sink, '/state')['posts'], 'metrics': metrics}
    with open('/tmp/ntfy-rust-cluster-probe.json', 'w') as handle:
        json.dump(result, handle, indent=2)
    print('PASS probe complete; redacted evidence saved to /tmp/ntfy-rust-cluster-probe.json', flush=True)
