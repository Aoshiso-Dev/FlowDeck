"""Download official, versioned Qt build archives into the workspace. No installer/UAC."""
import concurrent.futures
import hashlib
import json
import re
import subprocess
import urllib.request
from pathlib import Path, PurePosixPath

ROOT = Path(__file__).resolve().parents[1]
BASE = "https://download.qt.io/online/qtsdkrepository/windows_x86/desktop/qt6_683/qt6_683/qt.qt6.683.win64_msvc2022_64/"
SUFFIX = "-Windows-Windows_11_23H2-MSVC2022-Windows-Windows_11_23H2-X86_64.7z"
CACHE = ROOT / "artifacts/qt-6.8.3-archives"
DESTINATION = ROOT / ".tools/qt-6.8.3"


def fetch(module):
    name = "6.8.3-0-202503201308" + module + SUFFIX
    url = BASE + name
    with urllib.request.urlopen(url + ".sha1", timeout=30) as response:
        expected = response.read(256).decode("ascii").strip().lower()
    if not re.fullmatch("[0-9a-f]{40}", expected):
        raise ValueError("Invalid published archive checksum")
    path = CACHE / name
    if not path.exists():
        partial = path.with_suffix(path.suffix + ".partial")
        length = 0
        with urllib.request.urlopen(url, timeout=30) as response, partial.open("xb") as output:
            while chunk := response.read(1024 * 1024):
                length += len(chunk)
                if length > 200 * 1024 * 1024:
                    raise ValueError("Unexpectedly large SDK archive")
                output.write(chunk)
        if hashlib.sha1(partial.read_bytes()).hexdigest() != expected:
            raise ValueError("Official Qt archive checksum mismatch")
        partial.rename(path)
    data = path.read_bytes()
    if hashlib.sha1(data).hexdigest() != expected:
        raise ValueError("Cached Qt archive checksum mismatch")
    listing = subprocess.run(["tar.exe", "-tf", str(path)], capture_output=True, text=True, check=True)
    for name in listing.stdout.splitlines():
        p = PurePosixPath(name)
        if p.is_absolute() or ".." in p.parts or ":" in name or "\\" in name or not p.parts or p.parts[0] not in {
                "bin", "doc", "include", "lib", "metatypes", "mkspecs", "modules", "plugins", "sbom", "phrasebooks"}:
            raise ValueError("Unexpected SDK archive path: " + name)
    metadata = subprocess.run(["tar.exe", "-tvf", str(path)], capture_output=True, text=True, check=True)
    if any(line and line[0] not in "-d" for line in metadata.stdout.splitlines()):
        raise ValueError("SDK archive contains links or special files")
    print(json.dumps({"module": module, "downloadedAndVerified": True, "bytes": len(data)}), flush=True)
    return {"module": module, "url": url, "archive": str(path.relative_to(ROOT)), "length": len(data),
            "publishedSha1": expected.upper(), "sha256": hashlib.sha256(data).hexdigest().upper()}


def main():
    if DESTINATION.exists():
        raise ValueError("SDK destination must be new")
    CACHE.mkdir(exist_ok=True)
    with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
        records = list(pool.map(fetch, ["qtbase", "qttools", "qtsvg"]))
    qt = DESTINATION / "6.8.3/msvc2022_64"
    qt.mkdir(parents=True)
    for record in records:
        subprocess.run(["tar.exe", "-xf", str(ROOT / record["archive"]), "-C", str(qt)], check=True)
    qmake = qt / "bin/qmake.exe"
    info = subprocess.run([str(qmake), "-query"], capture_output=True, text=True, check=True)
    result = {"version": "6.8.3", "architecture": "msvc2022_64", "archives": records, "qmakeQuery": info.stdout,
              "qtDirectory": str(qt.relative_to(ROOT)), "installerUsed": False, "uacRequested": False}
    (CACHE / "prepared-sdk.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"prepared": True, "qtDirectory": result["qtDirectory"]}), flush=True)


if __name__ == "__main__":
    main()
