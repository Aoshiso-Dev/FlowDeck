"""Compile the complete pinned NetworkServer and SettingsManager with mock hardware.

This is NOT OpenRGB.exe: Windows startup, ResourceManager, enumeration, HID, DMI,
profiles and plugins are excluded. No hardware DLL or kernel module is linked.
Only the existing single synthetic ASRock controller can be created by this fixture.
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
    value = importlib.util.module_from_spec(spec); spec.loader.exec_module(value); return value


def build(source, output):
    source, output = Path(source).resolve(), Path(output).resolve()
    prepare = module('prepare', 'prepare-openrgb-source.py')
    pinned = prepare.verify(source)
    if pinned['flowDeckPatch'] not in (6, 7, 8) or output.exists():
        raise ValueError('Need verified patch 6/7/8 and a new output directory')
    output.mkdir(parents=True)
    files = [DIR+n for n in ['ASRockPolychromeUSBController.cpp', 'ASRockPolychromeUSBController.h',
        'RGBController_ASRockPolychromeUSB.cpp', 'RGBController_ASRockPolychromeUSB.h',
        'ASRockPolychromeUSBControllerDetect.cpp', 'FlowDeckAsrockIo.h', 'FlowDeckAsrockOperation.h',
        'FlowDeckAsrockAuthorization.h', 'FlowDeckHidAllowlist.h', 'FlowDeckAsrockWire.h',
        'FlowDeckAsrockPipe.h', 'FlowDeckAsrockBootstrap.h']]
    files += ['RGBController/RGBController.cpp', 'RGBController/RGBController.h', 'RGBController/RGBControllerInterface.h',
        'NetworkProtocol.h', 'NetworkProtocol.cpp', 'FlowDeckAsrockSdk.inc', 'FlowDeckAsrockSdkPeer.inc',
        'NetworkServer.cpp', 'NetworkServer.h', 'SettingsManager.cpp', 'SettingsManager.h',
        'JsonUtils.cpp', 'JsonUtils.h', 'filesystem.h', 'ProfileManager.h', 'PluginManagerInterface.h',
        'ResourceManagerCallback.h', 'net_port/net_port.h']
    if pinned['flowDeckPatch'] >= 7:
        files.append('FlowDeckNetworkLifecycle.inc')
    if pinned['flowDeckPatch'] >= 8:
        files.append('FlowDeckShutdown.h')
    for file in files:
        shutil.copyfile(source/file, output/Path(file).name)
    wrapper = output/(DIR+'FlowDeckAsrockAuthorization.h'); wrapper.parent.mkdir(parents=True)
    wrapper.write_text('#pragma once\n#include "../../FlowDeckAsrockAuthorization.h"\n', encoding='utf-8')
    fixture_path = ROOT/'tests/native/openrgb-io/fixture.h'
    fixture = fixture_path.read_text(encoding='utf-8')
    start, end = fixture.index('using RGBColor ='), fixture.index('struct hid_device')
    fixture = fixture[:start]+'#include "RGBController.h"\n#include "SettingsManager.h"\n'+fixture[end:]
    start, end = fixture.index('class SettingsManager'), fixture.index('class DMIInfo')
    fixture = fixture[:start]+'#include "ResourceManager.h"\n'+fixture[end:]
    fixture = fixture.replace('class RGBController { public: virtual ~RGBController() = default; };\n', '')
    fixture = fixture.replace('namespace StringUtils {', 'namespace StringUtils { inline std::string remove_null_terminating_chars(std::string value) { value.erase(std::remove(value.begin(), value.end(), char(0)), value.end()); return value; }')
    (output/'fixture.h').write_text(fixture, encoding='utf-8')
    for name in ['hidapi.h', 'dmiinfo.h', 'StringUtils.h', 'DetectionManager.h']:
        (output/name).write_text('#pragma once\n#include "fixture.h"\n', encoding='utf-8')
    inputs = [Path(__file__).resolve(), fixture_path]
    for name in ['ResourceManager.h', 'LogManager.h', 'NetworkClient.h', 'server-body.inc']:
        path = ROOT/'tests/native/openrgb-full-server'/name
        shutil.copyfile(path, output/name); inputs.append(path)
    if pinned['flowDeckPatch'] >= 7:
        for name in ['network-test-hooks.h', 'lifecycle-tests.inc']:
            path = ROOT/'tests/native/openrgb-full-server'/name
            shutil.copyfile(path, output/name); inputs.append(path)

    # Exact record layouts/constants from the pinned headers; their OS providers are absent.
    resource = (source/'ResourceManager.h').read_text(encoding='utf-8')
    records = resource[resource.index('typedef struct'):resource.index('class ResourceManager\n')]
    bus = (source/'i2c_smbus/i2c_smbus.h').read_text(encoding='utf-8')
    records += bus[bus.index('typedef struct'):bus.index('} i2c_smbus_info;')+len('} i2c_smbus_info;')]
    serial = (source/'serial_port/find_usb_serial_port.h').read_text(encoding='utf-8')
    records += '\n'+serial[serial.index('struct SerialDeviceInfo'):serial.index('} ;')+3]
    (output/'hardware-records.h').write_text('#pragma once\n#include <string>\n'+records, encoding='utf-8')
    (output/'i2c_smbus.h').write_text('#pragma once\n#include "hardware-records.h"\n', encoding='utf-8')
    inputs += [source/'ResourceManager.h', source/'i2c_smbus/i2c_smbus.h', source/'serial_port/find_usb_serial_port.h']

    # Registration statements are copied exactly, and run against the actual SettingsManager.
    resource = (source/'ResourceManager.cpp').read_text(encoding='utf-8')
    start = resource.index('    json                detection_settings_schema;')
    end = resource.index('    /*-----------------------------------------------------*\\\n    | Configure the log manager', start)
    schema = resource[start:end]
    (output/'fixture-schema.inc').write_text(schema, encoding='utf-8')
    inputs.append(source/'ResourceManager.cpp')
    original = ROOT/'tests/native/openrgb-integration/server.cpp'; inputs.append(original)
    harness = original.read_text(encoding='utf-8')
    harness = harness[:harness.index('struct NetworkClientInfo')]
    harness = harness.replace('using socklen_t = int;\n#include "FlowDeckAsrockSdkPeer.inc"\n#define MSG_NOSIGNAL 0\nconst char openrgb_sdk_magic[OPENRGB_SDK_MAGIC_SIZE] = {\'O\',\'R\',\'G\',\'B\'};', '#include "NetworkServer.h"')
    if pinned['flowDeckPatch'] >= 7:
        harness += '\n#include "lifecycle-tests.inc"\n'
    harness += '\n#include "server-body.inc"\n'
    (output/'server.cpp').write_text(harness, encoding='utf-8')
    env = module('builder', 'build-openrgb-source.py').environment(ROOT/'.tools/qt-6.8.3/6.8.3/msvc2022_64')
    compiler = Path(env['VCTOOLSINSTALLDIR'])/'bin/Hostx64/x64/cl.exe'
    command = [str(compiler), '/nologo', '/std:c++17', '/EHsc', '/W4', '/utf-8', '/permissive-', '/MT', '/O2',
        '/DFLOWDECK_PATCH='+str(pinned['flowDeckPatch']), '/DWIN32', '/DNOMINMAX', '/DWIN32_LEAN_AND_MEAN',
        '/I'+str(output), '/I'+str(source/'dependencies/json'),
        'ASRockPolychromeUSBController.cpp', 'RGBController_ASRockPolychromeUSB.cpp', 'RGBController.cpp',
        'ASRockPolychromeUSBControllerDetect.cpp', 'NetworkServer.cpp', 'NetworkProtocol.cpp', 'SettingsManager.cpp',
        'JsonUtils.cpp', 'server.cpp', '/Fe:full-server-fixture.exe', '/link', 'Ws2_32.lib']
    if pinned['flowDeckPatch'] >= 7:
        command.insert(1, '/DFLOWDECK_NETWORK_TEST')
    compiled = subprocess.run(command, cwd=output, env=env, capture_output=True, text=True, encoding='utf-8', errors='replace')
    log = compiled.stdout+compiled.stderr; (output/'build.txt').write_text(log, encoding='utf-8')
    if compiled.returncode: raise RuntimeError('Compilation failed; see '+str(output/'build.txt'))
    dumpbin = compiler.with_name('dumpbin.exe')
    inspected = subprocess.run([str(dumpbin), '/dependents', 'full-server-fixture.exe'], cwd=output, env=env,
        capture_output=True, text=True, encoding='utf-8', errors='replace')
    (output/'dependencies.txt').write_text(inspected.stdout+inspected.stderr, encoding='utf-8')
    dependencies = re.findall(r'^\s+(\S+\.dll)\s*$', inspected.stdout, re.MULTILINE | re.IGNORECASE)
    if inspected.returncode or {name.lower() for name in dependencies} != {'kernel32.dll', 'ws2_32.dll'}:
        raise RuntimeError('Unexpected native dependencies: '+str(dependencies))
    evidence = {'sourceRevision': pinned['sourceRevision'], 'flowDeckPatch': pinned['flowDeckPatch'], 'hardwareMocked': True,
        'fullNetworkServer': True, 'actualSettingsStorage': True, 'resourceManagerMocked': True,
        'windowsStartupIncluded': False, 'linkedDependencies': dependencies,
        'warnings': dict(Counter(re.findall(r'warning (C\d+)', log))),
        'binarySha256': prepare.sha256((output/'full-server-fixture.exe').read_bytes()),
        'compiledSources': [{'path': f, 'sha256': prepare.sha256((source/f).read_bytes())} for f in files],
        'inputs': [{'path': str(p.relative_to(ROOT)), 'sha256': prepare.sha256(p.read_bytes())} for p in inputs]}
    (output/'provenance.json').write_text(json.dumps(evidence, indent=2)+'\n', encoding='utf-8')
    print(json.dumps({'binarySha256': evidence['binarySha256'], 'warnings': evidence['warnings'], 'dependencies': dependencies}))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('prepared_source'); parser.add_argument('new_directory')
    args = parser.parse_args(); build(args.prepared_source, args.new_directory)
