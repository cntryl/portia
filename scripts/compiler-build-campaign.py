#!/usr/bin/env python3
"""Measure actual cold compiler and unchanged consumer builds; keep restore and setup outside timings."""
import json
from pathlib import Path
import statistics
import subprocess
import sys
import time

output = Path(sys.argv[1]).resolve()
repetitions = int(sys.argv[2]) if len(sys.argv) > 2 else 5
rows = []
for project in sorted((output / 'fixtures').glob('*/CompilerConsumer.csproj')):
    workload = project.parent.name
    with (output / f'{workload}-build-setup.log').open('w') as log:
        subprocess.run(['dotnet', 'restore', str(project)], stdout=log, stderr=subprocess.STDOUT, check=True)
        subprocess.run(['dotnet', 'build', str(project), '-c', 'Release', '--no-restore', '-m:1',
                        '/nodeReuse:false', '-p:UseSharedCompilation=false'], stdout=log, stderr=subprocess.STDOUT, check=True)
    for mode in ('cold-compiler', 'unchanged-build'):
        for repetition in range(repetitions):
            command = ['dotnet', 'build', str(project), '-c', 'Release', '--no-restore', '-m:1',
                       '/nodeReuse:false', '-p:UseSharedCompilation=false', '-p:BuildProjectReferences=false']
            if mode == 'cold-compiler':
                command.append('--no-incremental')
            with (output / f'{workload}-{mode}-{repetition}.log').open('w') as log:
                started = time.perf_counter_ns()
                subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, check=True)
                elapsed = (time.perf_counter_ns() - started) / 1e6
            rows.append({'workload': workload, 'mode': mode, 'repetition': repetition,
                         'milliseconds': elapsed, 'command': command, 'compiledSuccessfully': True})
            (output / 'build-raw.json').write_text(json.dumps(rows, indent=2) + '\n')
    print(workload, 'actual compiler/build controls passed', flush=True)
summary = []
for workload in sorted({row['workload'] for row in rows}):
    for mode in ('cold-compiler', 'unchanged-build'):
        values = sorted(row['milliseconds'] for row in rows if row['workload'] == workload and row['mode'] == mode)
        summary.append({'workload': workload, 'mode': mode, 'samples': len(values),
                        'p50Milliseconds': statistics.median(values), 'p95Milliseconds': values[-1],
                        'boundary': 'MSBuild invocation with prebuilt framework references; fresh compiler process for cold builds; restore excluded'})
(output / 'build-summary.json').write_text(json.dumps(summary, indent=2) + '\n')
