"""Make a new copy of verified patch 8 with a source-built PawnIOLib. Does not run hardware code."""
import argparse
import importlib.util
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def prepare(original, pawn_build, destination):
    spec = importlib.util.spec_from_file_location("openrgb_prepare", ROOT / "scripts/prepare-openrgb-source.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    original, pawn_build, destination = (Path(p).resolve() for p in (original, pawn_build, destination))
    receipt = module.verify(original)
    pawn = json.loads((pawn_build / "build-result.json").read_text(encoding="utf-8"))
    if receipt["flowDeckPatch"] != 8 or receipt.get("buildVariant") or pawn["sourceRevision"] != "aef64eb8810d93791dd24d8c318db2e3b119b264":
        raise ValueError("Expected patch 8 and the pinned PawnIOLib build")
    updates = {}
    for name in ("PawnIOLib.dll", "PawnIOLib.lib"):
        data = (pawn_build / name).read_bytes()
        if module.sha256(data).lower() != pawn["files"][name]:
            raise ValueError("PawnIOLib build changed")
        updates["dependencies/PawnIO/" + name] = data
    destination.mkdir(parents=True, exist_ok=False)
    for entry in receipt["files"]:
        name = entry["path"]
        data = updates.pop(name, None)
        if data is None:
            data = (original / name).read_bytes()
        target = destination / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
        entry.update(length=len(data), sha256=module.sha256(data))
    if updates:
        raise ValueError("Original source is missing a required PawnIOLib file")
    (destination / "flowdeck.patch").write_bytes((original / "flowdeck.patch").read_bytes())
    receipt.update(buildVariant="stable1", pawnIoBuild=pawn,
                   originalSourceManifestSha256=module.sha256((original / "flowdeck-source.json").read_bytes()))
    (destination / "flowdeck-source.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    module.verify(destination)
    print(json.dumps({"flowDeckPatch": 8, "buildVariant": "stable1", "files": len(receipt["files"]), "hardwareAccessed": False}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("verified_patch8_source")
    parser.add_argument("pawnio_build")
    parser.add_argument("new_directory")
    args = parser.parse_args()
    prepare(args.verified_patch8_source, args.pawnio_build, args.new_directory)
