"""Compile/test the patched detector with mock HID/constructors. No OpenRGB/Qt/device access."""
import argparse
import importlib.util
import json
import os
import shutil
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
FIRST = r"\\?\hid#vid_26ce&pid_01a2#one"
SECOND = r"\\?\hid#vid_26ce&pid_01a2#two"


def settings(paths):
    return {"FlowDeck": {"version": 1, "hid_paths": paths}}


def step(path=FIRST, opens=0, initializations=None, **kwargs):
    return {"path": path, "opens": opens, "initializations": opens if initializations is None else initializations, **kwargs}


def cases():
    invalid = [None, {}, {"FlowDeck": None}, {"FlowDeck": []}, {"FlowDeck": {"version": 1}},
               {"FlowDeck": {"version": 2, "hid_paths": [FIRST]}},
               {"FlowDeck": {"version": 1.0, "hid_paths": [FIRST]}},
               {"FlowDeck": {"version": True, "hid_paths": [FIRST]}},
               {"FlowDeck": {"version": 1, "hid_paths": FIRST}},
               {"FlowDeck": {"version": 1, "hid_paths": [FIRST], "all_devices": True}}]
    for values in [[], [FIRST, FIRST.upper()], [FIRST, ""], [FIRST, None], [FIRST, 1],
                   [FIRST, "invalid\0suffix"], [FIRST, "has space"], [FIRST, "tab\t"],
                   [FIRST, "nonascii\u00e9"], [FIRST, "delete\u007f"], [FIRST, "x" * 2049],
                   [FIRST] + [f"path{i}" for i in range(32)]]:
        invalid.append(settings(values))
    for index, config in enumerate(invalid):
        yield {"name": f"invalid-{index}", "settings": config, "settings_reads": 1, "steps": [step(), step(settings=settings([FIRST]))]}
    yield {"name": "only-captured-case-insensitive-once", "settings": settings([FIRST.upper(), SECOND]), "settings_reads": 1,
           "steps": [step("unknown"), step(opens=1), step(FIRST.upper(), 1), step(SECOND, 2), step(SECOND, 2)]}
    yield {"name": "live-settings-cannot-widen-capture", "settings": settings([FIRST]), "settings_reads": 1,
           "steps": [step(opens=1), step(SECOND, 1, settings=settings([SECOND])), step(FIRST, 1)]}
    yield {"name": "failed-open-is-not-retried", "settings": settings([FIRST]), "settings_reads": 1,
           "steps": [step(opens=1, initializations=0, open_fails=True), step(opens=1, initializations=0)]}
    yield {"name": "concurrent-hotplug-only-opens-once", "settings": settings([FIRST]), "settings_reads": 1,
           "steps": [step(opens=1, threads=32), step(opens=1, threads=32)]}
    yield {"name": "unavailable-settings-stay-closed", "settings": settings([FIRST]), "settings_reads": 1, "settings_fail": True,
           "steps": [step(), step(settings_fail=False)]}
    yield {"name": "invalid-info-and-wrong-products-never-open", "settings": settings([FIRST]), "settings_reads": 1,
           "steps": [step(null_info=True), step(vid=0x1234), step(pid=0x1a6), step(null_path=True), step(opens=1)]}
    maximum = "a" * 2048
    paths = [maximum] + [f"path{i}" for i in range(31)]
    yield {"name": "maximum-32-paths-and-2048-characters", "settings": settings(paths), "settings_reads": 1,
           "steps": [step(path, index + 1) for index, path in enumerate(paths)] + [step(maximum, 32)]}


