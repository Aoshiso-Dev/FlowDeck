"""Preserve timestamps of byte-identical pinned inputs for a stopped incremental build.

This never executes OpenRGB or changes source bytes. The caller must first verify
that the previous build process has finished, then reuse the same build flags and
validated ASCII aliases. Changed/forced files are made newer than existing objects.
"""
import argparse
import hashlib
import importlib.util
import json
import os
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def prepare_inputs(previous_build, new_source, evidence, force_inputs=()):
    previous_build, new_source, evidence = map(Path, [previous_build, new_source, evidence])
    result = json.loads((previous_build/'build-result.json').read_text(encoding='utf-8'))
    if not result['succeeded'] or result['openRgbExecuted'] or evidence.exists():
        raise ValueError('Need a successful unexecuted prior build and a new evidence file')
    previous_source = Path(result['sourceDirectory'])
    digest = lambda path: hashlib.sha256(path.read_bytes()).hexdigest().upper()
    if digest(previous_source/'flowdeck-source.json') != result['sourceManifestSha256']:
        raise ValueError('Previous source manifest does not match its build')
    if digest(previous_build/'release/OpenRGB.exe') != result['binarySha256']:
        raise ValueError('Previous binary does not match its build')
    spec = importlib.util.spec_from_file_location('source', ROOT/'scripts/prepare-openrgb-source.py')
    source = importlib.util.module_from_spec(spec); spec.loader.exec_module(source)
    prior, current = source.verify(previous_source), source.verify(new_source)
    if previous_source.resolve() == new_source.resolve() or prior['sourceRevision'] != current['sourceRevision']:
        raise ValueError('Need separate pinned inputs for the same base revision')
    old = {entry['path']:entry for entry in prior['files']}
    kept, changed, freshened = [], [], []
    force_inputs = set(force_inputs)
    if not force_inputs.issubset({entry['path'] for entry in current['files']}):
        raise ValueError('Forced input is absent from the verified manifest')
    # New source can be prepared while the previous build is still compiling.
    # Its original time is therefore not evidence that it is newer than an object.
    object_directory = Path(result['buildDirectory'])
    newest_object = max((path.stat().st_mtime_ns for path in object_directory.rglob('*.obj')), default=0)
    rebuild_after = max(time.time_ns(), newest_object + 2_000_000_000)
    for entry in current['files']:
        name = entry['path']
        if old.get(name) == entry and name not in force_inputs:
            stats = (previous_source/name).stat()
            os.utime(new_source/name, ns=(stats.st_atime_ns, stats.st_mtime_ns))
            kept.append(name)
        else:
            os.utime(new_source/name, ns=(rebuild_after, rebuild_after))
            freshened.append(name)
            if old.get(name) != entry: changed.append(name)
    source.verify(new_source)
    record = {'previousBuild':str(previous_build.resolve()), 'previousBinarySha256':result['binarySha256'],
        'previousSourceManifestSha256':result['sourceManifestSha256'], 'newSource':str(new_source.resolve()),
        'newSourceManifestSha256':digest(new_source/'flowdeck-source.json'),
        'unchangedInputs':kept, 'changedInputs':changed, 'freshenedInputs':freshened,
        'rebuildAfterNanoseconds':rebuild_after, 'sourceBytesChanged':False, 'openRgbExecuted':False}
    evidence.write_text(json.dumps(record, indent=2)+'\n', encoding='utf-8')
    print(json.dumps({'unchangedInputs':len(kept),'changedInputs':changed,'freshenedInputs':freshened,'sourceBytesChanged':False}))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('previous_build'); parser.add_argument('new_source'); parser.add_argument('evidence')
    parser.add_argument('--force-input', action='append', default=[])
    args = parser.parse_args(); prepare_inputs(args.previous_build, args.new_source, args.evidence, args.force_input)
