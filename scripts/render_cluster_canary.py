#!/usr/bin/env python3
import sys,yaml
items=list(yaml.safe_load_all(sys.stdin))
for resource in items:
    if resource and resource['kind']=='Deployment':
        assert resource['metadata']['name']=='ntfy-rust-canary'
        spec=resource['spec']['template']['spec']
        spec['automountServiceAccountToken']=False
        container=spec['containers'][0]
        env=[e for e in container['env'] if not any(word in e['name'] for word in ('Pushover','TopicTokens'))]
        env.extend({'name':key,'value':value} for key,value in {
            'Forwarder__PushoverUrl':'http://ntfy-rust-canary-sink.monitoring.svc.cluster.local:8080/notify',
            'Forwarder__PushoverUserKey':'canary-fake-user',
            'Forwarder__PushoverDefaultToken':'canary-fake-app',
            'Forwarder__MaxRetries':'2',
            'Forwarder__RetryDelayMilliseconds':'50',
            'Forwarder__ReconnectInitialDelayMilliseconds':'200',
        }.items())
        container['env']=env
        assert not any(e.get('valueFrom',{}).get('secretKeyRef',{}).get('key','').startswith('pushover') for e in env)
        container['securityContext']['readOnlyRootFilesystem']=True
print(yaml.safe_dump_all(items))
