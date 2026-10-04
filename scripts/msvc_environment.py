"""Construct a process-local, hash-verified MSVC environment without running vcvars or installers."""
import hashlib
import json
import os
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def environment(qt, configuration=None):
    configuration = Path(configuration or ROOT / ".tools/msvc-14.43-stable/toolchain.json").resolve()
    record = json.loads(configuration.read_text(encoding="utf-8-sig"))
    if record.get("format") != 1 or record.get("prerelease") is not False or not record.get("files"):
        raise ValueError("A verified stable MSVC toolchain configuration is required")
    base = (configuration.parent / record["root"]).resolve()
    for relative, expected in record["files"].items():
        file = (base / relative).resolve()
        if not file.is_relative_to(base) or hashlib.sha256(file.read_bytes()).hexdigest() != expected.lower():
            raise ValueError("MSVC toolchain input changed: " + relative)
    vc = (base / record["vcDirectory"]).resolve()
    if not vc.is_relative_to(base):
        raise ValueError("MSVC compiler directory escapes the verified toolchain")
    sdk = Path(record["windowsSdkRoot"]).resolve()
    version = record["windowsSdkVersion"]
    if not version or any(c not in "0123456789." for c in version):
        raise ValueError("Invalid Windows SDK version")
    compiler = vc / "bin/Hostx64/x64"
    sdk_bin = sdk / "bin" / version / "x64"
    for file in (compiler / "cl.exe", compiler / "link.exe", compiler / "nmake.exe", sdk_bin / "rc.exe",
                 vc / "lib/x64/msvcrt.lib", sdk / "Lib" / version / "um/x64/kernel32.lib"):
        if not file.is_file():
            raise ValueError("Required compiler/SDK input is absent: " + str(file))
    result = {key.upper(): value for key, value in os.environ.items()}
    for key in ("CL", "_CL_", "LINK", "_LINK_", "LIBPATH"):
        result.pop(key, None)
    result.update(VCTOOLSINSTALLDIR=str(vc) + os.sep, WINDOWSSDKDIR=str(sdk) + os.sep,
                  WINDOWSSDKVERSION=version + os.sep, VSCMD_ARG_TGT_ARCH="x64", VSCMD_ARG_HOST_ARCH="x64")
    result["INCLUDE"] = os.pathsep.join(map(str, [vc / "include", vc / "atlmfc/include", *[
        sdk / "Include" / version / p for p in ("ucrt", "shared", "um", "winrt")]]))
    result["LIB"] = os.pathsep.join(map(str, [vc / "lib/x64", vc / "atlmfc/lib/x64",
        sdk / "Lib" / version / "um/x64", sdk / "Lib" / version / "ucrt/x64"]))
    result["PATH"] = os.pathsep.join([str(compiler), str(sdk_bin), str(Path(qt) / "bin"), result["PATH"]])
    result["CL"] = "/MP8"
    return result
