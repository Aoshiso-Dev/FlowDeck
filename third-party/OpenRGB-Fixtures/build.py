"""Compile the bundled mock-hardware fixtures with MSVC; never execute them.

Run from an x64 Visual Studio Native Tools prompt with Python 3.10 or newer.
The destination must not exist. Existing test binaries are never replaced.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess

HERE = Path(__file__).resolve().parent
PROJECT = HERE.parent.parent


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def verify():
    manifest = json.loads((HERE / "manifest.json").read_text(encoding="utf-8"))
    for item in manifest["files"]:
        path = (HERE / item["path"]).resolve()
        if not path.is_relative_to(HERE) or digest(path) != item["sha256"]:
            raise ValueError("Source material changed: " + item["path"])
    for item in manifest["fixtures"]:
        path = (PROJECT / item["binaryPath"]).resolve()
        if not path.is_relative_to(PROJECT) or digest(path) != item["binarySha256"]:
            raise ValueError("Reference fixture changed: " + item["binaryPath"])
    return manifest


def build(destination):
    manifest = verify()
    destination = Path(destination).resolve()
    if destination.exists() or destination.is_relative_to(HERE):
        raise ValueError("Use a new output directory outside this source bundle")
    compiler = shutil.which("cl.exe")
    if not compiler or os.environ.get("VSCMD_ARG_TGT_ARCH", "").lower() != "x64":
        raise RuntimeError("Run from an x64 Visual Studio Native Tools prompt")
    destination.mkdir(parents=True)
    shutil.copytree(HERE / "shared", destination / "shared")
    env = os.environ.copy()
    for key in ("CL", "_CL_", "LINK", "_LINK_"):
        env.pop(key, None)
    results = []
    for fixture in manifest["fixtures"]:
        name = fixture["directory"]
        source = HERE / name
        work = destination / name
        shutil.copytree(source, work)
        command = [compiler, "/nologo", "/std:c++17", "/EHsc", "/W4", "/utf-8",
                   "/permissive-", "/MT", "/O2", "/I.",
                   "/I../shared", *fixture["compilerDefines"],
                   *fixture["compilationUnits"], "/Fe:" + fixture["outputName"]]
        if fixture["libraries"]:
            command += ["/link", *fixture["libraries"]]
        result = subprocess.run(command, cwd=work, env=env, capture_output=True)
        (work / "build.log").write_bytes(result.stdout + result.stderr)
        if result.returncode:
            raise RuntimeError("Compilation failed; see " + str(work / "build.log"))
        output = work / fixture["outputName"]
        results.append({"fixture": name, "builtSha256": digest(output),
                        "referenceSha256": fixture["binarySha256"],
                        "byteIdentical": digest(output) == fixture["binarySha256"],
                        "executed": False})
    receipt = {"sourceManifestSha256": digest(HERE / "manifest.json"),
               "hardwareExecuted": False, "referenceBinariesReplaced": False,
               "results": results}
    (destination / "build-verification.json").write_text(
        json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(receipt, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", nargs="?")
    parser.add_argument("--verify-only", action="store_true")
    args = parser.parse_args()
    if args.verify_only:
        verified = verify()
        print(json.dumps({"files": len(verified["files"]),
                          "referenceBinaries": len(verified["fixtures"]),
                          "verified": True}))
    elif not args.destination:
        parser.error("destination is required unless --verify-only is used")
    else:
        build(args.destination)
