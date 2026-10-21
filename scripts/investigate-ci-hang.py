#!/usr/bin/env python3
"""Bounded reproduction of CI run 39391883137, without changing target code or tests."""
import argparse
import datetime
import json
import os
from pathlib import Path
import signal
import subprocess
import time
import xml.etree.ElementTree as ET


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--mode', choices=('parallel-frameworks', 'net8-only'), required=True)
    parser.add_argument('--iterations', type=int, default=3)
    args = parser.parse_args()
    repo, output = args.repo.resolve(), args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    revision = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=repo, text=True).strip()
    summary = {'revision': revision, 'mode': args.mode, 'runs': [], 'rootCauseEstablished': False}
    (output / 'dotnet-info.txt').write_bytes(subprocess.check_output(['dotnet', '--info']))
    for iteration in range(args.iterations):
        destination = output / str(iteration + 1)
        destination.mkdir()
        command = ['dotnet', 'test', 'McpEngramMemory.slnx', '--no-build', '-c', 'Release',
                   '--filter', 'Category!=MSA&Category!=RecallScale&Category!=IndependentRecall',
                   '--results-directory', str(destination), '--logger', 'trx;LogFilePrefix=results',
                   '--logger', 'console;verbosity=normal', '--diag', str(destination / 'vstest.log'),
                   '--blame-hang-timeout', '5m', '--blame-hang-dump-type', 'mini']
        if args.mode == 'net8-only': command += ['-f', 'net8.0']
        started = time.monotonic()
        row = {'iteration': iteration + 1, 'startedUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
               'command': command}
        with (destination / 'console.log').open('w', encoding='utf-8') as log:
            # Ten minutes leaves the five-minute inactivity dump enough time to finish.
            try:
                process = subprocess.Popen(command, cwd=repo, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
                row['exitCode'] = process.wait(timeout=600)
            except subprocess.TimeoutExpired:
                # The investigator runs on Linux. Stop the whole test process group,
                # including a wedged child testhost, after the dump grace period.
                os.killpg(process.pid, signal.SIGKILL)
                process.wait(timeout=15)
                row['outerTimeout'] = True
                row['exitCode'] = -1
        row['seconds'] = time.monotonic() - started
        row['testCounters'] = []
        for report in destination.glob('*.trx'):
            counters = ET.parse(report).getroot().find('{*}ResultSummary/{*}Counters')
            row['testCounters'].append({'file': report.name, **counters.attrib})
        row['sequences'] = [str(p.relative_to(destination)) for p in destination.rglob('Sequence*.xml')]
        row['dumps'] = [str(p.relative_to(destination)) for p in destination.rglob('*.dmp')]
        expected = 1 if args.mode == 'net8-only' else 3
        row['passed'] = row['exitCode'] == 0 and len(row['testCounters']) == expected and all(
            int(c['executed']) > 0 and int(c['failed']) == 0 and c['executed'] == c['passed']
            for c in row['testCounters'])
        summary['runs'].append(row)
        (output / 'summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
        print(json.dumps(row), flush=True)
        if not row['passed']: raise SystemExit(1)


if __name__ == '__main__':
    main()
