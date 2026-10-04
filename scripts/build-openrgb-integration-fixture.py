"""Build actual patch 6 detection/RGB/SDK handlers with mock HID and real Windows IPC/TCP.

The accept/framing loop and OS/settings collaborators are test fixtures, not the full
upstream NetworkServer. No OpenRGB binary or hardware is executed by this builder.
"""
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
    spec = importlib.util.spec_from_file_location(name, ROOT/'scripts'/file)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


def build(source, output):
    source, output = Path(source).resolve(), Path(output).resolve()
    prepare = module('prepare', 'prepare-openrgb-source.py')
    pinned = prepare.verify(source)
    if pinned['flowDeckPatch'] != 6 or output.exists():
        raise ValueError('Need verified patch 6 and a new output directory')
    output.mkdir(parents=True)
    files = [DIR+n for n in ['ASRockPolychromeUSBController.cpp', 'ASRockPolychromeUSBController.h',
        'RGBController_ASRockPolychromeUSB.cpp', 'RGBController_ASRockPolychromeUSB.h',
        'ASRockPolychromeUSBControllerDetect.cpp', 'FlowDeckAsrockIo.h', 'FlowDeckAsrockOperation.h',
        'FlowDeckAsrockAuthorization.h', 'FlowDeckHidAllowlist.h', 'FlowDeckAsrockWire.h',
        'FlowDeckAsrockPipe.h', 'FlowDeckAsrockBootstrap.h']]
    files += ['RGBController/RGBController.cpp', 'RGBController/RGBController.h',
        'RGBController/RGBControllerInterface.h', 'NetworkProtocol.h', 'FlowDeckAsrockSdk.inc', 'FlowDeckAsrockSdkPeer.inc']
    for file in files:
        shutil.copyfile(source/file, output/Path(file).name)
    wrapper = output/(DIR+'FlowDeckAsrockAuthorization.h')
    wrapper.parent.mkdir(parents=True)
    wrapper.write_text('#pragma once\n#include "../../FlowDeckAsrockAuthorization.h"\n', encoding='utf-8')
    fixture_path = ROOT/'tests/native/openrgb-io/fixture.h'
    fixture = fixture_path.read_text(encoding='utf-8')
    start, end = fixture.index('using RGBColor ='), fixture.index('struct hid_device')
    fixture = fixture[:start]+'#include "RGBController.h"\n'+fixture[end:]
    fixture = fixture.replace('class RGBController { public: virtual ~RGBController() = default; };\n', '')
    fixture = fixture.replace('namespace StringUtils {', 'namespace StringUtils { inline std::string remove_null_terminating_chars(std::string value) { value.erase(std::remove(value.begin(), value.end(), char(0)), value.end()); return value; }')
    fixture = fixture.replace('class SettingsManager', 'json FixtureGetSettings(const std::string&);\njson FixtureGetSchema(const std::string&);\nvoid FixtureSetSettings(const std::string&,json);\nclass SettingsManager')
    fixture = fixture.replace('json GetSettings(std::string) { return fixtureSettings; }',
        'json GetSettings(std::string key) { return FixtureGetSettings(key); }\n    json GetSettingsSchema(std::string key) { return FixtureGetSchema(key); }')
    fixture = fixture.replace('void SetSettings(std::string, json value) { fixtureSettings = value; }',
        'void SetSettings(std::string key, json value) { FixtureSetSettings(key,value); }')
    (output/'fixture.h').write_text(fixture, encoding='utf-8')
    for name in ['hidapi.h', 'SettingsManager.h', 'ResourceManager.h', 'dmiinfo.h', 'StringUtils.h', 'DetectionManager.h']:
        (output/name).write_text('#pragma once\n#include "fixture.h"\n', encoding='utf-8')
    (output/'LogManager.h').write_text('#pragma once\n#define LOG_ERROR(...) ((void)0)\n', encoding='utf-8')
    patcher = module('patcher', 'prepare-openrgb-operation-source.py')
    server = (source/'NetworkServer.cpp').read_text(encoding='utf-8')
    macros = server[server.index('#define COPY_DATA_FIELD('):server.index('NetworkClientInfo::NetworkClientInfo()')]
    functions = []
    for name, parameters in [('UpdateLEDs', 'unsigned int controller_id'), ('UpdateZoneLEDs', 'unsigned int controller_id'),
                             ('UpdateSingleLED', 'unsigned int controller_id'), ('UpdateSaveMode', 'unsigned int controller_id, bool save_mode')]:
        signature = 'NetPacketStatus NetworkServer::ProcessRequest_RGBController_'+name+'(NetworkClientInfo* client_info, unsigned int data_size, unsigned char* data_ptr, '+parameters+')'
        functions.append(patcher.function(server, signature))
    for name in ['GetSettings', 'GetSettingsSchema']:
        functions.append(patcher.function(server, 'NetPacketStatus NetworkServer::ProcessRequest_SettingsManager_'+name+
            '(NetworkClientInfo* client_info, unsigned int data_size, unsigned char* data_ptr)'))
    protocol = (source/'NetworkProtocol.cpp').read_text(encoding='utf-8')
    start = protocol.index('void InitNetPacketHeader')
    functions.append(protocol[start:protocol.index('\n}', start)+2])
    extracted = macros+'\n'+'\n\n'.join(functions)
    (output/'sdk-functions.inc').write_text(extracted, encoding='utf-8')
    harness = ROOT/'tests/native/openrgb-integration/server.cpp'
    shutil.copyfile(harness, output/'server.cpp')
    env = module('builder', 'build-openrgb-source.py').environment(ROOT/'.tools/qt-6.8.3/6.8.3/msvc2022_64')
    compiler = Path(env['VCTOOLSINSTALLDIR'])/'bin/Hostx64/x64/cl.exe'
    command = [str(compiler), '/nologo', '/std:c++17', '/EHsc', '/W4', '/utf-8', '/permissive-', '/MT', '/O2',
        '/DFLOWDECK_PATCH=6', '/DNOMINMAX', '/DWIN32_LEAN_AND_MEAN', '/I'+str(output), '/I'+str(source/'dependencies/json'),
        'ASRockPolychromeUSBController.cpp', 'RGBController_ASRockPolychromeUSB.cpp', 'RGBController.cpp',
        'ASRockPolychromeUSBControllerDetect.cpp', 'server.cpp', '/Fe:integration-fixture.exe', '/link', 'Ws2_32.lib']
    compiled = subprocess.run(command, cwd=output, env=env, capture_output=True, text=True, encoding='utf-8', errors='replace')
    log = compiled.stdout+compiled.stderr
    (output/'build.txt').write_text(log, encoding='utf-8')
    if compiled.returncode:
        raise RuntimeError('Compilation failed; see '+str(output/'build.txt'))
    warnings = dict(Counter(re.findall(r'warning (C\d+)', log)))
    if warnings != {'C4458':28, 'C4244':11, 'C4996':14}:
        raise RuntimeError('Unexpected warnings: '+str(warnings))
    evidence = {'sourceRevision': pinned['sourceRevision'], 'flowDeckPatch': 6, 'hardwareMocked': True,
        'actualWindowsPipe': True, 'actualWinsock': True, 'actualSettingsHandlers': True,
        'settingsStorageMocked': True, 'fullNetworkServer': False, 'warnings': warnings,
        'binarySha256': prepare.sha256((output/'integration-fixture.exe').read_bytes()),
        'sdkBodiesSha256': prepare.sha256(extracted.encode('utf-8')),
        'compiledSources': [{'path': f, 'sha256': prepare.sha256((source/f).read_bytes())} for f in files],
        'inputs': [{'path': str(p.relative_to(ROOT)), 'sha256': prepare.sha256(p.read_bytes())}
            for p in [Path(__file__).resolve(), harness, fixture_path, source/'NetworkServer.cpp', source/'NetworkProtocol.cpp']]}
    (output/'provenance.json').write_text(json.dumps(evidence, indent=2)+'\n', encoding='utf-8')
    print(json.dumps({'binarySha256': evidence['binarySha256'], 'warnings': warnings}))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('prepared_source'); parser.add_argument('new_directory')
    args = parser.parse_args(); build(args.prepared_source, args.new_directory)
