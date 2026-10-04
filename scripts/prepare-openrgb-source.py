"""Prepare a reproducible source-only fork. Never run OpenRGB or access hardware."""
import argparse
import difflib
import hashlib
import json
import stat
import zipfile
from pathlib import Path, PurePosixPath

REVISION = "81bbe18a84c2e507006f19dd252e397e40a56bfe"
ARCHIVE_SHA256 = "2f02829016ec955c301c5fe27f9cbbcb086efa923e81a8f91b96b001bf59eaee"
DETECTOR = "Controllers/ASRockPolychromeUSBController/ASRockPolychromeUSBControllerDetect.cpp"
GUARD = "Controllers/ASRockPolychromeUSBController/FlowDeckHidAllowlist.h"
PATCH_VERSION = 1
HOOK = '''    // FlowDeck patch 1: no path outside the durable pre-detection capture may open.
    // The startup snapshot is immutable and each path can be attempted only once per process.
    if(info == nullptr || info->vendor_id != ASROCK_VID || info->product_id != ASROCK_MOTHERBOARD_1_PID)
        return detected_controllers;
    static FlowDeckHidAllowlist captured([]() -> nlohmann::json
    {
        try
        {
            auto settings = ResourceManager::get()->GetSettingsManager()->GetSettings("ASRock Polychrome USB");
            if(settings.is_object() && settings.contains("FlowDeck")) return settings["FlowDeck"];
        }
        catch(...) { }
        return nullptr;
    }());
    if(!captured.TryClaim(info->path)) return detected_controllers;

'''


def sha256(data):
    return hashlib.sha256(data).hexdigest().upper()


def prepare(archive, destination):
    archive, destination = Path(archive), Path(destination)
    raw = archive.read_bytes()
    if sha256(raw).lower() != ARCHIVE_SHA256:
        raise ValueError("Not the pinned OpenRGB 1.0 source archive")
    if destination.exists() or not destination.parent.is_dir():
        raise ValueError("Destination must be new and its parent must exist")
    prefix = f"OpenRGB-{REVISION}/"
    contents = {}
    folded = set()
    # Read the already-hashed bytes so replacing the input archive cannot change extraction.
    import io
    with zipfile.ZipFile(io.BytesIO(raw)) as source:
        for entry in source.infolist():
            if not entry.filename.startswith(prefix):
                raise ValueError("Unexpected source archive root")
            relative = entry.filename[len(prefix):]
            if not relative or entry.is_dir():
                continue
            path = PurePosixPath(relative)
            mode = stat.S_IFMT(entry.external_attr >> 16)
            if path.is_absolute() or any(p in (".", "..") for p in path.parts) or ":" in relative or "\\" in relative or mode not in (0, stat.S_IFREG):
                raise ValueError("Unsafe source archive entry")
            if relative.lower() in folded:
                raise ValueError("Duplicate source archive entry")
            folded.add(relative.lower())
            contents[relative] = source.read(entry)
    before = contents[DETECTOR]
    original = before.decode("utf-8-sig").replace("\r\n", "\n")
    include = '#include "RGBController_ASRockPolychromeUSB.h"\n'
    opening = "    dev = hid_open_path(info->path);\n"
    if original.count(include) != 1 or original.count(opening) != 1 or GUARD in contents:
        raise ValueError("Pinned detector source does not match the patch anchors")
    patched = original.replace(include, include + '#include "FlowDeckHidAllowlist.h"\n#include "ResourceManager.h"\n#include "SettingsManager.h"\n')
    patched = patched.replace(opening, HOOK + opening)
    contents[DETECTOR] = patched.encode("utf-8")
    guard = Path(__file__).resolve().parents[1] / "native/OpenRgb/FlowDeckHidAllowlist.h"
    contents[GUARD] = guard.read_bytes()
    patch = "".join(difflib.unified_diff(original.splitlines(True), patched.splitlines(True),
                                      fromfile="a/" + DETECTOR, tofile="b/" + DETECTOR))
    receipt = {"sourceRevision": REVISION, "sourceArchiveSha256": sha256(raw), "flowDeckPatch": PATCH_VERSION,
               "originalDetectorSha256": sha256(before), "patchSha256": sha256(patch.encode()),
               "files": [{"path": name, "length": len(data), "sha256": sha256(data)} for name, data in sorted(contents.items())],
               "compiled": False, "executed": False, "hardwareAccessed": False}
    destination.mkdir()
    for name, data in contents.items():
        target = destination / name
        target.parent.mkdir(parents=True, exist_ok=True)
        with target.open("xb") as output:
            output.write(data)
    (destination / "flowdeck.patch").write_text(patch, encoding="utf-8", newline="\n")
    (destination / "flowdeck-source.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8", newline="\n")
    verify(destination)
    return {"path": str(destination.resolve()), "files": len(contents), "flowDeckPatch": PATCH_VERSION,
            "detectorSha256": sha256(contents[DETECTOR]), "guardSha256": sha256(contents[GUARD]), "compiled": False}


def verify(destination):
    destination = Path(destination)
    receipt = json.loads((destination / "flowdeck-source.json").read_text(encoding="utf-8"))
    if receipt["sourceRevision"] != REVISION or receipt["sourceArchiveSha256"].lower() != ARCHIVE_SHA256 or receipt["flowDeckPatch"] not in (1, 2, 3, 4, 5, 6, 7, 8):
        raise ValueError("Unexpected prepared source revision")
    for entry in receipt["files"]:
        data = (destination / entry["path"]).read_bytes()
        if len(data) != entry["length"] or sha256(data) != entry["sha256"]:
            raise ValueError("Prepared source changed: " + entry["path"])
    if sha256((destination / "flowdeck.patch").read_bytes()) != receipt["patchSha256"]:
        raise ValueError("Prepared patch changed")
    return receipt


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive")
    parser.add_argument("new_directory")
    args = parser.parse_args()
    print(json.dumps(prepare(args.archive, args.new_directory)))
