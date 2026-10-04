"""Read the pinned upstream source archive; never execute upstream code or touch hardware."""
import hashlib
import json
import re
import sys
import zipfile
from pathlib import Path

REVISION = "81bbe18a84c2e507006f19dd252e397e40a56bfe"
ARCHIVE_HASH = "2f02829016ec955c301c5fe27f9cbbcb086efa923e81a8f91b96b001bf59eaee"
TOKENS = re.compile(r'"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'|//[^\n]*|/\*[\s\S]*?\*/')
CALL = re.compile(r'\b(REGISTER_[A-Z0-9_]*DETECTOR[A-Z0-9_]*)\s*\(\s*((?:"(?:\\.|[^"\\])*"\s*)+|[^,\n]*)\s*,')

def inspect(archive):
    data = Path(archive).read_bytes()
    if hashlib.sha256(data).hexdigest() != ARCHIVE_HASH:
        raise ValueError("Not the pinned release source archive")
    entries = []
    unresolved = []
    with zipfile.ZipFile(archive) as source:
        for item in source.infolist():
            path = item.filename.removeprefix(f"OpenRGB-{REVISION}/")
            if not path.startswith("Controllers/") or not path.endswith((".cpp", ".h")):
                continue
            # A superset is intentional: C++ conditional registrations may be removed by the
            # Windows build. Unused false entries are pruned by upstream, never enabled here.
            if any(platform in Path(path).name for platform in ("_Linux", "_FreeBSD", "_MacOS")):
                continue
            text = source.read(item).decode("utf-8-sig").replace("\r\n", "\n")
            text = TOKENS.sub(lambda m: "\n" * m[0].count("\n") if m[0].startswith(("//", "/*")) else m[0], text)
            defines = dict(re.findall(r'(?m)^\s*#define\s+(\w+)\s+((?:"(?:\\.|[^"\\])*"[ \t]*)+)$', text))
            for match in CALL.finditer(text):
                macro, expression = match.groups()
                if text[text.rfind("\n", 0, match.start()) + 1:match.start()].lstrip().startswith("#define"):
                    continue
                if macro.startswith("REGISTER_DYNAMIC_"):
                    continue  # QMK dynamic entries are excluded by an empty startup configuration.
                expression = defines.get(expression.strip(), expression)
                if not expression.startswith('"'):
                    unresolved.append({"path": path, "macro": macro, "expression": expression.strip()})
                    continue
                name = "".join(json.loads(s) for s in re.findall(r'"(?:\\.|[^"\\])*"', expression))
                entries.append({"name": name, "macro": macro, "path": path})
    if unresolved:
        raise ValueError(json.dumps({"unresolved": unresolved}, ensure_ascii=False))
    return {"version": 1, "sourceRevision": REVISION, "sourceArchiveSha256": ARCHIVE_HASH.upper(),
            "platform": "windows-x64-conditional-superset", "registrations": entries,
            "names": sorted({e["name"] for e in entries})}

if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit("inspect-openrgb-detectors.py <pinned-source.zip> <new-output.json>")
    result = inspect(sys.argv[1])
    with Path(sys.argv[2]).open("x", encoding="utf-8") as output:
        json.dump(result, output, ensure_ascii=False, indent=2)
        output.write("\n")
    print(json.dumps({"names": len(result["names"]), "registrations": len(result["registrations"])}))
