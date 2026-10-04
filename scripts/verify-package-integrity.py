"""Verify both the published folder and ZIP against their SHA256 manifest."""
import hashlib
import json
from pathlib import Path
import sys
import zipfile

package = Path(sys.argv[1]).resolve()
archive = Path(sys.argv[2]).resolve()
manifest_bytes = (package / "SHA256SUMS.txt").read_bytes()
manifest = manifest_bytes.decode("utf-8-sig")
entries = dict(line.split("  ", 1)[::-1] for line in manifest.splitlines() if line)
with zipfile.ZipFile(archive) as zipped:
    assert zipped.read("SHA256SUMS.txt") == manifest_bytes
    for relative, expected in entries.items():
        file = (package / relative).resolve()
        assert file.is_relative_to(package), relative
        assert hashlib.sha256(file.read_bytes()).hexdigest() == expected, relative
        assert hashlib.sha256(zipped.read(relative.replace("\\", "/"))).hexdigest() == expected, relative
    names = {name for name in zipped.namelist() if not name.endswith("/")}
    assert names == {name.replace("\\", "/") for name in entries} | {"SHA256SUMS.txt"}
print(json.dumps({"package": str(package), "zip": str(archive), "verifiedFiles": len(entries),
                  "zipSha256": hashlib.sha256(archive.read_bytes()).hexdigest()}, indent=2))
