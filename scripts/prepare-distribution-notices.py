"""Assemble pinned third-party materials offline. Does not import or execute any dependency."""
from pathlib import Path, PurePosixPath
import argparse, hashlib, io, json, os, posixpath, tarfile, uuid, zipfile

ROOT=Path(__file__).resolve().parents[1]
STAMP=(2026,9,29,0,0,0)

def digest(data): return hashlib.sha256(data).hexdigest()
def read_json(path): return json.loads(path.read_text(encoding='utf-8-sig'))
def encoded(value): return (json.dumps(value,ensure_ascii=False,indent=2)+'\n').encode('utf-8')
def relative(value):
    p=PurePosixPath(value)
    if not value or any(ord(c)<32 for c in value) or '\\' in value or ':' in value or p.is_absolute() or any(x in ('','.','..') or x.endswith((' ','.')) for x in value.split('/')):
        raise ValueError('Unsafe bundle path: '+value)
    return p

def legal(name):
    name=name.lower()
    return any(k in name for k in ('license','licence','copying','copyright','notice'))

def desktop_materials(manifest):
    """Select desktop components from an older combined application/firmware bundle."""
    components = [c for c in manifest['components'] if not c['id'].lower().startswith('firmware')]
    removed = set()
    for c in manifest['components']:
        if c not in components:
            removed.update(c['notices'])
            if c.get('source'):
                removed.add(c['source'])
    retained = {p for c in components for p in c['notices'] + ([c['source']] if c.get('source') else [])}
    removed.difference_update(retained)
    files = [p for p in manifest['files'] if p not in removed
             and not p.startswith(('firmware-relink/', 'metadata/firmware/', 'notices/firmware'))
             and p not in ('recipes/platformio.ini', 'metadata/base-materials.json', 'metadata/base-README.md')
             and 'firmware-relink' not in p and not p.endswith('/relink-firmware.py')]
    return components, files

def verify(directory):
    directory=Path(directory).resolve();m=read_json(directory/'materials.json')
    if m['format']!=1 or m['publicDistributionReady'] is not False: raise ValueError('Unexpected materials format/status')
    expected=m['files']; actual={p.relative_to(directory).as_posix() for p in directory.rglob('*') if p.is_file() and p!=directory/'materials.json'}
    if actual!=set(expected) or len({p.casefold() for p in expected})!=len(expected): raise ValueError('Material file set mismatch')
    for name,entry in expected.items():
        path=directory.joinpath(*relative(name).parts)
        if path.is_symlink() or not path.resolve().is_relative_to(directory): raise ValueError('Material link rejected')
        data=path.read_bytes()
        if len(data)!=entry['length'] or digest(data)!=entry['sha256']: raise ValueError('Material changed: '+name)
    for c in m['components']:
        if not c['notices'] or not all(n in expected for n in c['notices']): raise ValueError('Missing notices: '+c['id'])
        if c.get('source') and c['source'] not in expected: raise ValueError('Missing source: '+c['id'])
    return {'components':len(m['components']),'files':len(expected),'bytes':sum(e['length'] for e in expected.values()),'sha256':digest((directory/'materials.json').read_bytes()),'publicDistributionReady':False}

def binary_inputs(root):
    return {p.relative_to(root).as_posix():digest(p.read_bytes()) for p in sorted(root.rglob('*'))
            if p.is_file() and p.suffix.lower() in ('.dll','.exe','.so','.dylib')}

