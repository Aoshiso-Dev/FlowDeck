"""Build the prepared source in a separate directory. Never execute the resulting OpenRGB."""
import argparse
import datetime
import hashlib
import importlib.util
import json
import os
import re
import subprocess
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
_msvc_spec = importlib.util.spec_from_file_location("msvc_environment", ROOT / "scripts/msvc_environment.py")
_msvc = importlib.util.module_from_spec(_msvc_spec)
_msvc_spec.loader.exec_module(_msvc)
environment = _msvc.environment


def compiler_response_files(build):
    # qmake puts only source names into response files. OpenRGB's 23 KB include list
    # can still exceed cmd.exe's limit; put the compiler flags in the same response.
    path = build / "Makefile"
    before = path.read_bytes()
    content = before.decode("utf-8")
    content, count = re.subn(r"(?m)^\t\$\((CXX|CC)\) ([^\r\n]*?) @<<\r?\n",
                             lambda m: "\t$(" + m[1] + ") @<<\n" + m[2] + "\n", content)
    if not count:
        raise ValueError("No expected qmake MSVC response-file recipes")
    after = content.encode("utf-8")
    path.write_bytes(after)
    record = {"recipes": count, "originalSha256": hashlib.sha256(before).hexdigest(),
              "preparedSha256": hashlib.sha256(after).hexdigest(), "sourceChanged": False}
    (build / "makefile-response-files.json").write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")


