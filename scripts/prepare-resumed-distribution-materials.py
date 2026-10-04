"""Extend verified r4 materials with stable native and firmware inputs, entirely offline."""
import argparse
import importlib.util
import io
import json
import shutil
import tarfile
import uuid
import zipfile
from pathlib import Path, PurePosixPath

ROOT = Path(__file__).resolve().parents[1]
_spec = importlib.util.spec_from_file_location("materials", ROOT / "scripts/prepare-distribution-notices.py")
base = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(base)


def prepare(destination):
    output = Path(destination).resolve()
    if output.exists() or output == ROOT or not output.is_relative_to(ROOT):
        raise ValueError("Use a new output directory in the workspace")
    lock_path = ROOT / "third-party/distribution-resumed-materials.lock.json"
    lock = base.read_json(lock_path)
    previous = ROOT.joinpath(*base.relative(lock['baseDirectory']).parts)
    verified = base.verify(previous)
    if lock['format'] != 1 or verified['sha256'] != lock['baseManifestSha256']:
        raise ValueError("Reviewed base materials changed")
    for relative, expected in lock['inputs'].items():
        if base.digest(ROOT.joinpath(*base.relative(relative).parts).read_bytes()) != expected:
            raise ValueError("Reviewed input changed: " + relative)
    stage = output.with_name(output.name + '.preparing-' + uuid.uuid4().hex)
    stage.mkdir(parents=True)
    manifest = base.read_json(previous / 'materials.json')
    files = {}
    components = manifest['components']
    by_id = {c['id'].casefold(): c for c in components}

    def put(name, data):
        relative = base.relative(name)
        if name.casefold() in {n.casefold() for n in files}:
            raise ValueError("Duplicate material: " + name)
        path = stage.joinpath(*relative.parts)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
        files[name] = {'length': len(data), 'sha256': base.digest(data)}
        return name

    for name in manifest['files']:
        put('metadata/base-README.md' if name == 'README.md' else name, (previous / name).read_bytes())
    put('metadata/base-materials.json', (previous / 'materials.json').read_bytes())
    put('metadata/resumed-input-lock.json', lock_path.read_bytes())
    terms = []
    for download in lock['downloads']:
        if download.get('collectedForResearchOnly'):
            continue
        path = ROOT.joinpath(*base.relative(download['cachePath']).parts)
        data = path.read_bytes()
        if len(data) != download['length'] or base.digest(data) != download['sha256']:
            raise ValueError("Fixed source changed: " + download['file'])
        if download['id'].startswith('msvc-terms/'):
            terms.append(put('notices/native/MSVC/' + download['file'], data))
            continue
        prior = by_id.get(download['id'].casefold())
        if prior:
            if prior.get('sourceProvenance', {}).get('sha256') != download['sha256']:
                raise ValueError('Conflicting component input: ' + download['id'])
            prior.setdefault('additionalSourceReferences', []).append(download)
            continue
        # GitHub owner casing varies between gitmodules. Canonicalize component directories
        # so Windows creates the same ancestor spelling for different revisions of a repo.
        c = {'id': download['id'].lower(), 'version': download['version'], 'sourceProvenance': download, 'notices': []}
        c['source'] = put('sources/' + download['file'], data)
        if download['file'].endswith('.zip'):
            with zipfile.ZipFile(io.BytesIO(data)) as archive:
                for name in archive.namelist():
                    if name.endswith('/') or not base.legal(PurePosixPath(name).name):
                        continue
                    base.relative(name)
                    c['notices'].append(put('notices/' + c['id'] + '/' + name.split('/', 1)[-1], archive.read(name)))
        else:
            with tarfile.open(fileobj=io.BytesIO(data), mode='r:*') as archive:
                for member in archive:
                    if not member.isfile() or not base.legal(PurePosixPath(member.name).name):
                        continue
                    # Registry packages have no enclosing source directory; preserve their paths.
                    base.relative(member.name)
                    c['notices'].append(put('notices/' + c['id'] + '/' + member.name, archive.extractfile(member).read()))
        if not c['notices']:
            raise ValueError("No notice material in " + download['file'])
        components.append(c)
        by_id[c['id'].casefold()] = c

    helper = ROOT / 'artifacts/openrgb-flowdeck-helper-package8-stable1'
    runtime = base.read_json(helper / 'runtime-inputs.json')
    definition = base.read_json(helper / 'openrgb-helper-runtime.json')
    archive_path = helper / definition['archiveUrl'].removeprefix('local:')
    raw = archive_path.read_bytes()
    if base.digest(raw) != definition['archiveSha256'].lower() or runtime.get('buildVariant') != 'stable1':
        raise ValueError("Stable native package changed")
    c = {'id': 'native/OpenRGB', 'version': '1.0-flowdeck-patch8-stable1', 'notices': []}
    with zipfile.ZipFile(io.BytesIO(raw)) as archive:
        for item in definition['files']:
            data = archive.read(definition['archivePrefix'] + item['path'])
            if base.digest(data) != item['sha256'].lower() or len(data) != item['length']:
                raise ValueError("Helper file changed: " + item['path'])
            if item['path'] == 'source/OpenRGB-patch8.zip':
                c['source'] = put('sources/OpenRGB-patch8-stable1.zip', data)
                with zipfile.ZipFile(io.BytesIO(data)) as source:
                    for name in source.namelist():
                        if not name.endswith('/') and base.legal(PurePosixPath(name).name):
                            c['notices'].append(put('notices/native/OpenRGB/' + name.split('/', 1)[-1], source.read(name)))
            if item['path'].startswith('metadata/'):
                put('metadata/openrgb-stable/' + item['path'].removeprefix('metadata/'), data)
    c['runtimeBinaryInputs'] = {i['path']: i['sha256'].lower() for i in runtime['runtimeInputs']}
    components.append(c)
    components.append({'id': 'native/MSVC', 'version': runtime['redistributableVersion'], 'notices': terms,
                       'runtimeBinaryInputs': {i['path']: i['sha256'].lower() for i in runtime['runtimeInputs']
                                              if i['path'].lower().startswith(('msvcp', 'vcruntime', 'concrt'))}})
    put('metadata/openrgb-helper-runtime.json', (helper / 'openrgb-helper-runtime.json').read_bytes())

    relink = ROOT / 'artifacts/firmware-relink-20260929'
    for path in sorted(relink.rglob('*')):
        if path.is_file():
            put('firmware-relink/' + path.relative_to(relink).as_posix(), path.read_bytes())
    tool_notices = ROOT / '.tools/pio-core/packages/toolchain-xtensa-esp32/share/licenses'
    c = {'id': 'firmware/Xtensa-runtime-notices', 'version': '8.4.0+2021r2-patch5', 'notices': []}
    for path in sorted(tool_notices.rglob('*')):
        if path.is_file():
            c['notices'].append(put('notices/firmware/Xtensa/' + path.relative_to(tool_notices).as_posix(), path.read_bytes()))
    components.append(c)
    for name in ['prepare-resumed-distribution-materials.py', 'msvc_environment.py', 'build-pawniolib.py',
                 'build-openrgb-source.py', 'prepare-openrgb-stable-source.py', 'package-openrgb-helper.py',
                 'capture-firmware-relink.py', 'prepare-firmware-relink.py', 'relink-firmware.py']:
        put('recipes/resumed/' + name, (ROOT / 'scripts' / name).read_bytes())
    for name in ['firmware-portable-relinked/verification.json', 'firmware-portable-relinked/image-verification.json',
                 'platformio-framework-comparison.json', 'research/firmware-included-archives.json']:
        put('metadata/firmware/' + Path(name).name, (ROOT / 'artifacts/resume-20260929' / name).read_bytes())
    pending = [
        'Integrate and verify this material bundle against the next actual GUI/service package.',
        'Finish correspondence/notice review for prebuilt SDK libraries and native third-party components; collected sources alone are not a source rebuild proof.',
        'Finish public-distribution terms and notices; this development bundle is not a public-release approval.',
        'Real hardware acceptance and separate-PC installation remain unverified.'
    ]
    lines = ['# Third-party distribution materials r5', '',
             'Extends verified r4 without changing that bundle. Uses stable MSVC, source-built PawnIOLib, fixed SDK sources and tested firmware relink inputs.', '',
             'The original package-inventory.json is retained as baseline NuGet/Python evidence. Current native binary inputs are recorded in metadata/openrgb-stable and in the native components below.', '',
             'Three collected host-only CLI/test source inputs are retained in the research cache, and excluded from this firmware distribution bundle.', '',
             '| Component | Version | Source material |', '| --- | --- | --- |']
    lines += ['| ' + c['id'] + ' | ' + c['version'] + ' | ' + c.get('source', 'See notice/provenance') + ' |' for c in components]
    lines += ['', '## Remaining verification', ''] + ['- ' + p for p in pending]
    put('README.md', ('\n'.join(lines) + '\n').encode('utf-8'))
    manifest.update(files=files, components=components, remaining=pending,
                    baseManifestSha256=lock['baseManifestSha256'], resumedInputLockSha256=base.digest(lock_path.read_bytes()),
                    publicDistributionReady=False, hardwareExecuted=False)
    (stage / 'materials.json').write_bytes(base.encoded(manifest))
    result = base.verify(stage)
    if output.exists() or not stage.resolve().is_relative_to(ROOT):
        raise ValueError("Output changed during preparation")
    stage.rename(output)
    print(json.dumps(result))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('new_directory')
    args = parser.parse_args()
    prepare(args.new_directory)
