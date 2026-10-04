"""Install the exact parent authorization channel before the Windows helper enters startup."""
import argparse
import difflib
import importlib.util
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DIR = 'Controllers/ASRockPolychromeUSBController/'


def module(name, file):
    spec = importlib.util.spec_from_file_location(name, ROOT/'scripts'/file)
    value = importlib.util.module_from_spec(spec); spec.loader.exec_module(value); return value


def patch(archive, destination):
    module('authorization', 'prepare-openrgb-authorization-source.py').patch(archive, destination)
    once = module('operation', 'prepare-openrgb-operation-source.py').once
    prepare = module('prepare', 'prepare-openrgb-source.py'); destination = Path(destination)
    name = 'startup/main_Windows.cpp'
    original = (destination/name).read_text(encoding='utf-8')
    main = once(original, '#include "startup.h"', '#include "startup.h"\n#include "../'+DIR+'FlowDeckAsrockBootstrap.h"')
    main = once(main, 'int main(int argc, char* argv[])\n{', '''int main(int argc, char* argv[])
{
    // This private helper must authenticate its owning parent before any startup/detection.
    try { FlowDeckAsrockBootstrap::InstallFromEnvironment(); }
    catch(...) { return EXIT_FAILURE; }''')
    updates = {name: main}
    for header in ['FlowDeckAsrockWire.h', 'FlowDeckAsrockPipe.h', 'FlowDeckAsrockBootstrap.h']:
        updates[DIR+header] = (ROOT/'native/OpenRgb'/header).read_text(encoding='utf-8')
    diff = (destination/'flowdeck.patch').read_text(encoding='utf-8')
    manifest = destination/'flowdeck-source.json'; receipt = json.loads(manifest.read_text(encoding='utf-8'))
    entries = {e['path']: e for e in receipt['files']}
    for path, content in updates.items():
        (destination/path).write_text(content, encoding='utf-8', newline='\n')
        diff += ''.join(difflib.unified_diff((original if path == name else '').splitlines(True), content.splitlines(True), fromfile='a/'+path, tofile='b/'+path))
        data = (destination/path).read_bytes(); entries[path] = {'path':path,'length':len(data),'sha256':prepare.sha256(data)}
    (destination/'flowdeck.patch').write_text(diff, encoding='utf-8', newline='\n')
    receipt.update(flowDeckPatch=6, files=[entries[n] for n in sorted(entries)], patchSha256=prepare.sha256(diff.encode('utf-8')))
    manifest.write_text(json.dumps(receipt, indent=2)+'\n', encoding='utf-8'); prepare.verify(destination)
    print(json.dumps({'flowDeckPatch':6,'files':len(entries),'compiled':False,'parentChannelInstalled':True}))


if __name__ == '__main__':
    parser=argparse.ArgumentParser(description=__doc__); parser.add_argument('archive'); parser.add_argument('new_directory')
    args=parser.parse_args(); patch(args.archive,args.new_directory)
