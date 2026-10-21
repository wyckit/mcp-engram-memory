#!/usr/bin/env python3
"""Check a locally built Docker image over MCP with generated data in a new directory."""
import argparse
import json
import subprocess
from pathlib import Path
from validation.mcp_client import Client

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--image', required=True)
parser.add_argument('--output', required=True, type=Path)
args = parser.parse_args()
output = args.output.resolve()
output.mkdir(parents=True, exist_ok=False)
data = output / 'data'
data.mkdir()
configuration = json.loads(subprocess.check_output(['docker', 'image', 'inspect', args.image]))[0]['Config']
assert 'ORT_DISABLE_TELEMETRY=1' in configuration['Env']
# Also exercise startup enforcement if a caller overrides the image's default.
command = ['docker', 'run', '--rm', '-i', '--network', 'none', '-e', 'ORT_DISABLE_TELEMETRY=0', '-v', f'{data}:/app/data', args.image]
with Client(command, output, 'docker-fresh', {}) as client:
    assert len(client.request('tools/list')['tools']) == 17
    assert client.call('remember', id='docker-roundtrip', ns='docker', text='Container data survives a new container.')['status'] == 'stored'
with Client(command, output, 'docker-restart', {}) as client:
    assert client.call('get_memory', id='docker-roundtrip')['text'] == 'Container data survives a new container.'
(output / 'summary.json').write_text('{"fresh":"passed","volumeRestart":"passed","network":"disabled"}\n')
