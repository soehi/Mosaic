# -*- coding: utf-8 -*-
"""Generate the payload manifest embedded in the MSI Binary table.

The manifest is the native custom action's only trusted source of expected
payload files, so it must be produced from the same publish directory, with the
same inclusion rules, as the Payload.wxs component list. Every row carries the
component and file id used by Payload.wxs so the two can be cross-checked.

Usage:
    python -I scripts/generate_installer_manifest.py [publish_dir] [output]
Defaults:
    publish_dir = artifacts/local-run
    output      = artifacts/installer/Manifest.tsv
"""
import hashlib
import io
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PUBLISH = os.path.abspath(sys.argv[1]) if len(sys.argv) > 1 else os.path.join(ROOT, 'artifacts', 'local-run')
OUTPUT = os.path.abspath(sys.argv[2]) if len(sys.argv) > 2 else os.path.join(ROOT, 'artifacts', 'installer', 'Manifest.tsv')
PAYLOAD = os.path.join(ROOT, 'artifacts', 'installer', 'Payload.wxs')
RUNTIME_DIRS = {'Data', 'Config', 'Logs'}
HEADER = 'mosaic-manifest-v1'
SHORTCUTS = [
    ('desktop', 'Mosaic.lnk', 'Mosaic.Desktop.exe'),
    ('programmenu', 'Mosaic\\Mosaic.lnk', 'Mosaic.Desktop.exe'),
]


def mosaic_id(relative):
    return hashlib.sha256(relative.lower().encode('utf-8')).hexdigest().upper()[:24]


def file_sha(path):
    digest = hashlib.sha256()
    with open(path, 'rb') as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b''):
            digest.update(chunk)
    return digest.hexdigest().upper()


def included(relative):
    name = relative.replace('/', '\\').split('\\')[-1]
    if name == '.mosaic.instance.lock':
        return False
    if name.lower().endswith('.pdb'):
        return False
    return relative.replace('/', '\\').split('\\')[0] not in RUNTIME_DIRS


def collect():
    rows = []
    for base, _dirs, files in os.walk(PUBLISH):
        for name in files:
            path = os.path.join(base, name)
            relative = os.path.relpath(path, PUBLISH).replace('/', '\\')
            if not included(relative):
                continue
            size = os.path.getsize(path)
            rows.append((relative, size, file_sha(path)))
    rows.sort(key=lambda item: item[0].lower())
    return rows


def main():
    if not os.path.isdir(PUBLISH):
        sys.exit('Publish directory not found: ' + PUBLISH)
    rows = collect()
    if not rows:
        sys.exit('Publish directory is empty: ' + PUBLISH)
    seen = set()
    lines = [HEADER]
    expected_files = set()
    for relative, size, digest in rows:
        if relative in seen:
            sys.exit('Duplicate manifest path: ' + relative)
        seen.add(relative)
        ident = mosaic_id(relative)
        expected_files.add('F' + ident)
        lines.append('file\t%s\t%d\t%s\tC%s\tF%s' % (relative, size, digest, ident, ident))
    for location, link, target in SHORTCUTS:
        lines.append('shortcut\t%s\t%s\t%s' % (location, link, target))

    # Cross-check against the File ids actually emitted into Payload.wxs so the
    # manifest can never silently describe a different payload.
    if os.path.exists(PAYLOAD):
        with io.open(PAYLOAD, encoding='utf-8') as handle:
            payload_ids = set(re.findall(r'<File Id="(F[0-9A-F]{24})"', handle.read()))
        if payload_ids != expected_files:
            missing = sorted(expected_files - payload_ids)
            extra = sorted(payload_ids - expected_files)
            sys.exit('Manifest/Payload.wxs mismatch. only-in-manifest=%s only-in-payload=%s' % (missing[:5], extra[:5]))

    directory = os.path.dirname(OUTPUT)
    if directory:
        os.makedirs(directory, exist_ok=True)
    with io.open(OUTPUT, 'w', encoding='utf-8', newline='\r\n') as handle:
        handle.write('\n'.join(lines) + '\n')
    print('wrote %s: %d files, %d shortcuts' % (OUTPUT, len(rows), len(SHORTCUTS)))


if __name__ == '__main__':
    main()