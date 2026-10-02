#!/usr/bin/env python3
"""Reject prerelease external NuGet dependencies, including locked transitive packages."""
import argparse
import json
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parent.parent)
args = parser.parse_args()
failures = []
for lock in sorted(args.root.rglob('packages.lock.json')):
    if any(part in ('obj', 'bin', 'artifacts', '.git') for part in lock.relative_to(args.root).parts):
        continue
    dependencies = json.loads(lock.read_text(encoding='utf-8-sig'))['dependencies']
    for framework, packages in dependencies.items():
        for name, package in packages.items():
            version = package.get('resolved', '')
            if not name.startswith('Cntryl.Portia.') and '-' in version:
                failures.append(f'{lock.relative_to(args.root)} [{framework}] {name} {version}')
props = args.root / 'Directory.Packages.props'
if props.exists():
    for package in ET.parse(props).iter('PackageVersion'):
        name, version = package.get('Include', ''), package.get('Version', '')
        if not name.startswith('Cntryl.Portia.') and '-' in version:
            failures.append(f'Directory.Packages.props {name} {version}')
if failures:
    print('Prerelease external dependencies are not allowed:', file=sys.stderr)
    print('\n'.join(failures), file=sys.stderr)
    sys.exit(1)
print('All declared and locked external NuGet dependencies are stable.')
