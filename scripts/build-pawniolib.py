"""Build pinned PawnIOLib source in a new directory. Never load the resulting DLL."""
import argparse
import hashlib
import importlib.util
import io
import json
import subprocess
import zipfile
from pathlib import Path, PurePosixPath
_msvc_spec = importlib.util.spec_from_file_location("msvc_environment", Path(__file__).with_name("msvc_environment.py"))
_msvc = importlib.util.module_from_spec(_msvc_spec)
_msvc_spec.loader.exec_module(_msvc)
environment = _msvc.environment

REVISION = "aef64eb8810d93791dd24d8c318db2e3b119b264"
ARCHIVE_SHA256 = "784299e6a7b39be803379632dc6fcf1937d9cb62ebc655dea63b79278c287218"


def build(archive, output, toolchain=None):
    archive, output = Path(archive).resolve(), Path(output).resolve()
    raw = archive.read_bytes()
    if hashlib.sha256(raw).hexdigest() != ARCHIVE_SHA256:
        raise ValueError("Not the pinned PawnIOLib source archive")
    env = environment(output, toolchain)
    env.pop("CL", None)
    vc = Path(env["VCTOOLSINSTALLDIR"]) / "bin/Hostx64/x64"
    output.mkdir(parents=True, exist_ok=False)
    source = output / "source"
    prefix = "PawnIOLib-" + REVISION + "/"
    seen = set()
    with zipfile.ZipFile(io.BytesIO(raw)) as z:
        for info in z.infolist():
            if not info.filename.startswith(prefix):
                raise ValueError("Unexpected source archive root")
            if info.is_dir():
                continue
            name = PurePosixPath(info.filename[len(prefix):])
            if name.is_absolute() or ".." in name.parts or ":" in str(name) or "\\" in str(name) or str(name).casefold() in seen:
                raise ValueError("Unsafe/duplicate source archive path")
            seen.add(str(name).casefold())
            target = source.joinpath(*name.parts)
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(z.read(info))
    # CMake's Windows defaults supply kernel32.lib in addition to the project's ntdll.lib.
    command = [str(vc / "cl.exe"), "/nologo", "/O2", "/Ob2", "/DNDEBUG", "/MD", "/EHsc", "/std:c++20",
               "/DPawnIOLib_EXPORTS", "/LD", str(source / "PawnIOLib/PawnIOLib.cpp"), "/FoPawnIOLib.obj",
               "/FePawnIOLib.dll", "/link", "ntdll.lib", "kernel32.lib", "/ENTRY:DllEntry", "/NOCOFFGRPINFO",
               "/Brepro", "/PDBALTPATH:%_PDB%", "/INCREMENTAL:NO", "/MANIFEST:EMBED"]
    result = subprocess.run(command, cwd=output, env=env, capture_output=True)
    (output / "build.log").write_bytes(result.stdout + result.stderr)
    if result.returncode:
        raise RuntimeError("PawnIOLib build failed; see " + str(output / "build.log"))
    inspection = subprocess.run([str(vc / "dumpbin.exe"), "/headers", "/exports", "/imports", str(output / "PawnIOLib.dll")],
                                env=env, capture_output=True, check=True)
    (output / "inspection.txt").write_bytes(inspection.stdout)
    record = {"format": 1, "sourceRevision": REVISION, "sourceArchiveSha256": ARCHIVE_SHA256,
              "compilerSha256": hashlib.sha256((vc / "cl.exe").read_bytes()).hexdigest(),
              "windowsSdkVersion": env["WINDOWSSDKVERSION"].rstrip("\\/"), "command": command,
              "files": {p: hashlib.sha256((output / p).read_bytes()).hexdigest() for p in ("PawnIOLib.dll", "PawnIOLib.lib")},
              "binaryExecuted": False, "hardwareAccessed": False}
    (output / "build-result.json").write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(record))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source_archive")
    parser.add_argument("new_directory")
    parser.add_argument("--toolchain")
    args = parser.parse_args()
    build(args.source_archive, args.new_directory, args.toolchain)
