#!/usr/bin/env python3
"""Validate complete per-process compiler campaigns and aggregate their unchanged raw evidence."""
import csv
import json
from pathlib import Path
import sys

output = Path(sys.argv[1]).resolve()
repetitions = int(sys.argv[2])
profiles = sorted((output / 'profiles').iterdir())
if not profiles:
    raise SystemExit('No compiler workload matched the declared filter.')
for filename in ('raw.json', 'controls.json', 'summary.json', 'build-raw.json', 'build-summary.json'):
    combined = []
    for profile in profiles:
        rows = json.loads((profile / filename).read_text())
        if filename == 'raw.json':
            groups = {(row['Workload'], row['Edit'], row['Component']) for row in rows}
            for workload, edit, component in groups:
                observed = [row['Repetition'] for row in rows
                            if (row['Workload'], row['Edit'], row['Component']) == (workload, edit, component)]
                if sorted(observed) != list(range(repetitions)):
                    raise SystemExit(f'Incomplete samples: {profile.name}, {edit}, {component}')
        combined.extend(rows)
    (output / filename).write_text(json.dumps(combined, indent=2) + '\n')
with (output / 'raw.csv').open('w', newline='') as destination:
    writer = None
    for profile in profiles:
        with (profile / 'raw.csv').open(newline='') as source:
            reader = csv.reader(source)
            header = next(reader)
            if writer is None:
                writer = csv.writer(destination)
                writer.writerow(header)
            writer.writerows(reader)
(output / 'environment.json').write_text(json.dumps({
    'sourceCommit': (output / 'source-commit.txt').read_text().strip(),
    'method': 'One fresh process per workload; full controls and JIT warmup before sampling; atomic checkpoint after each repetition outside operation timings.',
    'profiles': {profile.name: json.loads((profile / 'environment.json').read_text()) for profile in profiles}
}, indent=2) + '\n')
print(f'Validated {len(profiles)} complete compiler workloads with {repetitions} samples per measurement group.')
