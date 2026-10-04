#!/usr/bin/env python3
"""Read-only integrity verification. Does not load PawnIO modules or touch hardware."""
from pathlib import Path
import argparse
import hashlib
import json
import zipfile


def sha(data):
    return hashlib.sha256(data).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--release-archive-dir', type=Path)
    args = parser.parse_args()
    directory = Path(__file__).resolve().parent
    root = directory.parent.parent
    manifest = json.loads((directory / 'manifest.json').read_text(encoding='utf-8'))
    checked = 0
    for release in manifest['releases']:
        source_path = root / release['sourceArchive']
        if sha(source_path.read_bytes()) != release['sourceArchiveSha256']:
            raise ValueError(f'Source archive mismatch: {source_path.name}')
        with zipfile.ZipFile(source_path) as source:
            license_data = source.read(release['sourceArchiveRoot'] + 'COPYING')
            if sha(license_data) != release['licenseSha256']:
                raise ValueError('Source license mismatch')
            if license_data != (directory / 'COPYING').read_bytes():
                raise ValueError('Bundled license mismatch')
            for module in release['binaries']:
                data = (root / module['path']).read_bytes()
                if len(data) != module['bytes'] or sha(data) != module['sha256']:
                    raise ValueError(f"Module mismatch: {module['path']}")
                if sha(source.read(module['sourceEntry'])) != module['sourceEntrySha256']:
                    raise ValueError(f"Module source mismatch: {module['sourceEntry']}")
                checked += 1
        if args.release_archive_dir:
            archive_name = release['releaseArchiveUrl'].rsplit('/', 1)[1]
            archive_path = args.release_archive_dir / archive_name
            if sha(archive_path.read_bytes()) != release['releaseArchiveSha256']:
                raise ValueError(f'Official archive mismatch: {archive_name}')
            with zipfile.ZipFile(archive_path) as archive:
                for module in release['binaries']:
                    if sha(archive.read(module['releaseEntry'])) != module['sha256']:
                        raise ValueError(f"Official module mismatch: {module['releaseEntry']}")
    print(json.dumps({'result': 'PASS', 'sourceArchives': len(manifest['releases']),
                      'modules': checked, 'officialArchivesChecked': bool(args.release_archive_dir)}))


if __name__ == '__main__':
    main()
