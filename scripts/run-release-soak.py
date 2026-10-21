#!/usr/bin/env python3
"""Continuous real MCP soak; isolated data, frozen recall oracle, no production changes.

Requires Python 3.11+ and psutil. Duration excludes seeding and final restart checks.
Any failure fails qualification and preserves data and diagnostics. Explicit diagnostic
mode can continue after quality failures; scope and reliability failures always stop.
Never resumes or silently restarts a failed run. A new output directory is mandatory.
"""
import argparse
from collections import defaultdict
from concurrent.futures import ThreadPoolExecutor
import ctypes
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import threading
import time
import traceback
import psutil
from validation.load_client import LoadClient

FROZEN = '11D86B86DF1629FC9931DE7DC846E4A7B317A9C31F15FB6363D73323CC4F6553'
CANARIES = ('PRIVATE_SOAK_CANARY', 'FOREIGN_SOAK_CANARY', 'LEGACY_SOAK_CANARY')


class RecallQualityFailure(AssertionError): pass
class ScopeViolation(AssertionError): pass


def now(): return dt.datetime.now(dt.timezone.utc).isoformat()


def write_json(path, value):
    temporary = path.with_suffix('.tmp')
    temporary.write_text(json.dumps(value, indent=2), encoding='utf-8')
    # Windows readers may briefly deny deletion/replacement of an open file.
    # Preserve atomic snapshots, but bound the retry so a permanent error fails.
    for attempt in range(20):
        try:
            temporary.replace(path)
            return
        except PermissionError:
            if attempt == 19: raise
            time.sleep(.05)