def run(source, output):
    source, output = Path(source).resolve(), Path(output).resolve()
    if output.exists():
        raise ValueError("Test output directory must be new")
    spec = importlib.util.spec_from_file_location("prepare", ROOT / "scripts/prepare-openrgb-source.py")
    prepare = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(prepare)
    receipt = prepare.verify(source)
    guard = ROOT / "native/OpenRgb/FlowDeckHidAllowlist.h"
    if guard.read_bytes() != (source / prepare.GUARD).read_bytes():
        raise ValueError("Prepared guard differs from current source")
    output.mkdir(parents=True)
    # Copy the detector byte-for-byte. Quoted collaborator includes resolve to the stubs here;
    # the real patched allowlist and upstream nlohmann JSON are compiled without substitutions.
    detector = output / "detector-under-test.cpp"
    shutil.copyfile(source / prepare.DETECTOR, detector)
    shutil.copyfile(source / prepare.GUARD, output / "FlowDeckHidAllowlist.h")
    if receipt['flowDeckPatch'] >= 5:
        for name in ['FlowDeckAsrockAuthorization.h', 'FlowDeckAsrockIo.h']:
            shutil.copyfile(source / ('Controllers/ASRockPolychromeUSBController/' + name), output / name)
    shutil.copyfile(ROOT / "tests/native/openrgb/fixture.h", output / "fixture.h")
    shutil.copyfile(ROOT / "tests/native/openrgb/detection.cpp", output / "detection.cpp")
    for name in ["hidapi.h", "ASRockPolychromeUSBController.h", "DetectionManager.h", "RGBController_ASRockPolychromeUSB.h", "SettingsManager.h", "ResourceManager.h"]:
        (output / name).write_text('#pragma once\n#include "fixture.h"\n', encoding="utf-8")
    vcvars = Path(r"C:\Program Files\Microsoft Visual Studio\18\Insiders\VC\Auxiliary\Build\vcvars64.bat")
    if not vcvars.is_file():
        raise ValueError("Configured MSVC environment not found")
    # cmd parses quotes differently from CommandLineToArgvW; list2cmdline would backslash-escape
    # them. Only this fixed, local SDK path is interpolated into the cmd command string.
    environment = subprocess.run(f'cmd.exe /d /s /c ""{vcvars}" >nul && set"',
                                 capture_output=True, text=True, check=True, encoding="utf-8", errors="replace")
    env = os.environ.copy()
    for line in environment.stdout.splitlines():
        if "=" in line and not line.startswith("="):
            key, value = line.split("=", 1)
            env[key] = value
    compiler = str(Path(env["VCToolsInstallDir"]) / "bin/Hostx64/x64/cl.exe")
    command = [compiler, "/nologo", "/std:c++17", "/EHsc", "/W4", "/WX", "/utf-8", "/permissive-", '/DFLOWDECK_PATCH='+str(receipt['flowDeckPatch']),
               "/I" + str(output), "/I" + str(source / "dependencies/json"), "detector-under-test.cpp", "detection.cpp", "/Fe:detector-test.exe"]
    build = subprocess.run(command, cwd=output, env=env, capture_output=True, text=True, encoding="utf-8", errors="replace")
    (output / "build.txt").write_text(build.stdout + build.stderr, encoding="utf-8")
    if build.returncode:
        raise RuntimeError("Native compilation failed; see " + str(output / "build.txt"))
    results = []
    for test in cases():
        path = output / (test["name"] + ".json")
        path.write_text(json.dumps(test), encoding="utf-8")
        process = subprocess.run([str(output / "detector-test.exe"), str(path)], cwd=output, capture_output=True, text=True, timeout=20)
        results.append({"name": test["name"], "exitCode": process.returncode, "output": process.stdout + process.stderr})
    report = {"sourceRevision": receipt["sourceRevision"], "flowDeckPatch": receipt["flowDeckPatch"],
              "detectorSha256": prepare.sha256(detector.read_bytes()), "guardSha256": prepare.sha256(guard.read_bytes()),
              "binarySha256": prepare.sha256((output / "detector-test.exe").read_bytes()),
              "total": len(results), "passed": sum(r["exitCode"] == 0 for r in results), "results": results,
              "openRgbExecuted": False, "hardwareAccessed": False, "wholeOpenRgbBuilt": False}
    (output / "results.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({k: v for k, v in report.items() if k != "results"}))
    if report["passed"] != report["total"]:
        raise RuntimeError("Detector tests failed; see results.json")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("prepared_source")
    parser.add_argument("new_test_directory")
    args = parser.parse_args()
    run(args.prepared_source, args.new_test_directory)