def verify_payload(directory,payload):
    result=verify(directory);directory=Path(directory).resolve();payload=Path(payload).resolve()
    m=read_json(directory/'materials.json');components={c['id']:c for c in m['components']}
    def check(name,expected):
        path=payload.joinpath(*relative(name).parts)
        if path.is_symlink() or not path.resolve().is_relative_to(payload) or digest(path.read_bytes())!=expected:
            raise ValueError('Payload input changed: '+name)
    python_files=m['pythonRuntimeInputs'];python_root=payload/'runtime/python'
    actual={p.relative_to(python_root).as_posix() for p in python_root.rglob('*') if p.is_file()}
    if actual!=set(python_files):raise ValueError('Python runtime file set mismatch')
    for name,value in python_files.items():check('runtime/python/'+name,value)
    deps_paths=[p for p in (payload/'FlowDeck.deps.json',payload/'FlowDeck.Service.deps.json') if p.is_file()]
    if len(deps_paths)!=1:raise ValueError('Expected one FlowDeck dependency manifest')
    deps=read_json(deps_paths[0]);targets=deps['targets'][deps['runtimeTarget']['name']];checked={}
    for identity,assets in targets.items():
        name,version=identity.rsplit('/',1)
        if name in ('FlowDeck','FlowDeck.Service','FlowDeck.Core','FlowDeck.Infrastructure','LibreHardwareMonitorLib'):continue
        component_id=('runtime/'+name.removeprefix('runtimepack.')) if name.startswith('runtimepack.') else 'nuget/'+name
        c=components.get(component_id)
        if c is None or c['version']!=version:raise ValueError('Dependency materials do not match: '+identity)
        for kind in ('runtime','native','resources'):
            for asset,metadata in assets.get(kind,{}).items():
                if asset.endswith('/_._'):continue
                relative(asset)
                source_asset=('runtimes/win-x64/'+('native/' if kind=='native' else 'lib/net10.0/')+asset) if name.startswith('runtimepack.') else asset
                value=c['packageBinaryInputs'].get(source_asset)
                if value is None:raise ValueError('Missing dependency asset evidence: '+asset)
                target=PurePosixPath(asset).name
                if kind=='resources':target=metadata['locale']+'/'+target
                check(target,value);checked[target]=value
    result.update({'payload':str(payload),'pythonFilesVerified':len(python_files),'dotnetFilesVerified':len(checked),'dependencyManifestSha256':digest(deps_paths[0].read_bytes())})
    return result

