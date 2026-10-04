"""Compile the real patched native controller and detector against mock HID/settings/DMI."""
import argparse
import importlib.util
import json
import re
from collections import Counter
import shutil
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DIRECTORY = 'Controllers/ASRockPolychromeUSBController/'


def module(name, file):
    spec = importlib.util.spec_from_file_location(name, ROOT / 'scripts' / file)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


def run(source, output):
    source, output = Path(source).resolve(), Path(output).resolve()
    prepare = module('prepare', 'prepare-openrgb-source.py')
    receipt = prepare.verify(source)
    if receipt['flowDeckPatch'] not in (2, 3, 4, 5):
        raise ValueError('The checked IO tests require source patch 2 through 5')
    if output.exists():
        raise ValueError('Test output must be new')
    output.mkdir(parents=True)
    files = ['ASRockPolychromeUSBController.cpp', 'ASRockPolychromeUSBController.h',
             'ASRockPolychromeUSBControllerDetect.cpp', 'FlowDeckAsrockIo.h', 'FlowDeckHidAllowlist.h']
    if receipt['flowDeckPatch'] >= 5:
        files.append('FlowDeckAsrockAuthorization.h')
        shutil.copyfile(ROOT / 'tests/native/openrgb-io/authorization.inc', output / 'authorization.inc')
    for file in files:
        shutil.copyfile(source / (DIRECTORY + file), output / file)
    for file in ['fixture.h', 'exchange.cpp', 'RGBController_ASRockPolychromeUSB.h']:
        shutil.copyfile(ROOT / 'tests/native/openrgb-io' / file, output / file)
    for file in ['hidapi.h', 'RGBController.h', 'DetectionManager.h', 'SettingsManager.h', 'ResourceManager.h', 'dmiinfo.h', 'StringUtils.h']:
        (output / file).write_text('#pragma once\n#include "fixture.h"\n', encoding='utf-8')
    builder = module('builder', 'build-openrgb-source.py')
    env = builder.environment(ROOT / '.tools/qt-6.8.3/6.8.3/msvc2022_64')
    compiler = Path(env['VCTOOLSINSTALLDIR']) / 'bin/Hostx64/x64/cl.exe'
    command = [str(compiler), '/nologo', '/std:c++17', '/EHsc', '/W4', '/utf-8', '/permissive-', '/DFLOWDECK_PATCH='+str(receipt['flowDeckPatch']),
               '/I' + str(output), '/I' + str(source / 'dependencies/json'),
               'ASRockPolychromeUSBController.cpp', 'ASRockPolychromeUSBControllerDetect.cpp', 'exchange.cpp', '/Fe:exchange-test.exe']
    compiled = subprocess.run(command, cwd=output, env=env, capture_output=True, text=True, encoding='utf-8', errors='replace')
    (output / 'build.txt').write_text(compiled.stdout + compiled.stderr, encoding='utf-8')
    if compiled.returncode:
        raise RuntimeError('Native IO compilation failed; see ' + str(output / 'build.txt'))
    warnings = dict(Counter(re.findall(r'warning (C\d+)', compiled.stdout + compiled.stderr)))
    # Existing upstream narrowing/local-name warnings remain visible. Additional diagnostics
    # require investigation; the real controller is not rewritten merely to satisfy a fixture.
    if warnings != {'C4244': 2, 'C4458': 3}:
        raise RuntimeError('Unexpected native controller warnings: ' + str(warnings))
    cases = ['construction-and-query', 'normal-detection-ownership', 'rgb-constructor-failure',
             *['detection-failure-' + str(i) for i in range(1, 7)],
             'volatile-zone-and-order', 'led-count-denied', 'partial-direct-frame', 'query-selector-mismatch',
             'write-exception', 'read-exception',
             *['write-' + str(i) for i in (-1, 0, 64, 66)], *['read-' + str(i) for i in (-1, 0, 63, 65)],
             *['rejected-' + name for name in ('flash','led-count','query-selector','report-id','mode','direct-marker')],
             'concurrent-exchanges']
    if receipt['flowDeckPatch'] >= 5:
        cases += ['auth-'+name for name in ['missing', 'open-denied', 'empty-io-check', 'serial', 'serial-denied',
                   'captured-path', 'replace-denied', 'invalid-path', 'origin-scope', 'origin-threads', 'successful-trace']]
        cases += ['auth-detection-'+phase+'-'+str(i) for phase in ['write','read','reply'] for i in range(1,7)]
        cases += ['auth-operation-'+phase for phase in ['write','read','reply']]
    results = []
    for case in cases:
        result = subprocess.run([str(output / 'exchange-test.exe'), case], cwd=output, capture_output=True, text=True, timeout=20)
        results.append({'case': case, 'exitCode': result.returncode, 'output': result.stdout + result.stderr})
    report = {'sourceRevision': receipt['sourceRevision'], 'flowDeckPatch': receipt['flowDeckPatch'],
              'total': len(results), 'passed': sum(r['exitCode'] == 0 for r in results), 'results': results,
              'compiledSources': [{'path': file, 'sha256': prepare.sha256((output / file).read_bytes())} for file in files],
              'binarySha256': prepare.sha256((output / 'exchange-test.exe').read_bytes()), 'upstreamWarnings': warnings,
              'nativeControllerAndDetectorBytesUnchanged': True, 'hardwareAccessed': False, 'openRgbExecuted': False}
    (output / 'results.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'total': report['total'], 'passed': report['passed'], 'hardwareAccessed': False}))
    if report['passed'] != report['total']:
        raise RuntimeError('Native IO tests failed; see results.json')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('prepared_source')
    parser.add_argument('new_test_directory')
    args = parser.parse_args()
    run(args.prepared_source, args.new_test_directory)