def run(source, build, resume=False, path_aliases=None, make_tool='nmake', jobs=8, toolchain=None, reuse_makefile=False):
    source, build = Path(source).resolve(), Path(build).resolve()
    spec = importlib.util.spec_from_file_location("prepare", ROOT / "scripts/prepare-openrgb-source.py")
    prepare = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(prepare)
    receipt = prepare.verify(source)
    qt = ROOT / ".tools/qt-6.8.3/6.8.3/msvc2022_64"
    if not (qt / "bin/qmake.exe").is_file():
        raise ValueError("Prepared Qt SDK not found")
    if build.exists() and not resume:
        raise ValueError("Build destination must be new (or explicitly resume a stopped build)")
    if build == source or source in build.parents or build in source.parents:
        raise ValueError("Build output must be separate from prepared source")
    build.mkdir(parents=True, exist_ok=resume)
    compile_source, compile_build = source, build
    if path_aliases:
        aliases = json.loads(Path(path_aliases).read_text(encoding="utf-8-sig"))
        for key, target in {"source": source, "build": build, "qt": qt}.items():
            alias = Path(aliases[key])
            if not alias.is_absolute() or not str(alias).isascii() or alias.resolve() != target.resolve():
                raise ValueError("Build alias must resolve to the intended workspace directory")
        compile_source, compile_build, qt = (Path(aliases[k]) for k in ("source", "build", "qt"))
    env = environment(qt, toolchain)
    if path_aliases and 'msvc' in aliases:
        actual_vc = Path(env['VCTOOLSINSTALLDIR'])
        alias_vc = Path(aliases['msvc'])
        if not alias_vc.is_absolute() or not str(alias_vc).isascii() or alias_vc.resolve() != actual_vc.resolve():
            raise ValueError('MSVC alias must resolve to the verified compiler directory')
        for key in ['PATH', 'INCLUDE', 'LIB', 'VCTOOLSINSTALLDIR']:
            env[key] = env[key].replace(str(actual_vc), str(alias_vc))
    if not 1 <= jobs <= 32:
        raise ValueError('Build concurrency must be between 1 and 32')
    if make_tool == 'jom':
        env['CL'] = '/MP1'
        make = ROOT / '.tools/jom-1.1.7/jom.exe'
        receipt_jom = json.loads((ROOT / 'artifacts/jom-1.1.7-preparation.json').read_text(encoding='utf-8'))
        expected = next(entry['sha256'] for entry in receipt_jom['files'] if entry['path'] == 'jom.exe')
        if hashlib.sha256(make.read_bytes()).hexdigest().upper() != expected:
            raise ValueError('Prepared jom binary changed')
    compiler = Path(env["VCTOOLSINSTALLDIR"]) / "bin/Hostx64/x64"
    steps = []

    def execute(label, command):
        started = time.monotonic()
        log = build / (label + ".log")
        with log.open("w", encoding="utf-8") as output:
            child = subprocess.Popen(command, cwd=compile_build, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                     text=True, encoding="utf-8", errors="replace")
            print(json.dumps({"stage": label, "pid": child.pid, "log": str(log)}), flush=True)
            reported = started
            for line in child.stdout:
                output.write(line)
                output.flush()
                if time.monotonic() - reported >= 20:
                    print(json.dumps({"stage": label, "elapsedSeconds": round(time.monotonic() - started),
                                      "lastOutput": line.strip()[:250]}, ensure_ascii=True), flush=True)
                    reported = time.monotonic()
            code = child.wait()
        step = {"stage": label, "command": command, "exitCode": code, "seconds": round(time.monotonic() - started, 2),
                "logSha256": hashlib.sha256(log.read_bytes()).hexdigest().upper()}
        steps.append(step)
        print(json.dumps({"stage": label, "exitCode": code, "seconds": step["seconds"]}), flush=True)
        if code:
            raise RuntimeError(label + " failed; see " + str(log))

    result = {"recordedAt": datetime.datetime.now().astimezone().isoformat(), "sourceRevision": receipt["sourceRevision"],
              "flowDeckPatch": receipt["flowDeckPatch"], "sourceManifestSha256": hashlib.sha256((source / "flowdeck-source.json").read_bytes()).hexdigest().upper(),
              "sourceDirectory": str(source), "buildDirectory": str(build), "qtVersion": "6.8.3", "steps": steps,
              "toolchain": {"configuration": str(Path(toolchain or ROOT / '.tools/msvc-14.43-stable/toolchain.json').resolve()),
                            "compilerDirectory": str(compiler),
                            "compilerSha256": hashlib.sha256((compiler / 'cl.exe').read_bytes()).hexdigest().upper(),
                            "linkerSha256": hashlib.sha256((compiler / 'link.exe').read_bytes()).hexdigest().upper(),
                            "windowsSdkVersion": env['WINDOWSSDKVERSION'].rstrip('\\/'), "prerelease": False},
              "succeeded": False, "openRgbExecuted": False, "hardwareAccessed": False, "uacRequested": False}
    try:
        if reuse_makefile:
            if not resume:
                raise ValueError('Reusing the Makefile requires --resume')
            previous = json.loads((build / 'build-result.json').read_text(encoding='utf-8'))
            prepared = json.loads((build / 'makefile-response-files.json').read_text(encoding='utf-8'))
            if previous['sourceManifestSha256'] != result['sourceManifestSha256'] or previous['qtVersion'] != result['qtVersion'] or \
               previous['toolchain']['compilerSha256'] != result['toolchain']['compilerSha256'] or \
               previous['toolchain']['linkerSha256'] != result['toolchain']['linkerSha256'] or \
               previous['toolchain']['windowsSdkVersion'] != result['toolchain']['windowsSdkVersion'] or \
               hashlib.sha256((build / 'Makefile').read_bytes()).hexdigest() != prepared['preparedSha256']:
                raise ValueError('Previous Makefile does not match these inputs/tools')
            result['reusedMakefileSha256'] = prepared['preparedSha256']
        else:
            execute("qmake", [str(qt / "bin/qmake.exe"), str(compile_source / "OpenRGB.pro"), "CONFIG-=debug_and_release", "CONFIG+=release"])
            compiler_response_files(build)
        if make_tool == 'jom':
            execute('jom', [str(make), '-j' + str(jobs), '-f', 'Makefile'])
        else:
            execute("nmake", [str(compiler / "nmake.exe"), "/NOLOGO"])
        exe = build / "release/OpenRGB.exe"
        if not exe.is_file():
            raise RuntimeError("Build returned success without OpenRGB.exe")
        execute("imports", [str(compiler / "dumpbin.exe"), "/headers", "/dependents", str(exe)])
        prepare.verify(source)
        result.update(succeeded=True, binarySha256=hashlib.sha256(exe.read_bytes()).hexdigest().upper(), binaryLength=exe.stat().st_size)
    finally:
        (build / "build-result.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"succeeded": result["succeeded"], "binarySha256": result.get("binarySha256"), "openRgbExecuted": False}), flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("prepared_source")
    parser.add_argument("build_directory")
    parser.add_argument("--resume", action="store_true")
    parser.add_argument("--path-aliases", help="Validated ASCII junction paths to the same workspace source/build/SDK")
    parser.add_argument('--make-tool', choices=['nmake', 'jom'], default='nmake')
    parser.add_argument('--jobs', type=int, default=8)
    parser.add_argument('--toolchain', help='Verified stable MSVC configuration; defaults to the workspace toolchain')
    parser.add_argument('--reuse-makefile', action='store_true', help='Resume a checked generated Makefile with unchanged source and tools')
    args = parser.parse_args()
    run(args.prepared_source, args.build_directory, args.resume, args.path_aliases, args.make_tool, args.jobs, args.toolchain, args.reuse_makefile)
