"""Seed native receipts and keep ASRock Native/Direct encoding aligned with the actual color order."""
import argparse
import difflib
import importlib.util
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DIR = 'Controllers/ASRockPolychromeUSBController/'


def module(name, file):
    spec = importlib.util.spec_from_file_location(name, ROOT / 'scripts' / file)
    value = importlib.util.module_from_spec(spec); spec.loader.exec_module(value); return value


def patch(archive, destination):
    operation = module('operation', 'prepare-openrgb-operation-source.py')
    operation.patch(archive, destination)
    prepare = module('prepare', 'prepare-openrgb-source.py'); once = operation.once
    destination = Path(destination)
    names = [DIR + n for n in ['ASRockPolychromeUSBController.cpp', 'RGBController_ASRockPolychromeUSB.cpp', 'FlowDeckAsrockOperation.h']]
    original = {n: (destination / n).read_text(encoding='utf-8') for n in names}; updates = dict(original)
    source = updates[DIR + 'ASRockPolychromeUSBController.cpp']
    if source.count('if(device_info.rgswap)') != 3: raise ValueError('Unexpected color encode/decode sites')
    source = source.replace('if(device_info.rgswap)', 'if((configtable[8] & (1u << device_info.zone_type)) != 0)')
    anchor = '    WriteHeader(POLYCHROME_USB_RGSWAP_CFG, rgconfig, 1);'
    source = once(source, anchor, anchor + '\n    configtable[8] = rgconfig[0]; // Update effective wire order only after the checked reply.')
    updates[DIR + 'ASRockPolychromeUSBController.cpp'] = source
    header = updates[DIR + 'FlowDeckAsrockOperation.h']
    header = once(header, 'public:\n', '''public:
    void Initialize(const FlowDeckAsrockIoSnapshot& initial)
    {
        std::lock_guard<std::mutex> operation_lock(operation_mutex);
        if(initialized || initial.faulted || initial.attempted == 0 || initial.attempted != initial.completed ||
           initial.stage != FlowDeckAsrockIoStage::ReplyReceived)
            throw std::runtime_error("Invalid initial ASRock IO receipt");
        FlowDeckOperationSnapshot value; value.before = value.after = initial;
        Store(value); initialized = true;
    }
''')
    header = once(header, '        auto current = Snapshot();', '        if(!initialized) return false;\n        auto current = Snapshot();\n        const auto previous_io = current.after;')
    header = once(header, '            if(current.before.faulted || current.before.attempted != current.before.completed)',
                  '            if(!SameIo(previous_io, current.before) || current.before.faulted || current.before.attempted != current.before.completed)')
    header = once(header, 'private:\n', '''private:
    static bool SameIo(const FlowDeckAsrockIoSnapshot& a, const FlowDeckAsrockIoSnapshot& b)
    {
        return a.attempted == b.attempted && a.completed == b.completed && a.stage == b.stage &&
            a.faulted == b.faulted && a.write_result == b.write_result && a.read_result == b.read_result &&
            a.request == b.request && a.response == b.response;
    }
    bool initialized = false;
''')
    updates[DIR + 'FlowDeckAsrockOperation.h'] = header
    source = updates[DIR + 'RGBController_ASRockPolychromeUSB.cpp']
    source = once(source, '    SetupZones();\n}', '    SetupZones();\n    flowdeck_operation.Initialize(controller->FlowDeckIoState());\n}')
    source = once(source, '     active_mode = zones_info.size() > 1 ? zones_info[0].mode : 0x0;', '''     active_mode = 0;
     for(std::size_t i = 0; i < zones.size(); ++i)
     {
         if(zones[i].leds_count > 0) { active_mode = zones_info[i].mode; break; }
     }''')
    # Mode belongs to the current global request, not to the first potentially empty physical zone.
    source = once(source, 'if(POLYCHROME_USB_MODE_DIRECT == zones_info[0].mode )', 'if(POLYCHROME_USB_MODE_DIRECT == modes[active_mode].value)')
    mode_check = 'if(active_mode < 0 || static_cast<std::size_t>(active_mode) >= modes.size()) throw std::out_of_range("ASRock mode");'
    for name in ['DeviceUpdateLEDs()', 'DeviceUpdateZoneLEDs(int zone)', 'DeviceUpdateSingleLED(int led)']:
        signature = 'void RGBController_PolychromeUSB::' + name
        old = operation.function(source, signature)
        opening = old.index('    {\n', old.index('flowdeck_operation.Run(')) + len('    {\n')
        body = old[:opening] + '        ' + mode_check + '\n' + old[opening:]
        if name != 'DeviceUpdateLEDs()':
            # Keep index validation before the branch; an invalid single/zone request is never a full-device write.
            guard_end = body.index('\n', body.index('throw std::out_of_range("ASRock ' + ('zone' if name.startswith('DeviceUpdateZone') else 'LED') + '");'))
            condition = ' && zones[zone].leds_count > 0' if name.startswith('DeviceUpdateZone') else ''
            body = body[:guard_end] + '''
        if(POLYCHROME_USB_MODE_DIRECT == modes[active_mode].value''' + condition + ''')
        {
            controller->WriteAllZones(zones_info, zones);
            return;
        }''' + body[guard_end:]
        source = once(source, old, body)
    updates[DIR + 'RGBController_ASRockPolychromeUSB.cpp'] = source
    diff = (destination / 'flowdeck.patch').read_text(encoding='utf-8')
    for name, text in updates.items():
        (destination / name).write_text(text, encoding='utf-8', newline='\n')
        diff += ''.join(difflib.unified_diff(original[name].splitlines(True), text.splitlines(True), fromfile='a/' + name, tofile='b/' + name))
    (destination / 'flowdeck.patch').write_text(diff, encoding='utf-8', newline='\n')
    manifest = destination / 'flowdeck-source.json'; receipt = json.loads(manifest.read_text(encoding='utf-8'))
    entries = {e['path']: e for e in receipt['files']}
    for name in updates:
        data = (destination / name).read_bytes(); entries[name] = {'path': name, 'length': len(data), 'sha256': prepare.sha256(data)}
    receipt.update(flowDeckPatch=4, files=[entries[n] for n in sorted(entries)], patchSha256=prepare.sha256(diff.encode('utf-8')))
    manifest.write_text(json.dumps(receipt, indent=2)+'\n', encoding='utf-8'); prepare.verify(destination)
    print(json.dumps({'flowDeckPatch':4,'files':len(entries),'compiled':False}))


if __name__ == '__main__':
    parser=argparse.ArgumentParser(description=__doc__); parser.add_argument('archive'); parser.add_argument('new_directory')
    args=parser.parse_args(); patch(args.archive,args.new_directory)