def prepare(destination):
    output=Path(destination).resolve()
    if not output.is_relative_to(ROOT) or output==ROOT or output.exists(): raise ValueError('Use a new output inside the workspace')
    lock_path=ROOT/'third-party/distribution-materials.lock.json';lock=read_json(lock_path)
    inventory_path=ROOT/'docs/汎用版_配布依存物一覧.json';inventory=read_json(inventory_path)
    if digest(inventory_path.read_bytes())!=lock['inventorySha256']: raise ValueError('Package inventory changed')
    native_lock_path=ROOT/'third-party/distribution-native-materials.lock.json';native_lock=read_json(native_lock_path)
    if native_lock['format']!=1:raise ValueError('Unexpected native input format')
    downloads=lock['downloads']+native_lock['downloads'];cache={}
    for d in downloads:
        p=ROOT.joinpath(*relative(d['cachePath']).parts);data=p.read_bytes()
        if len(data)!=d['length'] or digest(data)!=d['sha256']: raise ValueError('Download changed: '+d['file'])
        cache[d['file']]=data
    temp=output.with_name(output.name+'.preparing-'+uuid.uuid4().hex)
    if not temp.resolve().is_relative_to(ROOT): raise ValueError('Temporary output escapes workspace')
    temp.mkdir(parents=True);files={};components=[]
    def put(name,data):
        p=relative(name)
        if name.casefold() in {n.casefold() for n in files}: raise ValueError('Duplicate material: '+name)
        dest=temp.joinpath(*p.parts);dest.parent.mkdir(parents=True,exist_ok=True);dest.write_bytes(data)
        files[name]={'length':len(data),'sha256':digest(data)};return name
    def local(name,path,expected=None):
        data=path.read_bytes()
        if expected and digest(data)!=expected: raise ValueError('Local input changed: '+str(path))
        return put(name,data)
    def archive_notices(c,download):
        data=cache[download['file']];c['source']=put('sources/'+download['file'],data);c['sourceProvenance']=download
        if download['file'].endswith('.zip'):
            with zipfile.ZipFile(io.BytesIO(data)) as archive:
                for n in archive.namelist():
                    if n.endswith('/'): continue
                    relative(n)
                    if legal(PurePosixPath(n).name): c['notices'].append(put('notices/'+c['id']+'/upstream/'+n.split('/',1)[-1],archive.read(n)))
        else:
            with tarfile.open(fileobj=io.BytesIO(data),mode='r:*') as archive:
                for member in archive.getmembers():
                    if not member.isfile():continue
                    n=member.name;relative(n)
                    if legal(PurePosixPath(n).name):c['notices'].append(put('notices/'+c['id']+'/upstream/'+n.split('/',1)[-1],archive.extractfile(member).read()))
    for item in inventory['nugetResolvedPackages']:
        c={'id':'nuget/'+item['name'],'version':item['version'],'licenseDeclaration':item['declaredLicense'],'notices':[], 'package':item}
        root=Path.home()/'.nuget/packages'/item['name'].lower()/item['version']
        spec=next(root.glob('*.nuspec'));local('metadata/nuget/'+spec.name,spec,item['nuspecSha256'])
        c['packageBinaryInputs']=binary_inputs(root)
        metadata=read_json(root/'.nupkg.metadata')
        if metadata['contentHash']!=item['contentHash']: raise ValueError('NuGet content identity changed')
        for p in root.iterdir():
            if p.is_file() and legal(p.name):c['notices'].append(local('notices/'+c['id']+'/'+p.name,p))
        if item['declaredLicense']=='MPL-2.0':archive_notices(c,next(d for d in downloads if d.get('id')==item['name']))
        elif item['declaredLicense']=='MIT' and not any('license' in p.lower() for p in c['notices']):
            commit=item['repository']['commit'];d=next(d for d in downloads if d.get('id')=='dotnet-license' and d['revision']==commit)
            c['notices'].append(put('notices/'+c['id']+'/LICENSE.TXT',cache[d['file']]))
        elif item['name']=='Mono.Posix.NETStandard':
            d=next(d for d in downloads if d.get('id')==item['name']);c['noticeProvenance']=d
            c['notices'].append(put('notices/'+c['id']+'/LICENSE',cache[d['file']]))
        components.append(c)
    for name in ('Microsoft.NETCore.App.Runtime.win-x64','Microsoft.WindowsDesktop.App.Runtime.win-x64'):
        root=Path.home()/'.nuget/packages'/name.lower()/'10.0.6';c={'id':'runtime/'+name,'version':'10.0.6','notices':[]}
        for p in root.iterdir():
            if p.is_file() and legal(p.name):c['notices'].append(local('notices/'+c['id']+'/'+p.name,p))
        spec=next(root.glob('*.nuspec'));local('metadata/runtime/'+spec.name,spec);c['packageBinaryInputs']=binary_inputs(root);components.append(c)
    pyroot=ROOT/'.tools/python313';site=pyroot/'Lib/site-packages'
    components.append({'id':'runtime/Python','version':'3.13.12','notices':[local('notices/runtime/Python/LICENSE.txt',pyroot/'LICENSE.txt')]})
    for item in inventory['python']['distributions']:
        c={'id':'python/'+item['name'],'version':item['version'],'licenseDeclaration':item['declaredLicense'],'notices':[]}
        local('metadata/python/'+item['name']+'-METADATA',ROOT/'.tools/python313'/item['metadataPath'].removeprefix('runtime/python/'),item['metadataSha256'])
        for path,hashvalue in item['licenseFiles'].items():c['notices'].append(local('notices/'+c['id']+'/'+path,site/path,hashvalue))
        d=next(d for d in downloads if d.get('id')==item['name'] and d.get('version')==item['version'])
        c['source']=put('sources/'+d['file'],cache[d['file']]);c['sourceProvenance']=d
        if d.get('kind')=='python-wheel-source-unavailable':
            c['sourceStatus']='Published wheel retained; upstream build source is still to be collected'
            with zipfile.ZipFile(io.BytesIO(cache[d['file']])) as z:
                c['wheelInstalledFilesCompared']=0
                for n in z.namelist():
                    relative(n);installed=site/n
                    if n.endswith('/') or not installed.is_file() or n.endswith('/RECORD'):continue
                    if installed.read_bytes()!=z.read(n):raise ValueError('Installed wheel differs: '+n)
                    c['wheelInstalledFilesCompared']+=1
        components.append(c)
    # Supplement the two published wheels with their tagged build sources.
    for name,subdir,count in [('WinUsbCDC','winusbcdc',8),('libusb-package','src/libusb_package',4)]:
        c=next(x for x in components if x['id']=='python/'+name)
        c['publishedWheel']=c.pop('source');c['publishedWheelProvenance']=c.pop('sourceProvenance')
        d=next(x for x in downloads if x['id']==name+'-upstream');archive_notices(c,d)
        c['sourceStatus']='Tagged upstream build source retained; original wheel and exact installed-file evidence also retained'
        c['sourceFileComparisons']=[]
        with zipfile.ZipFile(io.BytesIO(cache[d['file']])) as archive:
            for n in archive.namelist():
                relative(n.rstrip('/'))
                marker='/'+subdir+'/'
                if marker not in n or n.endswith('/'):continue
                rel=subdir.split('/')[-1]+'/'+n.split(marker,1)[1];installed=site/rel
                if not installed.is_file():continue
                a=archive.read(n);b=installed.read_bytes()
                if a!=b and a.replace(b'\r\n',b'\n')!=b.replace(b'\r\n',b'\n'):raise ValueError('Upstream Python source differs: '+rel)
                c['sourceFileComparisons'].append({'path':rel,'sourceSha256':digest(a),'installedSha256':digest(b),'exact':a==b,'onlyLineEndingsDiffer':a!=b})
        if len(c['sourceFileComparisons'])!=count:raise ValueError('Incomplete upstream Python comparison: '+name)
    library_root=ROOT/'artifacts/openrgb-flowdeck-source-patch8/dependencies'
    c={'id':'optional/libusb','version':'1.0.27','notices':[],'optionalOpenRgb':True}
    d=next(x for x in downloads if x['id']=='libusb-openrgb-source');archive_notices(c,d)
    binary=(library_root/'libusb-1.0.27/VS2019/MS64/dll/libusb-1.0.dll').read_bytes();expected=native_lock['libusbOfficialBinary']
    if digest(binary)!=expected['sha256'] or len(binary)!=expected['length']:raise ValueError('OpenRGB libusb binary changed')
    c['officialBinaryComparison']=expected
    d=next(x for x in downloads if x['id']=='libusb-openrgb-release-binaries');c['referenceBinaryArchive']=put('reference-archives/'+d['file'],cache[d['file']]);c['referenceBinaryArchiveProvenance']=d
    with tarfile.open(fileobj=io.BytesIO(cache[c['sourceProvenance']['file']]),mode='r:*') as archive:
        header=archive.extractfile('libusb-1.0.27/libusb/libusb.h').read()
        if header!=(library_root/'libusb-1.0.27/include/libusb.h').read_bytes():raise ValueError('libusb header/source mismatch')
        c['matchedHeaderSha256']=digest(header)
    components.append(c)
    c={'id':'native/libusb-python','version':'1.0.30','notices':[]}
    d=next(x for x in downloads if x['id']=='libusb-python-native');archive_notices(c,d)
    tree=read_json(ROOT/'artifacts/native-dependency-research/libusb-package-tree.json')
    gitlink=next(x for x in tree['tree'] if x['path']=='src/libusb')
    if gitlink['mode']!='160000' or gitlink['sha']!=d['revision']:raise ValueError('libusb Python source submodule mismatch')
    c['submoduleEvidence']={'parentRevision':'3ea4db141ef013c070f28738cae6ee8ed85688be','path':'src/libusb','revision':gitlink['sha']};components.append(c)
    c={'id':'optional/hidapi-hotplug','version':'0.15.0','notices':[],'optionalOpenRgb':True}
    d=next(x for x in downloads if x['id']=='hidapi-hotplug-upstream');archive_notices(c,d)
    tag=read_json(ROOT/'artifacts/native-dependency-research/hidapi-hotplug-tag.json')
    if tag['name']!='hidapi-hotplug-0.15.0' or tag['commit']['id']!=d['revision']:raise ValueError('hidapi source tag mismatch')
    c['upstreamOpenRgbAdoptionRevision']=native_lock['hidapiHotplugAdoptionRevision'];c['sourceCorrespondence']='Upstream OpenRGB explicitly selected this source tag; both public headers match; binary rebuild not claimed'
    c['matchedHeaderHashes']={}
    with zipfile.ZipFile(io.BytesIO(cache[d['file']])) as archive:
        for source_name,target_name in [('hidapi-hotplug/hidapi.h','hidapi.h'),('windows/hidapi_winapi.h','hidapi_winapi.h')]:
            n=next(n for n in archive.namelist() if n.endswith('/'+source_name));data=archive.read(n)
            if data!=(library_root/'hidapi-hotplug-win/include'/target_name).read_bytes():raise ValueError('hidapi header/source mismatch')
            c['matchedHeaderHashes'][target_name]=digest(data)
    c['distributedBinarySha256']=digest((library_root/'hidapi-hotplug-win/x64/hidapi-hotplug.dll').read_bytes());components.append(c)
    for item in native_lock['metadata']:local('metadata/native/'+item['path'],ROOT.joinpath(*relative(item['cachePath']).parts),item['sha256'])
    put('metadata/native-input-lock.json',native_lock_path.read_bytes())
    qt=ROOT/'.tools/qt-6.8.3/6.8.3/msvc2022_64'
    for name in ('qtbase','qtsvg'):
        d=next(d for d in downloads if d.get('id')==name);data=cache[d['file']]
        sbom=read_json(qt/'sbom'/f'{name}-6.8.3.spdx.json');repo=next(p for p in sbom['packages'] if p['name']==name)['downloadLocation'];commit=repo.split('@')[-1]
        c={'id':'optional/'+name,'version':'6.8.3','notices':[],'revision':commit,'optionalOpenRgb':True,'source':put('sources/'+d['file'],data),'sourceProvenance':d}
        local('metadata/qt/'+name+'-6.8.3.spdx.json',qt/'sbom'/f'{name}-6.8.3.spdx.json')
        with tarfile.open(fileobj=io.BytesIO(data),mode='r:xz') as archive:
            allfiles={m.name:m for m in archive.getmembers() if m.isfile()};prefix=name+'-everywhere-src-6.8.3/'
            if archive.extractfile(allfiles[prefix+'.tag']).read().decode().strip()!=commit:raise ValueError('Qt source/SBOM commit mismatch')
            required={n for n in allfiles if legal(PurePosixPath(n).name) or '/LICENSES/' in n}
            attributions=[]
            for n in allfiles:
                if not n.endswith('/qt_attribution.json'):continue
                # Six official qtbase attribution files contain literal newlines in string values.
                # Retain the source bytes and accept JSON string control characters only here.
                doc=json.loads(archive.extractfile(allfiles[n]).read(),strict=False);entries=doc if isinstance(doc,list) else [doc]
                for entry in entries:
                    if not isinstance(entry,dict):raise ValueError('Invalid Qt attribution entry: '+n)
                    attributions.append({'archivePath':n,**entry})
                    value=entry.get('LicenseFile');paths=value if isinstance(value,list) else [value] if value else []
                    for part in paths:
                        if not isinstance(part,str) or not part:raise ValueError('Invalid Qt license reference: '+n)
                        path=posixpath.normpath(posixpath.join(posixpath.dirname(n),part));relative(path)
                        if not path.startswith(prefix) or path not in allfiles:raise ValueError('Qt attribution license missing: '+path)
                        required.add(path)
                required.add(n)
            c['attributions']=put('metadata/qt/'+name+'-attributions.json',encoded(attributions))
            for n in sorted(required):c['notices'].append(put('notices/'+c['id']+'/'+n.removeprefix(prefix),archive.extractfile(allfiles[n]).read()))
        components.append(c)
    put('metadata/input-lock.json',lock_path.read_bytes());put('metadata/package-inventory.json',inventory_path.read_bytes())
    local('recipes/prepare-distribution-notices.py',Path(__file__));local('recipes/bootstrap-python.ps1',ROOT/'scripts/bootstrap-python.ps1');local('recipes/requirements.lock',ROOT/'worker/requirements.lock')
    pending=['PawnIOLib source revision matched to the distributed binary (candidate source retained separately for investigation)',
        'MSVC runtime redistribution material matched to compiler runtime',
        'Integration into the next package and verification against its actual runtime files']
    lines=['# Third-party distribution materials','','Fixed inputs for FlowDeck development packages. These materials do not mark public distribution or hardware acceptance complete.','',
        'Full source archives are retained in sources/. The two Python wheels are also kept as build evidence, and native libusb is stored separately from its Python wrapper. Extracted notices retain their original relative paths. Qt includes notices for the full source modules, including components not used by the helper.','',
        'Mono.Posix.NETStandard uses a pinned snapshot of the license URL supplied by its NuGet package. Its binary source revision is not inferred from that license snapshot.','',
        '| Component | Version | Source material |','| --- | --- | --- |']
    for c in components:lines.append('| '+c['id']+' | '+c['version']+' | '+c.get('source','Not included / see provenance')+' |')
    lines+=['','## Still pending','']+['- '+p for p in pending]
    put('README.md',('\n'.join(lines)+'\n').encode('utf-8'))
    manifest={'format':1,'publicDistributionReady':False,'hardwareExecuted':False,'inputLockSha256':digest(lock_path.read_bytes()),'nativeInputLockSha256':digest(native_lock_path.read_bytes()),'pythonRuntimeInputs':{p.relative_to(pyroot).as_posix():digest(p.read_bytes()) for p in sorted(pyroot.rglob('*')) if p.is_file()},'components':components,'remaining':pending,'files':files}
    (temp/'materials.json').write_bytes(encoded(manifest));result=verify(temp)
    # Both resolved paths are checked above; no existing output is replaced or deleted.
    if output.exists() or not temp.resolve().is_relative_to(ROOT) or not output.resolve().is_relative_to(ROOT):raise ValueError('Output changed during preparation')
    temp.rename(output);return result

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('directory');parser.add_argument('--verify',action='store_true');parser.add_argument('--verify-payload');args=parser.parse_args()
    print(json.dumps(verify_payload(args.directory,args.verify_payload) if args.verify_payload else verify(args.directory) if args.verify else prepare(args.directory),ensure_ascii=False))
