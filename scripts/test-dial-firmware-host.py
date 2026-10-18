"""Compile and test the actual portable dial firmware logic; no device APIs."""
from pathlib import Path
import hashlib
import json
import os
import subprocess
import sys

root = Path(__file__).resolve().parents[1]
out = root / "artifacts/dial-development/firmware-host"
out.mkdir(parents=True, exist_ok=True)
vc = root / ".tools/msvc-14.43-stable/VC/Tools/MSVC/14.43.34808"
sdk = Path("C:/Program Files (x86)/Windows Kits/10")
version = "10.0.26100.0"
env = os.environ.copy()
env["VSLANG"] = "1033"
for key in ("CL", "_CL_", "LINK", "_LINK_"):
    env.pop(key, None)
env["INCLUDE"] = ";".join(map(str, [vc / "include", *[
    sdk / "Include" / version / p for p in ("ucrt", "shared", "um")]]))
env["LIB"] = ";".join(map(str, [vc / "lib/x64", *[
    sdk / "Lib" / version / p / "x64" for p in ("ucrt", "um")]]))
env["PATH"] = str(vc / "bin/Hostx64/x64") + os.pathsep + env.get("PATH", "")
source = root / "firmware/dial/test/core_host.cpp"
exe = out / "dial-core-host.exe"
args = [str(vc / "bin/Hostx64/x64/cl.exe"), "/nologo", "/std:c++17", "/EHsc", "/W4", "/utf-8",
        "/I" + str(root / "firmware/dial/src"),
        "/I" + str(root / "firmware/dial/.pio/libdeps/jc3636k518/ArduinoJson/src"),
        str(source), "/Fe:" + str(exe), "/Fo:" + str(out / "dial-core-host.obj")]
build = subprocess.run(args, cwd=out, env=env, text=True, encoding="utf-8", errors="replace", capture_output=True)
(out / "build.log").write_text(build.stdout + build.stderr, encoding="utf-8")
if build.returncode:
    print(build.stdout + build.stderr)
    sys.exit(build.returncode)
run = subprocess.run([str(exe)], cwd=out, env=env, text=True, encoding="utf-8", errors="replace", capture_output=True)
(out / "run.log").write_text(run.stdout + run.stderr, encoding="utf-8")
print(run.stdout + run.stderr)
inputs = [source, Path(__file__), *sorted((root / "firmware/dial/src").glob("*.h"))]
(out / "result.json").write_text(json.dumps({
    "passed": run.returncode == 0, "hardwareAccess": False,
    "sha256": {p.relative_to(root).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest() for p in inputs}
}, indent=2), encoding="utf-8")
sys.exit(run.returncode)
