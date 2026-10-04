"""Collect captured firmware link inputs in a new portable directory; no upload or serial access."""
import argparse
import hashlib
import json
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def prepare(capture, destination):
    capture, destination = Path(capture).resolve(), Path(destination).resolve()
    record = json.loads(capture.read_text(encoding="utf-8"))
    if record.get("format") != 1 or record.get("hardwareExecuted") is not False:
        raise ValueError("Expected the captured build-only firmware link")
    old_root = record["workingDirectory"].replace("\\", "/").removesuffix("/firmware/m5go")
    if old_root == record["workingDirectory"].replace("\\", "/"):
        raise ValueError("Unexpected firmware working directory")
    def local(path):
        normalized = path.replace("\\", "/")
        if not normalized.startswith(old_root + "/"):
            raise ValueError("Captured path is outside the recorded workspace")
        result = (ROOT / normalized[len(old_root) + 1:]).resolve()
        if not result.is_relative_to(ROOT):
            raise ValueError("Captured path escapes the workspace")
        return result
    framework = local(record["frameworkDirectory"])
    toolchain = local(record["toolchainDirectory"])
    build = capture.parent
    if digest(build / "firmware.elf") != record["elfSha256"]:
        raise ValueError("Captured ELF changed")
    destination.mkdir(parents=True, exist_ok=False)
    inputs = {}
    for base, prefix in ((build, "objects"), (framework / "tools/sdk/esp32", "sdk")):
        for path in sorted(base.rglob("*")):
            relative = path.relative_to(base)
            if not path.is_file() or path.suffix not in (".o", ".a", ".ld"):
                continue
            if prefix == "sdk" and relative.parts[0] not in ("lib", "ld", "qio_qspi"):
                continue
            name = prefix + "/" + relative.as_posix()
            target = destination / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(path, target)
            inputs[name] = digest(target)
    command = record["command"].copy()
    command[0] = "{toolchain}/bin/xtensa-esp32-elf-g++.exe"
    command[command.index("-o") + 1] = "{output}/firmware.elf"
    build_prefix = (old_root + "/" + build.relative_to(ROOT).as_posix())
    sdk_prefix = record["frameworkDirectory"].replace("\\", "/") + "/tools/sdk/esp32"
    for index, value in enumerate(command):
        value = value.replace("\\", "/")
        if value.startswith("-Wl,-Map="):
            value = "-Wl,-Map={output}/firmware.map"
        value = value.replace(build_prefix, "{bundle}/objects").replace(sdk_prefix, "{bundle}/sdk")
        if old_root in value or str(ROOT).replace("\\", "/") in value:
            raise ValueError("Nonportable link input: " + value)
        command[index] = value
    toolchain_files = {p.relative_to(toolchain).as_posix(): digest(p) for p in sorted(toolchain.rglob("*"))
                       if p.is_file() and "__pycache__" not in p.parts and p.name != ".piopm"}
    result = {"format": 1, "command": command, "files": inputs, "toolchainFiles": toolchain_files,
              "toolchainVersion": "8.4.0+2021r2-patch5", "elfSha256": record["elfSha256"],
              "firmwareBinSha256": digest(build / "firmware.bin"), "captureSha256": digest(capture), "hardwareAccessed": False}
    (destination / "relink.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    shutil.copyfile(ROOT / "scripts/relink-firmware.py", destination / "relink-firmware.py")
    print(json.dumps({"bundleFiles": len(inputs), "toolchainFiles": len(toolchain_files), "output": str(destination)}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("captured_link_json")
    parser.add_argument("new_directory")
    args = parser.parse_args()
    prepare(args.captured_link_json, args.new_directory)
