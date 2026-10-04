"""Relink the saved application objects with replaceable libraries. Never access a device."""
import argparse
import hashlib
import json
import os
import subprocess
from pathlib import Path


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def relink(bundle, toolchain, output, allow_modified=False):
    # Keep an ASCII junction path intact: this older GCC cannot use every Unicode path.
    bundle, toolchain, output = (Path(os.path.abspath(p)) for p in (bundle, toolchain, output))
    if any(not str(p).isascii() for p in (bundle, toolchain, output)):
        raise ValueError("Extract the bundle and toolchain to ASCII paths (spaces are supported)")
    if output.exists():
        raise ValueError("Relink output must be a new directory")
    record = json.loads((bundle / "relink.json").read_text(encoding="utf-8"))
    if record.get("format") != 1:
        raise ValueError("Unsupported relink record")
    changed = []
    for label, base, files in (("bundle", bundle, record["files"]), ("toolchain", toolchain, record["toolchainFiles"])):
        for relative, expected in files.items():
            path = (base / relative)
            if not path.resolve().is_relative_to(base.resolve()):
                raise ValueError("Input escapes its directory")
            if not path.is_file() or digest(path) != expected:
                changed.append(label + "/" + relative)
    if changed and not allow_modified:
        raise ValueError("Inputs differ; use --allow-modified only when intentionally relinking modified libraries: " + ", ".join(changed[:10]))
    output.mkdir(parents=True)
    substitutions = {"{bundle}": bundle.as_posix(), "{toolchain}": toolchain.as_posix(), "{output}": output.as_posix()}
    command = []
    for value in record["command"]:
        for key, path in substitutions.items():
            value = value.replace(key, path)
        command.append(value)
    response = output / "link.rsp"
    response.write_text("\n".join('"' + item.replace('"', '\\"') + '"' for item in command[1:]), encoding="utf-8")
    result = subprocess.run([command[0], "@" + str(response)], cwd=bundle, capture_output=True)
    (output / "link.log").write_bytes(result.stdout + result.stderr)
    if result.returncode:
        raise RuntimeError("Link failed; see " + str(output / "link.log"))
    actual = digest(output / "firmware.elf")
    verification = {"format": 1, "elfSha256": actual, "originalElfSha256": record["elfSha256"],
                    "identical": actual == record["elfSha256"], "modifiedInputs": changed, "hardwareAccessed": False}
    (output / "verification.json").write_text(json.dumps(verification, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(verification))
    if not allow_modified and not verification["identical"]:
        raise RuntimeError("Original inputs did not reproduce the original ELF")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("bundle")
    parser.add_argument("toolchain")
    parser.add_argument("new_output")
    parser.add_argument("--allow-modified", action="store_true")
    args = parser.parse_args()
    relink(args.bundle, args.toolchain, args.new_output, args.allow_modified)
