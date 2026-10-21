#!/usr/bin/env python3
"""Verify opt-out at native library load, then exercise the real runtime over MCP.

Requires CMake + a C compiler; Windows ETW measurement also needs an elevated
runner (GitHub's Windows runners provide this). Uses disposable published copies.
"""
import argparse
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import uuid
import xml.etree.ElementTree as ET
from validation.mcp_client import Client

CI_NAMES = ('CI', 'TF_BUILD', 'GITHUB_ACTIONS', 'GITLAB_CI', 'CIRCLECI', 'TRAVIS',
            'JENKINS_URL', 'CODEBUILD_BUILD_ID', 'BUILDKITE', 'TEAMCITY_VERSION',
            'APPVEYOR', 'BITBUCKET_BUILD_NUMBER', 'SYSTEM_TEAMFOUNDATIONCOLLECTIONURI')
PROVIDER = '{3a26b1ff-7484-7484-7484-15261f42614d}'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--server', type=Path, required=True, help='Published McpEngramMemory.dll')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    source = Path(__file__).resolve().parent / 'validation'
    windows = os.name == 'nt'
    architecture = 'arm64' if platform.machine().lower() in ('arm64', 'aarch64') else 'x64'
    rid = ('win' if windows else 'osx' if sys.platform == 'darwin' else 'linux') + '-' + architecture
    library_name = 'onnxruntime.dll' if windows else 'libonnxruntime.dylib' if sys.platform == 'darwin' else 'libonnxruntime.so'
    published = output / 'published'
    shutil.copytree(args.server.resolve().parent, published)
    server = published / args.server.name
    native = published / 'runtimes' / rid / 'native' / library_name
    if not native.is_file(): native = published / library_name
    assert native.is_file(), native
    # Never let CI's automatic ORT suppression or a developer's opt-out mask a bug.
    for key in list(os.environ):
        if key in CI_NAMES or key.startswith(('ORT_', 'MEMORY_', 'SYNTHESIS_')) or key == 'AGENT_ID':
            del os.environ[key]
    env = dict(os.environ)
    env.update(MEMORY_DATA_PATH=str(output / 'data'), MEMORY_STORAGE='json')
    build = output / 'probe-build'
    with (output / 'compiler.log').open('w', encoding='utf-8') as log:
        subprocess.run(['cmake', '-S', str(source / 'native-telemetry-probe'), '-B', str(build)],
                       check=True, stdout=log, stderr=subprocess.STDOUT)
        subprocess.run(['cmake', '--build', str(build), '--config', 'Release'],
                       check=True, stdout=log, stderr=subprocess.STDOUT)
    probe = next(build.rglob(library_name))
    original = native.read_bytes()
    probes = []
    try:
        shutil.copyfile(probe, native)
        for inherited in (None, '0', 'false', '1'):
            child_env = dict(env)
            if inherited is not None:
                child_env.update(ORT_DISABLE_TELEMETRY=inherited, ORT_RUNNING_UNIT_TESTS=inherited)
            requests = [
                {'jsonrpc': '2.0', 'id': 1, 'method': 'initialize', 'params': {'protocolVersion': '2024-11-05',
                 'capabilities': {}, 'clientInfo': {'name': 'telemetry-probe', 'version': '1'}}},
                {'jsonrpc': '2.0', 'method': 'notifications/initialized'},
                {'jsonrpc': '2.0', 'id': 2, 'method': 'tools/call', 'params': {'name': 'remember',
                 'arguments': {'ns': 'probe', 'id': 'probe', 'text': 'Synthetic telemetry startup probe.'}}}]
            result = subprocess.run(['dotnet', str(server)], env=child_env, cwd=output,
                input='\n'.join(json.dumps(r) for r in requests) + '\n', capture_output=True, text=True, timeout=90)
            (output / f'probe-{inherited}.log').write_text(result.stdout + result.stderr, encoding='utf-8')
            assert result.returncode == 86, (inherited, result.returncode, result.stderr)
            record = next(json.loads(line) for line in result.stdout.splitlines() if '"native-load"' in line)
            assert record['disabled'] == 1 and record['ciPresent'] == 0, record
            if windows: assert record['windowsSuppressed'] == 1, record
            else: assert record['windowsSuppressed'] == int(inherited == '1'), record
            probes.append({'inherited': inherited, **record})
    finally:
        native.write_bytes(original)

    trace_name = 'engram-telemetry-' + uuid.uuid4().hex
    trace = output / 'onnx.etl'
    pids, controls = [], {}
    def trace_command(arguments):
        subprocess.run(arguments, check=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    if windows:
        trace_command(['logman', 'start', trace_name, '-p', PROVIDER, '0xffffffffffffffff', '5',
                       '-o', str(trace), '-ets'])
    try:
        if windows:
            for label, flag in (('positive', '0'), ('suppressed', '1')):
                control_env = dict(env, ORT_RUNNING_UNIT_TESTS=flag, ORT_DISABLE_TELEMETRY='0')
                result = subprocess.run([sys.executable, str(source / 'onnx-etw-control.py'), str(native)],
                    env=control_env, capture_output=True, text=True, check=True, timeout=30)
                controls[label] = json.loads(result.stdout)
                assert controls[label]['version'] == '1.29.0', controls[label]
        for profile, count in (('minimal', 17), ('standard', 39), ('full', 63)):
            # Explicitly hostile inherited values exercise enforcement in the real binary too.
            settings = dict(env, MEMORY_TOOL_PROFILE=profile, ORT_DISABLE_TELEMETRY='0', ORT_RUNNING_UNIT_TESTS='0')
            with Client(['dotnet', str(server)], output, profile, settings) as client:
                pids.append(client.process.pid)
                assert len(client.request('tools/list')['tools']) == count
                assert client.call('remember', ns='telemetry-' + profile, id=profile,
                    text='The Cedar production recovery owner is Rowan.')['status'] == 'stored'
                recalled = client.call('recall', ns='telemetry-' + profile, query='Who owns Cedar production recovery?')
                assert 'Rowan' in json.dumps(recalled), recalled
            with Client(['dotnet', str(server)], output, profile + '-restart', settings) as client:
                pids.append(client.process.pid)
                assert client.call('get_memory', id=profile)['text'] == 'The Cedar production recovery owner is Rowan.'
    finally:
        if windows: trace_command(['logman', 'stop', trace_name, '-ets'])
    event_counts = None
    if windows:
        xml_path = output / 'onnx.xml'
        trace_command(['tracerpt', str(trace), '-o', str(xml_path), '-of', 'XML', '-y'])
        event_counts = {}
        for event in ET.parse(xml_path).getroot().iter('{http://schemas.microsoft.com/win/2004/08/events/event}Event'):
            system = event.find('{*}System')
            provider = system.find('{*}Provider')
            if provider is not None and provider.get('Guid', '').lower() == PROVIDER.lower():
                pid = int(system.find('{*}Execution').get('ProcessID'))
                event_counts[str(pid)] = event_counts.get(str(pid), 0) + 1
        assert event_counts.get(str(controls['positive']['pid']), 0) > 0, 'ETW positive control did not emit; measurement invalid'
        for pid in [controls['suppressed']['pid'], *pids]:
            assert event_counts.get(str(pid), 0) == 0, (pid, event_counts)
    summary = {'platform': rid, 'nativeLoadProbes': probes, 'profiles': [17, 39, 63],
               'mcpRememberRecallRestart': 'passed', 'etwControls': controls, 'serverPids': pids,
               'etwEventCounts': event_counts,
               'limits': 'Native environment ordering and Windows ETW measured; no packet-capture claim.'}
    (output / 'summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
    print(json.dumps(summary, indent=2))


if __name__ == '__main__':
    main()
