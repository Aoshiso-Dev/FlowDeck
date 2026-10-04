"""Prepare a pinned local helper archive from a verified build. Never execute OpenRGB or install drivers."""
import argparse
import hashlib
import importlib.util
import io
import json
import re
import struct
import subprocess
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
EXPECTED_EXE = '5E8D474C84D2AEFA1EEB0194F5DFD1DB42F5E7292C5EA1EE5600140E06AE43CD'
PREFIX = 'FlowDeck-OpenRGB/'
DATE = (2026, 9, 28, 0, 0, 0)


def sha(data):
    return hashlib.sha256(data).hexdigest().upper()


def json_bytes(value):
    return (json.dumps(value, indent=2, ensure_ascii=False)+'\n').encode('utf-8')


def zip_bytes(files, prefix=''):
    output = io.BytesIO()
    with zipfile.ZipFile(output, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for path, data in sorted(files.items()):
            info = zipfile.ZipInfo(prefix+path, DATE); info.compress_type=zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            archive.writestr(info, data)
    return output.getvalue()


def prepare(source, build, destination, stable_build=False):
    source, build, destination = map(lambda p: Path(p).resolve(), (source, build, destination))
    if destination.exists(): raise ValueError('Output must be new')
    module_spec=importlib.util.spec_from_file_location('prepare',ROOT/'scripts/prepare-openrgb-source.py')
    module=importlib.util.module_from_spec(module_spec); module_spec.loader.exec_module(module)
    source_record=module.verify(source)
    record=json.loads((build/'build-result.json').read_text(encoding='utf-8'))
    expected_exe = EXPECTED_EXE
    archive_name = 'FlowDeck-OpenRGB-1.0-patch8-win-x64.zip'
    if stable_build:
        if source_record.get('buildVariant') != 'stable1' or record.get('toolchain', {}).get('prerelease') is not False or \
           record['toolchain']['compilerSha256'] != '9BEEC04038C74406E4C055593EDC07DDDA7B166272D77CBF85507D5A6BE29FF0':
            raise ValueError('Not the verified stable compiler/source variant')
        expected_exe = record['binarySha256']
        archive_name = 'FlowDeck-OpenRGB-1.0-patch8-stable1-win-x64.zip'
    if source_record['flowDeckPatch'] != 8 or not record['succeeded'] or record['flowDeckPatch'] != 8 or record['openRgbExecuted'] or \
       Path(record['sourceDirectory']).resolve() != source or record['sourceManifestSha256'] != sha((source/'flowdeck-source.json').read_bytes()) or \
       sha((build/'release/OpenRGB.exe').read_bytes()) != expected_exe or record['binarySha256'] != expected_exe:
        raise ValueError('Not the verified patch 8 helper build')
    qt=ROOT/'.tools/qt-6.8.3/6.8.3/msvc2022_64'
    qt_receipt=json.loads((ROOT/'artifacts/qt-6.8.3-archives/prepared-sdk.json').read_text(encoding='utf-8'))
    archives={r['module']:r for r in qt_receipt['archives']}
    for r in archives.values():
        if sha((ROOT/r['archive']).read_bytes()) != r['sha256']: raise ValueError('Qt archive changed')
    vc=Path(r'C:\Program Files\Microsoft Visual Studio\18\Insiders\VC')
    redist=vc/'Redist/MSVC/14.51.36231/x64/Microsoft.VC145.CRT'
    dumpbin=vc/'Tools/MSVC/14.51.36231/bin/Hostx64/x64/dumpbin.exe'
    redist_record = None
    if stable_build:
        redist_base = ROOT / '.tools/msvc-14.44-stable-redist'
        redist = redist_base / 'VC/Redist/MSVC/14.44.35112/x64/Microsoft.VC143.CRT'
        redist_record = json.loads((redist_base / 'preparation.json').read_text(encoding='utf-8'))
        if redist_record['source']['sha256'] != '4aaf54db0bfc9435f7c3660e1a00237a4b556042bfeea64bde44c2e0194e6ee5':
            raise ValueError('Stable redistributable provenance changed')
        redist_archive = ROOT/'artifacts/resume-20260929/research/msvc-stable'/redist_record['source']['fileName']
        if sha(redist_archive.read_bytes()).lower() != redist_record['source']['sha256']:
            raise ValueError('Official redistributable archive changed')
        dumpbin = Path(record['toolchain']['compilerDirectory']) / 'dumpbin.exe'
    system=Path(r'C:\Windows\System32')
    if not redist.is_dir() or not dumpbin.is_file(): raise ValueError('Configured compiler runtime is unavailable')
    payload={}; inputs=[]; imports={}; pending=[]; loaded={}; system_imports=set()

    def add(path, original, qt_module=None, source_dependency=None):
        original=Path(original); data=original.read_bytes()
        if qt_module:
            entry=str(original.relative_to(qt)).replace('\\','/')
            archived=subprocess.run(['tar.exe','-xOf',str(ROOT/archives[qt_module]['archive']),entry],capture_output=True,check=True).stdout
            if data != archived: raise ValueError('Qt SDK file differs from its verified archive: '+entry)
        if source_dependency and data != (source/source_dependency).read_bytes(): raise ValueError('Copied native dependency changed')
        if path in payload: raise ValueError('Duplicate payload file: '+path)
        if data[:2] != b'MZ': raise ValueError('Not a Windows image: '+path)
        pe=struct.unpack_from('<I',data,0x3c)[0]
        if data[pe:pe+4] != b'PE\0\0' or struct.unpack_from('<H',data,pe+4)[0] != 0x8664: raise ValueError('Not x64: '+path)
        payload[path]=data; loaded[original.name.casefold()]=path; pending.append((path,original))
        inputs.append({'path':path,'source':str(original),'length':len(data),'sha256':sha(data)})

    add('OpenRGB.exe',build/'release/OpenRGB.exe')
    native={
        'libusb-1.0.dll':'dependencies/libusb-1.0.27/VS2019/MS64/dll/libusb-1.0.dll',
        'hidapi-hotplug.dll':'dependencies/hidapi-hotplug-win/x64/hidapi-hotplug.dll',
        'pawniolib.dll':'dependencies/PawnIO/PawnIOLib.dll'}
    qt_plugins=['platforms/qwindows.dll','imageformats/qgif.dll','imageformats/qico.dll','imageformats/qjpeg.dll',
                'imageformats/qsvg.dll','iconengines/qsvgicon.dll']
    for name in qt_plugins:
        add(name,qt/'plugins'/name,'qtsvg' if 'svg' in name else 'qtbase')
    redist_files={p.name.casefold():p for p in redist.glob('*.dll')}
    qt_files={p.name.casefold():p for p in (qt/'bin').glob('*.dll')}
    while pending:
        name,path=pending.pop(0)
        result=subprocess.run([str(dumpbin),'/nologo','/dependents',str(path)],capture_output=True,text=True,encoding='utf-8',errors='replace',check=True)
        dependencies=re.findall(r'^\s+([A-Za-z0-9_.-]+\.dll)\s*$',result.stdout,re.MULTILINE|re.IGNORECASE)
        if not dependencies: raise ValueError('No static dependency information: '+name)
        imports[name]=dependencies
        for dependency in dependencies:
            key=dependency.casefold()
            if key in loaded: continue
            if key in native:
                original=source/native[key]; add(original.name,build/'release'/original.name,source_dependency=native[key])
            elif key in qt_files: add(qt_files[key].name,qt_files[key],'qtsvg' if key.startswith('qt6svg') else 'qtbase')
            elif key in redist_files:
                if redist_record and sha(redist_files[key].read_bytes()).lower() != redist_record['files'][redist_files[key].relative_to(redist_base).as_posix()]:
                    raise ValueError('Stable redistributable file changed')
                if redist_record:
                    with zipfile.ZipFile(redist_archive) as original_archive:
                        if original_archive.read('Contents/' + redist_files[key].relative_to(redist_base).as_posix()) != redist_files[key].read_bytes():
                            raise ValueError('Redistributable differs from official archive')
                add(redist_files[key].name,redist_files[key])
            elif key.startswith(('api-ms-win-','ext-ms-win-')) or (system/dependency).is_file(): system_imports.add(dependency)
            else: raise ValueError('Unresolved dependency: '+name+' -> '+dependency)
    if any(n.lower().endswith(('.bin','.sys')) for n in payload): raise ValueError('Kernel module in helper runtime')
    # Preserve complete patched source and the exact preparation recipe with the private artifact.
    source_files={f['path']:(source/f['path']).read_bytes() for f in source_record['files']}
    for name in ['flowdeck-source.json','flowdeck.patch']: source_files[name]=(source/name).read_bytes()
    payload['source/OpenRGB-patch8.zip']=zip_bytes(source_files,'OpenRGB-patch8/')
    payload['LICENSE-OpenRGB.txt']=(source/'LICENSE').read_bytes()
    payload['metadata/build-result.json']=(build/'build-result.json').read_bytes()
    payload['metadata/qt-sdk.json']=json_bytes(qt_receipt)
    if stable_build:
        pawn_archive = ROOT / 'artifacts/distribution-source-downloads/PawnIOLib-aef64eb8810d93791dd24d8c318db2e3b119b264.zip'
        if sha(pawn_archive.read_bytes()).lower() != source_record['pawnIoBuild']['sourceArchiveSha256']:
            raise ValueError('Corresponding PawnIOLib source changed')
        payload['source/PawnIOLib-aef64eb8.zip'] = pawn_archive.read_bytes()
        payload['metadata/pawniolib-build.json'] = json_bytes(source_record['pawnIoBuild'])
        payload['metadata/msvc-runtime.json'] = json_bytes(redist_record)
        payload['metadata/compiler-response-files.json'] = (build/'makefile-response-files.json').read_bytes()
        for name in ['build-pawniolib.py', 'msvc_environment.py', 'prepare-openrgb-stable-source.py', 'build-openrgb-source.py']:
            payload['source/recipes/' + name] = (ROOT/'scripts'/name).read_bytes()
    for component in ['qtbase','qtsvg']:
        payload['metadata/'+component+'-6.8.3.spdx.json']=(qt/'sbom'/f'{component}-6.8.3.spdx.json').read_bytes()
    provenance={'sourceRevision':record['sourceRevision'],'sourcePatch':8,'authorizationVersion':1,'runtimeInputs':inputs,
                'staticImports':imports,'systemImports':sorted(system_imports),'kernelModulesIncluded':False,
                'openRgbExecuted':False,'hardwareAccessed':False,'uacRequested':False,'publicDistributionReady':False}
    if stable_build:
        provenance.update(buildVariant='stable1', pawnIoSourceRevision=source_record['pawnIoBuild']['sourceRevision'],
                          compiler=record['toolchain'], redistributableVersion='14.44.35211')
    payload['metadata/runtime-inputs.json']=json_bytes(provenance)
    archive=zip_bytes(payload,PREFIX)
    manifest={'version':1,'id':'flowdeck-openrgb-1.0-patch8-windows-x64-usb','release':'1.0-flowdeck-patch8',
              'sourceRevision':record['sourceRevision'],'archiveUrl':'local:'+archive_name,
              'archiveSha256':sha(archive),'archivePrefix':PREFIX,'authorizationVersion':1,'sourcePatch':8,
              'files':[{'path':p,'length':len(data),'sha256':sha(data),'include':True} for p,data in sorted(payload.items())]}
    if len(manifest['files']) > 128 or len(archive) > 256*1024*1024: raise ValueError('Package exceeds runtime limits')
    destination.mkdir(parents=True)
    (destination/archive_name).write_bytes(archive)
    (destination/'openrgb-helper-runtime.json').write_bytes(json_bytes(manifest))
    (destination/'runtime-inputs.json').write_bytes(json_bytes(provenance))
    print(json.dumps({'files':len(payload),'zipLength':len(archive),'archiveSha256':sha(archive),
                      'exeSha256':expected_exe,'privateImages':len(inputs),'staticDependencyClosureChecked':True,'openRgbExecuted':False}))


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__); parser.add_argument('prepared_source'); parser.add_argument('verified_build'); parser.add_argument('new_directory')
    parser.add_argument('--stable-build', action='store_true')
    args=parser.parse_args(); prepare(args.prepared_source,args.verified_build,args.new_directory,args.stable_build)