def check_recall(corpus, question, mode, result):
    assert isinstance(result, dict) and isinstance(result.get('results'), list), result
    if any(marker in json.dumps(result) for marker in CANARIES): raise ScopeViolation(result)
    entries = {e['id']: e for e in corpus['entries']}
    rows = result['results']
    ids = [r['id'] for r in rows]
    assert len(ids) <= 5 and len(ids) == len(set(ids)), result
    eligible = lambda i: mode != 'direct' or entries[i]['ns'] == question['ns']
    expected = [i for i in question['expected'] if eligible(i)]
    unknown = []
    for row in rows:
        entry = entries.get(row['id'])
        if entry is None:
            if mode == 'direct': raise ScopeViolation(result)
            unknown.append(row['id'])
            continue
        if row['text'] != entry['text'] or not eligible(row['id']): raise ScopeViolation(result)
        if entry['project'] != question['project']: raise ScopeViolation(result)
        if question['environment'] and entry['environment'] != question['environment']: raise ScopeViolation(result)
    if unknown: raise RecallQualityFailure(('unrelated evidence', question['id'], mode, result))
    if expected and not any(i in ids for i in expected): raise RecallQualityFailure(('answer missed', question['id'], mode, result))
    if not expected and ids: raise RecallQualityFailure(('non-answer evidence', question['id'], mode, result))
    if question['topOne'] and (not ids or ids[0] not in expected): raise RecallQualityFailure(('wrong top one', question['id'], mode, result))
    for facet in question['facets'].values():
        required = [i for i in facet if eligible(i)]
        if required and not any(i in ids for i in required): raise RecallQualityFailure(('incomplete facets', question['id'], mode, result))
    assert not any(c.get('resolution') or c.get('status') == 'resolved' for c in result.get('conflicts') or []), result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--server', required=True, type=Path)
    parser.add_argument('--fixture', required=True, type=Path)
    parser.add_argument('--revision', required=True)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--hours', type=float, default=48)
    parser.add_argument('--interval', type=float, default=5)
    parser.add_argument('--json-size', type=int, default=1000)
    parser.add_argument('--sqlite-size', type=int, default=5000)
    parser.add_argument('--dump-tool', type=Path)
    parser.add_argument('--collect-after-quality-failure', action='store_true',
                        help='Continue diagnostic load after recorded quality failures; the final release verdict still fails. Scope failures always stop.')
    args = parser.parse_args()
    assert args.hours > 0 and args.interval > 0
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    fixture = args.fixture.read_bytes()
    assert hashlib.sha256(fixture).hexdigest().upper() == FROZEN
    corpus = json.loads(fixture)
    # Snapshot the executable and fixture: a later checkout/build must not change a live run.
    published = output / 'server'
    shutil.copytree(args.server.resolve().parent, published)
    server = published / args.server.name
    (output / 'fixture.json').write_bytes(fixture)
    manifest = {str(p.relative_to(published)): hashlib.sha256(p.read_bytes()).hexdigest()
                for p in published.rglob('*') if p.is_file()}
    write_json(output / 'binary-manifest.json', manifest)
    harness_manifest = {}
    for relative in ('run-release-soak.py', 'validation/load_client.py'):
        source = Path(__file__).resolve().parent / relative
        destination = output / 'harness' / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, destination)
        harness_manifest[relative] = hashlib.sha256(source.read_bytes()).hexdigest()
    summary = {'schemaVersion': 1, 'state': 'seeding', 'createdUtc': now(), 'revision': args.revision,
               'harnessFilesSha256': harness_manifest,
               'fixtureSha256': FROZEN, 'durationHours': args.hours, 'pid': os.getpid(),
               'platform': os.name, 'intervalSeconds': args.interval, 'backends': {},
               'continueAfterQualityFailure': args.collect_after_quality_failure, 'qualityFailures': 0,
               'qualification': '48-hour continuous soak' if args.hours >= 48 else 'short harness validation'}
    write_json(output / 'status.json', summary)
    clients, baselines, versions = {}, {}, defaultdict(int)
    slow_windows = defaultdict(int)
    quality_lock = threading.Lock()
    latencies, failures = defaultdict(list), []
    executor = ThreadPoolExecutor(max_workers=8)
    if os.name == 'nt': ctypes.windll.kernel32.SetThreadExecutionState(0x80000001)

    def start(backend, label, agent='soak-owner', tenant='soak-a'):
        return LoadClient(['dotnet', str(server)], {'MEMORY_STORAGE': backend,
            'MEMORY_DATA_PATH': str(output / ('data-' + backend)), 'MEMORY_TOOL_PROFILE': 'minimal',
            'MEMORY_TENANT_ID': tenant, 'AGENT_ID': agent, 'Logging__LogLevel__Default': 'Warning'}, output, backend + '-' + label)

    def read(backend, question, mode):
        begin = time.monotonic()
        params = {'query': question['query']}
        if mode == 'direct': params['ns'] = question['ns']
        result = clients[backend].call('recall', **params)
        try:
            check_recall(corpus, question, mode, result)
        except RecallQualityFailure as error:
            with quality_lock:
                summary['qualityFailures'] += 1
                with (output / 'quality-failures.jsonl').open('a', encoding='utf-8') as log:
                    log.write(json.dumps({'utc': now(), 'backend': backend, 'question': question,
                        'mode': mode, 'result': result, 'reason': str(error.args[0][0])}) + '\n')
            if not args.collect_after_quality_failure: raise
        return backend, begin, time.monotonic(), result['strategy']

    def scope(client):
        for marker in CANARIES:
            result = client.call('recall', query='Who owns Citrine production deployment?', ns='soak-private' if marker == CANARIES[0] else 'missing-namespace')
            assert not result['results'] and marker not in json.dumps(result), result
        denied = client.call('store_memory', id='forbidden', ns='soak-private', text='Forbidden mutation.')
        assert isinstance(denied, str) and denied.startswith('Error:'), denied
        threshold = client.call('recall', query='Who owns Citrine production deployment?', ns='expert_citrine_ops', minScore=1)
        assert not threshold['results'], threshold

    def write(backend):
        begin = time.monotonic()
        client = clients[backend]
        versions[backend] += 1
        version = versions[backend]
        value = f'Writer committed catalogue revision {version}.'
        result = client.call('store_memory', id='soak-version', ns='soak-writes', text=value,
                             lifecycleState='ltm', metadata={'version': version})
        assert isinstance(result, str) and result.startswith('Stored entry'), result
        assert client.call('get_memory', id='soak-version')['text'] == value
        result = client.call('store_memory', id='soak-ephemeral', ns='soak-writes', text='Temporary catalogue marker.', lifecycleState='stm')
        assert isinstance(result, str) and result.startswith('Stored entry'), result
        result = client.call('delete_memory', id='soak-ephemeral')
        assert isinstance(result, str) and 'Deleted' in result, result
        return backend, begin, time.monotonic()

    def full_check(backend):
        for question in corpus['queries']:
            for mode in ('direct', 'unscoped'): read(backend, question, mode)
        scope(clients[backend])

    try:
        for backend, size in (('json', args.json_size), ('sqlite', args.sqlite_size)):
            assert size >= len(corpus['entries'])
            for label, marker, agent, tenant, namespace in (
                ('private', CANARIES[0], 'other-owner', 'soak-a', 'soak-private'),
                ('foreign', CANARIES[1], 'soak-owner', 'soak-b', 'expert_citrine_ops'),
                ('legacy', CANARIES[2], 'soak-owner', '', 'expert_citrine_ops')):
                with start(backend, 'seed-' + label, agent, tenant) as client:
                    result = client.call('remember', id='c-owner', ns=namespace,
                        text=f'Citrine production deployment owner is {marker}.', lifecycleState='archived')
                    assert result['status'] == 'stored', result
            client = clients[backend] = start(backend, 'continuous')
            assert len(client.request('tools/list')['tools']) == 17
            grouped = defaultdict(list)
            for entry in corpus['entries']:
                grouped[entry['ns']].append({'id': entry['id'], 'text': entry['text'], 'lifecycleState': entry['state']})
            for i in range(size - len(corpus['entries'])):
                grouped['background-' + str(i % 5)].append({'id': f'background-{i}',
                    'text': f'Library catalogue volume {i}: coastal birds, tidal plants and navigation charts.', 'lifecycleState': 'ltm'})
            for namespace, entries in grouped.items():
                for i in range(0, len(entries), 50):
                    result = client.call('store_batch', ns=namespace, entries=entries[i:i+50], checkDuplicates=False)
                    assert result['status'] == 'stored' and result['entriesStored'] == len(entries[i:i+50]), result
            full_check(backend)
            summary['backends'][backend] = {'pid': client.process.pid, 'baseMemories': size, 'requests': client.count,
                'recallObservations': 0, 'overlappingReads': 0}
            write_json(output / 'status.json', summary)
        start_time, last_tick = time.monotonic(), time.monotonic()
        started = dt.datetime.now(dt.timezone.utc)
        summary.update(state='running_with_blockers' if summary['qualityFailures'] else 'running', startedUtc=started.isoformat(),
            dueUtc=(started + dt.timedelta(hours=args.hours)).isoformat())
        write_json(output / 'status.json', summary)
        round_number, last_write, last_sample = 0, -30., -60.
        while time.monotonic() - start_time < args.hours * 3600:
            tick = time.monotonic()
            assert tick - last_tick < 90, 'Host paused or workload stalled for 90s; continuous qualification invalid'
            last_tick = tick
            elapsed = tick - start_time
            question = corpus['queries'][round_number % len(corpus['queries'])]
            futures = [executor.submit(read, backend, question, mode) for backend in clients for mode in ('direct', 'unscoped')]
            writers = []
            if elapsed - last_write >= 30:
                writers = [executor.submit(write, backend) for backend in clients]
                last_write = elapsed
            reads = [future.result(timeout=40) for future in futures]
            writes = [future.result(timeout=120) for future in writers]
            for backend, begin, end, strategy in reads:
                latencies[backend].append(end - begin)
                summary['backends'][backend]['lastStrategy'] = strategy
                summary['backends'][backend]['recallObservations'] += 1
                summary['backends'][backend]['overlappingReads'] += int(any(
                    backend == wb and begin < we and ws < end for wb, ws, we in writes))
            if elapsed - last_sample >= 60:
                for backend, client in clients.items():
                    scope(client)
                    process = psutil.Process(client.process.pid)
                    memory = process.memory_info()
                    resources = {'rssBytes': memory.rss, 'threads': process.num_threads(),
                                 'handles': process.num_handles() if os.name == 'nt' else process.num_fds()}
                    assert resources['rssBytes'] < 2 * 1024**3, (backend, resources)
                    if elapsed >= 600:
                        baselines.setdefault(backend, resources)
                        assert resources['rssBytes'] - baselines[backend]['rssBytes'] < 512 * 1024**2, (backend, resources)
                        assert resources['handles'] - baselines[backend]['handles'] < 512, (backend, resources)
                    health = client.call('engram_status')
                    intervals = {'decay': 900, 'consolidation': 86400, 'auto_link': 21600, 'accretion': 1800}
                    for worker in health.values():
                        assert not worker.get('lastErrorMessage'), (backend, health)
                        interval = intervals[worker['worker']]
                        if elapsed > interval + 300:
                            assert worker['cyclesCompleted'] > 0, (backend, 'worker never ran', worker)
                            last_run = dt.datetime.fromisoformat(worker['lastRunUtc'].replace('Z', '+00:00'))
                            assert (dt.datetime.now(dt.timezone.utc) - last_run).total_seconds() < interval + 300, (backend, 'worker stalled', worker)
                    samples = sorted(latencies[backend])
                    p95 = samples[max(0, int(len(samples) * .95) - 1)]
                    slow_windows[backend] = slow_windows[backend] + 1 if p95 > 2 else 0
                    assert slow_windows[backend] < 3, (backend, 'recall p95 exceeds 2s for three consecutive minutes')
                    row = {'utc': now(), 'elapsedSeconds': elapsed, 'round': round_number, 'backend': backend,
                           'resources': resources, 'recallP95Seconds': p95, 'workerStatus': health,
                           'requests': client.count, 'writerVersion': versions[backend]}
                    with (output / 'samples.jsonl').open('a', encoding='utf-8') as log: log.write(json.dumps(row) + '\n')
                    summary['backends'][backend].update(row)
                    latencies[backend].clear()
                summary.update(state='running_with_blockers' if summary['qualityFailures'] else 'running',
                               lastHeartbeatUtc=now(), elapsedSeconds=elapsed, round=round_number)
                write_json(output / 'status.json', summary)
                last_sample = elapsed
            round_number += 1
            time.sleep(max(0, args.interval - (time.monotonic() - tick)))
        for backend, client in list(clients.items()):
            summary['backends'][backend].update(requests=client.count, writerVersion=versions[backend])
            assert summary['backends'][backend]['overlappingReads'] > 0, 'No read/write overlap observed'
            full_check(backend)
            health = client.call('engram_status')
            if args.hours >= 48:
                assert all(worker['cyclesCompleted'] > 0 for worker in health.values()), health
            client.close()
            clients[backend] = start(backend, 'final-restart')
            assert clients[backend].call('get_memory', id='soak-version')['text'] == f'Writer committed catalogue revision {versions[backend]}.'
            assert clients[backend].call('get_memory', id='soak-ephemeral') == "Entry 'soak-ephemeral' not found."
            full_check(backend)
            clients[backend].close()
            del clients[backend]
            for label, marker, agent, tenant in (
                ('private', CANARIES[0], 'other-owner', 'soak-a'),
                ('foreign', CANARIES[1], 'soak-owner', 'soak-b'),
                ('legacy', CANARIES[2], 'soak-owner', '')):
                with start(backend, 'verify-' + label, agent, tenant) as sentinel:
                    entry = sentinel.call('get_memory', id='c-owner')
                    assert entry['text'] == f'Citrine production deployment owner is {marker}.'
                    assert entry['lifecycleState'] == 'archived' and entry['accessCount'] == 1, entry
        summary.update(state='failed' if summary['qualityFailures'] else 'passed', reliabilityCompleted=True,
                       completedUtc=now(), elapsedSeconds=time.monotonic() - start_time)
        if summary['qualityFailures']:
            failures.append(RecallQualityFailure('Recorded recall quality failures remain release blockers'))
    except BaseException as error:
        summary.update(state='failed', completedUtc=now(), failure=str(error), traceback=traceback.format_exc())
        for backend, client in clients.items():
            with client.lock: recent = list(client.recent)
            write_json(output / (backend + '-last-requests.json'), recent)
            if args.dump_tool and client.process.poll() is None:
                with (output / (backend + '-dump.log')).open('w') as log:
                    try: subprocess.run([str(args.dump_tool.resolve()), 'collect', '-p', str(client.process.pid),
                        '--type', 'Mini', '-o', str(output / (backend + '.dmp'))], stdout=log, stderr=subprocess.STDOUT, timeout=45)
                    except Exception as dump_error: log.write(str(dump_error))
        failures.append(error)
    finally:
        executor.shutdown(wait=False, cancel_futures=True)
        for client in clients.values():
            try: client.close(check=not failures)
            except Exception as error:
                summary.update(state='failed', shutdownFailure=str(error))
                failures.append(error)
        if os.name == 'nt': ctypes.windll.kernel32.SetThreadExecutionState(0x80000000)
        write_json(output / 'status.json', summary)
    print(json.dumps(summary, indent=2), flush=True)
    if failures: raise SystemExit(1)


if __name__ == '__main__':
    main()
