#!/usr/bin/env python3
"""Exercise publisher -WhatIf in a disposable checkout; retain pre-existing evidence."""
import argparse
from pathlib import Path
import subprocess

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--repo', required=True, type=Path, help='Disposable checkout only')
args = parser.parse_args()
repo = args.repo.resolve()
evidence = repo / 'artifacts/publisher-check'
evidence.mkdir(parents=True, exist_ok=False)
sentinel = evidence / 'sentinel.txt'
sentinel.write_text('Existing validation evidence must survive.\n', encoding='utf-8')
before = sentinel.read_bytes()
with (evidence / 'publisher.log').open('w', encoding='utf-8') as log:
    subprocess.run(['pwsh', '-NoProfile', '-File', str(repo / 'publish-nuget.ps1'), '-WhatIf', '-SkipTests'],
                   cwd=repo, check=True, stdout=log, stderr=subprocess.STDOUT)
assert sentinel.read_bytes() == before
assert len(list((repo / 'artifacts/packages').glob('*/*.nupkg'))) >= 3
(evidence / 'summary.json').write_text('{"whatIf":"passed","existingEvidencePreserved":true}\n')
