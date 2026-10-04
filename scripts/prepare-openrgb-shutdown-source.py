"""Make patch 8: private owned-process shutdown enters the real headless cleanup path."""
import argparse
import difflib
import importlib.util
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
def module(name, file):
    spec = importlib.util.spec_from_file_location(name, ROOT/'scripts'/file)
    value = importlib.util.module_from_spec(spec); spec.loader.exec_module(value); return value

def patch(archive, destination):
    lifecycle = module('lifecycle', 'prepare-openrgb-lifecycle-source.py')
    lifecycle.patch(archive, destination)
    once = module('operation', 'prepare-openrgb-operation-source.py').once
    prepare = module('prepare', 'prepare-openrgb-source.py')
    destination = Path(destination); name = 'startup/main_Windows.cpp'
    original = (destination/name).read_text(encoding='utf-8')
    main = once(original, '#include "startup.h"', '#include "startup.h"\n#include "../FlowDeckShutdown.h"')
    main = once(main, 'static int common_main(int argc, char* argv[]);',
        'static std::unique_ptr<FlowDeckShutdown> flowdeck_shutdown;\nstatic int common_main(int argc, char* argv[]);')
    main = once(main, 'try { FlowDeckAsrockBootstrap::InstallFromEnvironment(); }',
        'try { FlowDeckAsrockBootstrap::InstallFromEnvironment(); flowdeck_shutdown = std::make_unique<FlowDeckShutdown>(); }')
    main = lifecycle.change_function(main, 'WaitWhileServerOnline', lambda _: '''static void WaitWhileServerOnline(NetworkServer* srv)
{
    // Parent requests this only after SDK restoration and disconnection. Stop joins the
    // server workers before common_main calls ServiceShutdown and DetectionManager::Cleanup.
    if(!flowdeck_shutdown) throw std::runtime_error("Owned shutdown was not initialized");
    flowdeck_shutdown->RunServerUntilStopped(srv);
}''')
    updates = {name: main, 'FlowDeckShutdown.h': (ROOT/'native/OpenRgb/FlowDeckShutdown.h').read_text(encoding='utf-8')}
    diff = (destination/'flowdeck.patch').read_text(encoding='utf-8')
    manifest = destination/'flowdeck-source.json'; receipt = json.loads(manifest.read_text(encoding='utf-8'))
    entries = {e['path']:e for e in receipt['files']}
    for path, content in updates.items():
        (destination/path).write_text(content, encoding='utf-8', newline='\n')
        diff += ''.join(difflib.unified_diff((original if path == name else '').splitlines(True), content.splitlines(True), fromfile='a/'+path, tofile='b/'+path))
        data = (destination/path).read_bytes(); entries[path] = dict(path=path,length=len(data),sha256=prepare.sha256(data))
    (destination/'flowdeck.patch').write_text(diff, encoding='utf-8', newline='\n')
    receipt.update(flowDeckPatch=8,files=[entries[n] for n in sorted(entries)],patchSha256=prepare.sha256(diff.encode('utf-8')))
    manifest.write_text(json.dumps(receipt,indent=2)+'\n',encoding='utf-8'); prepare.verify(destination)
    print(json.dumps(dict(flowDeckPatch=8,files=len(entries),compiled=False,ownedShutdown=True)))

if __name__ == '__main__':
    parser=argparse.ArgumentParser(description=__doc__); parser.add_argument('archive'); parser.add_argument('new_directory')
    args=parser.parse_args(); patch(args.archive,args.new_directory)
