#!/usr/bin/env python3
"""Validate the real source installer in a new checkout/config/data sandbox (Python 3.11+)."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tomllib
from validation.mcp_client import Client


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--shell', choices=('bash', 'powershell', 'pwsh'), required=True)
    parser.add_argument('--sdk', choices=('8', '9', '10'), required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    repo = Path(__file__).resolve().parent.parent
    checkout, config, data = (output / name for name in ('source with spaces', 'config', 'data'))
    files = subprocess.check_output(['git', 'ls-files', '-co', '--exclude-standard', '-z'], cwd=repo).decode().split('\0')
    for name in set(filter(None, files)):
        source = repo / name
        if source.is_file():
            target = checkout / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, target)
    sdks = subprocess.check_output(['dotnet', '--list-sdks'], text=True).splitlines()
    versions = [line.split()[0] for line in sdks if line.startswith(args.sdk + '.')]
    version = sorted(versions, key=lambda x: tuple(int(v) for v in x.split('.')))[-1]
    (checkout / 'global.json').write_text(json.dumps({'sdk': {'version': version, 'rollForward': 'disable'}}))
    assert subprocess.check_output(['dotnet', '--version'], cwd=checkout, text=True).strip() == version
    env = {k: v for k, v in os.environ.items() if not k.startswith(('MEMORY_', 'SYNTHESIS_')) and k != 'AGENT_ID'}

    def setup(label, success=True):
        if args.shell == 'bash':
            command = ['bash', str(checkout / 'setup.sh'), '--for', 'all', '--config-root', str(config), '--data-path', str(data)]
        else:
            command = [args.shell, '-NoProfile', '-File', str(checkout / 'setup.ps1'), '-For', 'all', '-ConfigRoot', str(config), '-DataPath', str(data)]
        with (output / (label + '.log')).open('w', encoding='utf-8') as log:
            result = subprocess.run(command, cwd=checkout, env=env, stdout=log, stderr=subprocess.STDOUT)
        assert (result.returncode == 0) == success, (label, result.returncode)

    paths = [config / '.claude.json', config / '.gemini/settings.json', config / '.codex/config.toml']
    setup('fresh')

    def read(path):
        return tomllib.loads(path.read_text(encoding='utf-8')) if path.suffix == '.toml' else json.loads(path.read_text(encoding='utf-8'))

    def entry(path):
        return read(path)['mcp_servers' if path.suffix == '.toml' else 'mcpServers']['engram-memory']

    for path in paths:
        e = entry(path)
        assert e['command'] == 'dotnet' and len(e['args']) == 1
        assert Path(e['args'][0]).is_file()
        assert e['env']['MEMORY_TOOL_PROFILE'] == 'minimal'
        assert Path(e['env']['MEMORY_DATA_PATH']) == data
        if os.name != 'nt': assert path.stat().st_mode & 0o777 == 0o600
    launch = entry(paths[0])
    with Client([launch['command'], *launch['args']], output, 'configured-server', launch['env']) as c:
        assert len(c.request('tools/list')['tools']) == 17
        assert c.call('remember', id='source-check', ns='source', text='Source setup preserves the deployment owner.')['status'] == 'stored'
    for path in paths[:2]:
        model = read(path)
        model['sentinel'] = {'preserve': True}
        model['mcpServers']['engram-memory']['env']['CUSTOM_SETTING'] = 'keep'
        path.write_text(json.dumps(model), encoding='utf-8')
    with paths[2].open('a', encoding='utf-8') as f:
        f.write('\n# unrelated server\n[mcp_servers.engram-memory-helper]\ncommand = "keep"\n')
    originals = {path: path.read_bytes() for path in paths}
    setup('repeat')
    for path in paths:
        backups = list(path.parent.glob(path.name + '.engram-backup-*'))
        assert any(b.read_bytes() == originals[path] for b in backups), path
    for path in paths[:2]:
        assert read(path)['sentinel']['preserve']
        assert entry(path)['env']['CUSTOM_SETTING'] == 'keep'
    assert read(paths[2])['mcp_servers']['engram-memory-helper']['command'] == 'keep'
    with Client([launch['command'], *launch['args']], output, 'configured-restart', launch['env']) as c:
        assert c.call('get_memory', id='source-check')['text'] == 'Source setup preserves the deployment owner.'
    paths[1].write_text('{"malformed":', encoding='utf-8')
    originals = {path: path.read_bytes() for path in paths}
    setup('malformed', success=False)
    assert all(path.read_bytes() == before for path, before in originals.items())
    (output / 'summary.json').write_text(json.dumps({'sdk': version, 'shell': args.shell,
        'fresh': 'passed', 'repeatAndBackup': 'passed', 'malformedUnchanged': 'passed',
        'configuredStdioAndRestart': 'passed'}, indent=2), encoding='utf-8')
    print((output / 'summary.json').read_text())


if __name__ == '__main__':
    main()
