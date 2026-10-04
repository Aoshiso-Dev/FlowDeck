"""Opt-in PlatformIO post script: record the actual link inputs without touching hardware."""
import hashlib
import json
from pathlib import Path
import re

Import("env")


def capture(source, target, env):
    template = str(env["LINKCOM"])
    wrapped = re.fullmatch(r"\$\{TEMPFILE\('(.*)','\$LINKCOMSTR'\)\}", template)
    if wrapped:
        template = wrapped[1]
    if "TEMPFILE" in template:
        raise ValueError("Unrecognized linker command wrapper")
    commands = env.subst_list(template, target=target, source=source)
    if len(commands) != 1:
        raise ValueError("Expected one linker command")
    command = [str(arg) for arg in commands[0]]
    compiler = env.WhereIs(command[0])
    if not compiler:
        raise ValueError("Linker executable is missing")
    command[0] = str(Path(compiler).resolve())
    elf = Path(str(target[0])).resolve()
    record = {
        "format": 1,
        "hardwareExecuted": False,
        "workingDirectory": str(Path.cwd()),
        "command": command,
        "linkerSha256": hashlib.sha256(Path(compiler).read_bytes()).hexdigest(),
        "elf": str(elf),
        "elfSha256": hashlib.sha256(elf.read_bytes()).hexdigest(),
        "sourceObjects": [str(Path(str(node)).resolve()) for node in source],
        "frameworkDirectory": env.PioPlatform().get_package_dir("framework-arduinoespressif32"),
        "toolchainDirectory": env.PioPlatform().get_package_dir("toolchain-xtensa-esp32"),
    }
    output = elf.parent / "relink-inputs.json"
    output.write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")


env.AddPostAction("$BUILD_DIR/${PROGNAME}.elf", capture)
