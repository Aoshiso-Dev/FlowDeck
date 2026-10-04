"""Compile actual RGB base/native/ASRock implementation against mocked OS collaborators."""
import argparse
import importlib.util
import json
import re
import shutil
import subprocess
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DIR = 'Controllers/ASRockPolychromeUSBController/'


def module(name, file):
    spec = importlib.util.spec_from_file_location(name, ROOT / 'scripts' / file)
    value = importlib.util.module_from_spec(spec); spec.loader.exec_module(value); return value


def run(source, output):
    source, output = Path(source).resolve(), Path(output).resolve()
    prepare = module('prepare', 'prepare-openrgb-source.py'); receipt = prepare.verify(source)
    if receipt['flowDeckPatch'] not in (3, 4, 5) or output.exists(): raise ValueError('Need patch 3/4/5 and a new test directory')
    output.mkdir(parents=True)
    files = [DIR + n for n in ['ASRockPolychromeUSBController.cpp','ASRockPolychromeUSBController.h',
              'RGBController_ASRockPolychromeUSB.cpp','RGBController_ASRockPolychromeUSB.h','FlowDeckAsrockIo.h','FlowDeckAsrockOperation.h']]
    files += ['RGBController/RGBController.cpp','RGBController/RGBController.h','RGBController/RGBControllerInterface.h','NetworkProtocol.h','FlowDeckAsrockSdk.inc']
    if receipt['flowDeckPatch'] >= 5:
        files += [DIR+'FlowDeckAsrockAuthorization.h','FlowDeckAsrockSdkPeer.inc',DIR+'ASRockPolychromeUSBControllerDetect.cpp',DIR+'FlowDeckHidAllowlist.h']
        header = output / (DIR+'FlowDeckAsrockAuthorization.h'); header.parent.mkdir(parents=True)
        header.write_text('#pragma once\n#include "../../FlowDeckAsrockAuthorization.h"\n',encoding='utf-8')
        for name in ['authorization.inc','socket-fixture.h','detection-authorization.inc']:
            shutil.copyfile(ROOT/'tests/native/openrgb-operation'/name,output/name)
        shutil.copyfile(ROOT/'native/OpenRgb/FlowDeckAsrockWire.h',output/'FlowDeckAsrockWire.h')
    for file in files: shutil.copyfile(source / file, output / Path(file).name)
    fixture = (ROOT / 'tests/native/openrgb-io/fixture.h').read_text(encoding='utf-8')
    start = fixture.index('using RGBColor ='); end = fixture.index('struct hid_device')
    fixture = fixture[:start] + '#include "RGBController.h"\n' + fixture[end:]
    fixture = fixture.replace('class RGBController { public: virtual ~RGBController() = default; };\n','')
    fixture = fixture.replace('namespace StringUtils {', 'namespace StringUtils { inline std::string remove_null_terminating_chars(std::string value) { value.erase(std::remove(value.begin(), value.end(), char(0)), value.end()); return value; }')
    (output / 'fixture.h').write_text(fixture, encoding='utf-8')
    for name in ['hidapi.h','SettingsManager.h','ResourceManager.h','dmiinfo.h','StringUtils.h','DetectionManager.h']:
        (output / name).write_text('#pragma once\n#include "fixture.h"\n', encoding='utf-8')
    (output / 'LogManager.h').write_text('#pragma once\n#define LOG_ERROR(...) ((void)0)\n', encoding='utf-8')
    shutil.copyfile(ROOT / 'tests/native/openrgb-operation/operation.cpp', output / 'operation.cpp')
    shutil.copyfile(ROOT / 'tests/native/openrgb-operation/sdk-fixture.h', output / 'sdk-fixture.h')
    patcher=module('patcher','prepare-openrgb-operation-source.py')
    server=(source/'NetworkServer.cpp').read_text(encoding='utf-8')
    macros=server[server.index('#define COPY_DATA_FIELD('):server.index('NetworkClientInfo::NetworkClientInfo()')]
    functions=[]
    for name,parameters in [('UpdateLEDs','unsigned int controller_id'),('UpdateZoneLEDs','unsigned int controller_id'),('UpdateSingleLED','unsigned int controller_id'),('UpdateSaveMode','unsigned int controller_id, bool save_mode')]:
        signature='NetPacketStatus NetworkServer::ProcessRequest_RGBController_'+name+'(NetworkClientInfo* client_info, unsigned int data_size, unsigned char* data_ptr, '+parameters+')'
        functions.append(patcher.function(server,signature))
    protocol=(source/'NetworkProtocol.cpp').read_text(encoding='utf-8')
    start=protocol.index('void InitNetPacketHeader'); end=protocol.index('\n}',start)+2
    functions.append(protocol[start:end])
    # Function bodies/macros are copied without edits; the complete network server is linked in the full helper build.
    extracted=macros+'\n'+'\n\n'.join(functions)
    (output/'sdk-functions.inc').write_text(extracted,encoding='utf-8')
    env = module('builder', 'build-openrgb-source.py').environment(ROOT / '.tools/qt-6.8.3/6.8.3/msvc2022_64')
    compiler = Path(env['VCTOOLSINSTALLDIR']) / 'bin/Hostx64/x64/cl.exe'
    command = [str(compiler), '/nologo', '/std:c++17', '/EHsc', '/W4', '/utf-8', '/permissive-', '/DFLOWDECK_PATCH='+str(receipt['flowDeckPatch']), '/I' + str(output), '/I' + str(source/'dependencies/json'),
               'ASRockPolychromeUSBController.cpp','RGBController_ASRockPolychromeUSB.cpp','RGBController.cpp','operation.cpp',
               *(['ASRockPolychromeUSBControllerDetect.cpp'] if receipt['flowDeckPatch'] >= 5 else []),'/Fe:operation-test.exe']
    compiled = subprocess.run(command, cwd=output, env=env, capture_output=True, text=True, encoding='utf-8', errors='replace')
    (output/'build.txt').write_text(compiled.stdout+compiled.stderr,encoding='utf-8')
    if compiled.returncode: raise RuntimeError('Compilation failed; see '+str(output/'build.txt'))
    warnings=dict(Counter(re.findall(r'warning (C\d+)',compiled.stdout+compiled.stderr)))
    # Adding the real detector adds one translation unit including the same upstream matrix
    # header; its three shadowed constructor parameters are not new FlowDeck diagnostics.
    if receipt['flowDeckPatch'] >= 5:
        repeated = [line for line in compiled.stdout.splitlines() if 'warning C4458' in line and 'ASRockPolychromeUSBControllerDetect.cpp' in line]
        if len(repeated) != 3 or any('RGBControllerInterface.h(275)' not in line for line in repeated):
            raise RuntimeError('Unexpected detector compilation warning')
    if warnings != {'C4458':28 if receipt['flowDeckPatch'] >= 5 else 25,'C4244':11,'C4996':14}:
        raise RuntimeError('Unexpected compilation warnings: '+str(warnings))
    cases=['synchronous-mode','zero-led-first-zone','synchronous-zone','synchronous-single','running-receipt','serialized-operations',
           'partial-direct-frame','failed-mode-1','failed-mode-2','failed-mode-3','invalid-mode','invalid-zone','invalid-led','resize-denied']
    cases += ['sdk-'+kind+'-'+result for kind in ['mode','colors','zone','single'] for result in ['success','failure']]
    cases += ['sdk-status-'+kind for kind in ['fragmented','version','size','id','send-failure']]
    cases += ['sdk-command-admission']
    if receipt['flowDeckPatch'] >= 4:
        cases += ['initial-baseline','single-zone-mode','direct-effective-order','direct-empty-first','direct-zone','direct-single',
                  'direct-empty-target','restored-order','out-of-band-io','native-receipt-wire']
    if receipt['flowDeckPatch'] >= 5:
        cases += ['auth-sdk-'+kind+'-'+result for kind in ['mode','colors','zone','single'] for result in ['success','wrong-peer','write','read','reply']]
        cases += ['auth-peer-'+kind for kind in ['get-fails','local-fails','ipv6','remote-address','local-address','short-peer','short-local','zero-peer','zero-local','protocol','save']]
        cases += ['auth-partial-direct']
        cases += ['startup-normal','startup-empty','startup-unavailable']
    results=[]
    for case in cases:
        r=subprocess.run([str(output/'operation-test.exe'),case],cwd=output,capture_output=True,text=True,timeout=20)
        results.append({'case':case,'exitCode':r.returncode,'output':r.stdout+r.stderr})
    authorization_receipts=[]; detection_traces=[]
    for r in results:
        for line in r['output'].splitlines():
            if line.startswith('WIRE '): (output/'native-receipts.json').write_text(json.dumps(json.loads(line[5:]),indent=2)+'\n',encoding='utf-8')
            if line.startswith('AUTHWIRE '): authorization_receipts.append(json.loads(line[9:]))
            if line.startswith('DETECTIONWIRE '): detection_traces.append(json.loads(line[14:]))
    if authorization_receipts:
        (output/'authorization-receipts.json').write_text(json.dumps(authorization_receipts,indent=2)+'\n',encoding='utf-8')
    if detection_traces:
        (output/'detection-authorization.json').write_text(json.dumps(detection_traces,indent=2)+'\n',encoding='utf-8')
    report={'sourceRevision':receipt['sourceRevision'],'flowDeckPatch':receipt['flowDeckPatch'],'total':len(results),'passed':sum(r['exitCode']==0 for r in results),'results':results,
            'compiledSources':[{'path':f,'sha256':prepare.sha256((source/f).read_bytes())} for f in files],
            'binarySha256':prepare.sha256((output/'operation-test.exe').read_bytes()),'warnings':warnings,
            'actualRgbBaseAndNativeSources':True,'isolatedSdkFunctionBodiesSha256':prepare.sha256(extracted.encode('utf-8')),
            'hardwareAccessed':False,'openRgbExecuted':False,'realSocketsUsed':False,
            'testHarnessSources':[{'path':str(p.relative_to(ROOT)), 'sha256':prepare.sha256(p.read_bytes())} for p in
                [ROOT/'tests/native/openrgb-operation/operation.cpp',ROOT/'tests/native/openrgb-operation/detection-authorization.inc',ROOT/'native/OpenRgb/FlowDeckAsrockWire.h',ROOT/'scripts/test-openrgb-operation.py']]}
    (output/'results.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({'total':report['total'],'passed':report['passed'],'warnings':warnings}))
    if report['passed']!=report['total']: raise RuntimeError('Operation tests failed')


if __name__ == '__main__':
    parser=argparse.ArgumentParser(description=__doc__); parser.add_argument('prepared_source'); parser.add_argument('new_test_directory')
    args=parser.parse_args(); run(args.prepared_source,args.new_test_directory)
