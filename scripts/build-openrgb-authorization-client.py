"""Compile the real native authorization channel with mock HID for cross-process .NET tests."""
import argparse
import importlib.util
import json
import shutil
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def module(name, file):
    spec = importlib.util.spec_from_file_location(name, ROOT/'scripts'/file)
    value = importlib.util.module_from_spec(spec); spec.loader.exec_module(value); return value


def build(source, output):
    source, output = Path(source).resolve(), Path(output).resolve()
    prepare = module('prepare','prepare-openrgb-source.py'); pinned=prepare.verify(source)
    if pinned['flowDeckPatch'] != 5 or output.exists(): raise ValueError('Need patch 5 and a new output directory')
    output.mkdir(parents=True)
    inputs={}
    for name in ['FlowDeckAsrockAuthorization.h','FlowDeckAsrockIo.h']:
        inputs[name]=source/'Controllers/ASRockPolychromeUSBController'/name
    for name in ['FlowDeckAsrockWire.h','FlowDeckAsrockPipe.h','FlowDeckAsrockBootstrap.h']:
        inputs[name]=ROOT/'native/OpenRgb'/name
    inputs['client.cpp']=ROOT/'tests/native/openrgb-authorization-pipe/client.cpp'
    for name,path in inputs.items(): shutil.copyfile(path,output/name)
    env=module('builder','build-openrgb-source.py').environment(ROOT/'.tools/qt-6.8.3/6.8.3/msvc2022_64')
    compiler=Path(env['VCTOOLSINSTALLDIR'])/'bin/Hostx64/x64/cl.exe'
    command=[str(compiler),'/nologo','/std:c++17','/EHsc','/W4','/WX','/utf-8','/permissive-','/MT','/O2',
             '/I'+str(source/'dependencies/json'),'client.cpp','/Fe:authorization-client.exe']
    compiled=subprocess.run(command,cwd=output,env=env,capture_output=True,text=True,encoding='utf-8',errors='replace')
    (output/'build.txt').write_text(compiled.stdout+compiled.stderr,encoding='utf-8')
    if compiled.returncode: raise RuntimeError('Authorization client build failed; see '+str(output/'build.txt'))
    receipt={'sourceRevision':pinned['sourceRevision'],'basePatch':5,'hardwareMocked':True,'actualWindowsPipeClient':True,
             'binarySha256':prepare.sha256((output/'authorization-client.exe').read_bytes()),
             'inputs':[{'path':str(path.relative_to(ROOT)),'sha256':prepare.sha256(path.read_bytes())} for path in inputs.values()]}
    (output/'provenance.json').write_text(json.dumps(receipt,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(receipt))


if __name__ == '__main__':
    parser=argparse.ArgumentParser(description=__doc__); parser.add_argument('prepared_source'); parser.add_argument('new_directory')
    args=parser.parse_args(); build(args.prepared_source,args.new_directory)
