"""Compile/run the actual firmware parser/effect code on Windows; no serial or USB APIs."""
from pathlib import Path
import hashlib, json, os, subprocess, sys

root = Path(__file__).resolve().parents[1]
out = root / 'artifacts/resume-20260930/m5-body-led/firmware-host'
out.mkdir(parents=True, exist_ok=True)
vc = root / '.tools/msvc-14.43-stable/VC/Tools/MSVC/14.43.34808'
sdk = Path('C:/Program Files (x86)/Windows Kits/10')
version = '10.0.26100.0'
env = os.environ.copy()
env['INCLUDE'] = ';'.join(map(str, [vc / 'include', *[sdk / 'Include' / version / p for p in ['ucrt', 'shared', 'um']]]))
env['LIB'] = ';'.join(map(str, [vc / 'lib/x64', *[sdk / 'Lib' / version / p / 'x64' for p in ['ucrt', 'um']]]))
exe = out / 'body-led-host.exe'
source = root / 'firmware/m5go/test/body_led_host.cpp'
args = [str(vc / 'bin/Hostx64/x64/cl.exe'), '/nologo', '/std:c++17', '/EHsc', '/W4', '/utf-8',
        '/I' + str(root / 'firmware/m5go/src'), '/I' + str(root / 'firmware/m5go/.pio/libdeps/m5go/ArduinoJson/src'),
        str(source), '/Fe:' + str(exe), '/Fo:' + str(out / 'body-led-host.obj')]
build = subprocess.run(args, cwd=out, env=env, text=True, capture_output=True)
(out / 'build.log').write_text(build.stdout + build.stderr, encoding='utf-8')
if build.returncode: print(build.stdout + build.stderr); sys.exit(build.returncode)
vectors = root / 'tests/FlowDeck.Tests/Fixtures/M5BodyLedVectors.jsonl'
run = subprocess.run([str(exe), str(vectors)], cwd=out, text=True, capture_output=True)
(out / 'run.log').write_text(run.stdout + run.stderr, encoding='utf-8')
print(run.stdout + run.stderr)
assert run.returncode == 0
inputs = [source, root / 'firmware/m5go/src/body_led_logic.h', vectors, Path(__file__)]
(out / 'result.json').write_text(json.dumps({'passed': True, 'effectVectors': 800, 'hardwareAccess': False,
    'sha256': {p.relative_to(root).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest() for p in inputs}}, indent=2), encoding='utf-8')
