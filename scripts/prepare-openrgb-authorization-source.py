"""Require parent authorization at every ASRock HID boundary and preserve actual SDK origin."""
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
    module('color', 'prepare-openrgb-color-source.py').patch(archive, destination)
    operation = module('operation', 'prepare-openrgb-operation-source.py'); once = operation.once
    prepare = module('prepare', 'prepare-openrgb-source.py'); destination = Path(destination)
    names = [DIR + n for n in ['FlowDeckAsrockIo.h', 'ASRockPolychromeUSBController.cpp', 'ASRockPolychromeUSBControllerDetect.cpp']]
    names += ['NetworkServer.cpp']
    originals = {n: (destination / n).read_text(encoding='utf-8') for n in names}; updates = dict(originals)
    header = updates[DIR + 'FlowDeckAsrockIo.h']
    header = once(header, 'enum class FlowDeckAsrockIoStage', 'enum class FlowDeckAsrockIoPhase { BeforeWrite, BeforeRead, AfterRead };\nenum class FlowDeckAsrockIoStage')
    header = once(header, '    FlowDeckAsrockIo(Write writeFunction, Read readFunction) : write(std::move(writeFunction)), read(std::move(readFunction))', '''    using Authorize = std::function<void(FlowDeckAsrockIoPhase, const FlowDeckAsrockIoSnapshot&)>;
    FlowDeckAsrockIo(Write writeFunction, Read readFunction, Authorize authorizeFunction)
        : write(std::move(writeFunction)), read(std::move(readFunction)), authorize(std::move(authorizeFunction))''')
    header = once(header, 'if(!this->write || !this->read)', 'if(!this->write || !this->read || !this->authorize)')
    start = header.index('        ++state.attempted;'); end = header.index('        try\n', start)
    header = header[:start] + '''        auto next = state;
        ++next.attempted;
        next.stage = FlowDeckAsrockIoStage::WriteUncertain;
        next.write_result = next.read_result = -2;
        std::copy_n(packet, 65, next.request.begin());
        next.response.fill(0);
        // Denial here means no write was attempted: preserve the preceding IO receipt.
        authorize(FlowDeckAsrockIoPhase::BeforeWrite, next);
        state = next;
''' + header[end:]
    header = once(header, '            state.read_result = read(', '            authorize(FlowDeckAsrockIoPhase::BeforeRead, state);\n            state.read_result = read(')
    header = once(header, '            state.stage = FlowDeckAsrockIoStage::ReplyReceived;', '            authorize(FlowDeckAsrockIoPhase::AfterRead, state);\n            state.stage = FlowDeckAsrockIoStage::ReplyReceived;')
    header = once(header, '    const Read read;', '    const Read read;\n    const Authorize authorize;')
    updates[DIR + 'FlowDeckAsrockIo.h'] = header
    source = updates[DIR + 'ASRockPolychromeUSBController.cpp']
    source = once(source, '#include "ASRockPolychromeUSBController.h"', '#include "ASRockPolychromeUSBController.h"\n#include "FlowDeckAsrockAuthorization.h"')
    source = once(source, 'return hid_read_timeout(dev_handle, data, size, timeout); })', 'return hid_read_timeout(dev_handle, data, size, timeout); },\n         FlowDeckAsrockAuthorization::ForPath(path))')
    source = once(source, '    int ret = hid_get_serial_number_string', '    FlowDeckAsrockAuthorization::Require(FlowDeckAsrockAuthorizationPhase::BeforeSerial, location, flowdeck_io.Snapshot());\n    int ret = hid_get_serial_number_string')
    updates[DIR + 'ASRockPolychromeUSBController.cpp'] = source
    source = updates[DIR + 'ASRockPolychromeUSBControllerDetect.cpp']
    source = once(source, '#include "FlowDeckHidAllowlist.h"', '#include "FlowDeckHidAllowlist.h"\n#include "FlowDeckAsrockAuthorization.h"')
    anchor = '    if(!captured.TryClaim(info->path)) return detected_controllers;'
    source = once(source, anchor, anchor + '\n    FlowDeckAsrockAuthorization::Require(FlowDeckAsrockAuthorizationPhase::BeforeOpen, info->path);')
    updates[DIR + 'ASRockPolychromeUSBControllerDetect.cpp'] = source
    server = updates['NetworkServer.cpp']
    server = once(server, '#include "NetworkServer.h"', '#include "NetworkServer.h"\n#include "FlowDeckAsrockSdkPeer.inc"')
    for name, command, parameters in [('UpdateLEDs', 1050, 'unsigned int controller_id'), ('UpdateZoneLEDs', 1051, 'unsigned int controller_id'),
                                      ('UpdateSingleLED', 1052, 'unsigned int controller_id'), ('UpdateSaveMode', 1101, 'unsigned int controller_id, bool save_mode')]:
        signature = 'NetPacketStatus NetworkServer::ProcessRequest_RGBController_' + name + '(NetworkClientInfo* client_info, unsigned int data_size, unsigned char* data_ptr, ' + parameters + ')'
        old = operation.function(server, signature)
        anchor = '''    if(!controller_idx_valid)
    {
        return(NET_PACKET_STATUS_ERROR_INVALID_ID);
    }'''
        save_check = 'save_mode || ' if name == 'UpdateSaveMode' else ''
        new = once(old, anchor, anchor + '''

    FlowDeckAsrockOrigin flowdeck_origin;
    if(controllers[controller_idx]->FlowDeckManaged() && (''' + save_check + '''!FlowDeckAsrockSdkPeer(
        client_info->client_sock, client_info->client_protocol_version, ''' + str(command) + ''', controller_id, flowdeck_origin)))
        return(NET_PACKET_STATUS_ERROR_NOT_ALLOWED);
    const FlowDeckAsrockOriginScope flowdeck_origin_scope(flowdeck_origin);''')
        server = once(server, old, new)
    updates['NetworkServer.cpp'] = server
    for name, target in [('FlowDeckAsrockAuthorization.h', DIR + 'FlowDeckAsrockAuthorization.h'), ('FlowDeckAsrockSdkPeer.inc', 'FlowDeckAsrockSdkPeer.inc')]:
        updates[target] = (ROOT / 'native/OpenRgb' / name).read_text(encoding='utf-8')
    diff = (destination / 'flowdeck.patch').read_text(encoding='utf-8')
    for name, content in updates.items():
        (destination / name).write_text(content, encoding='utf-8', newline='\n')
        diff += ''.join(difflib.unified_diff(originals.get(name, '').splitlines(True), content.splitlines(True), fromfile='a/' + name, tofile='b/' + name))
    (destination / 'flowdeck.patch').write_text(diff, encoding='utf-8', newline='\n')
    manifest = destination / 'flowdeck-source.json'; receipt = json.loads(manifest.read_text(encoding='utf-8'))
    entries = {e['path']: e for e in receipt['files']}
    for name in updates:
        data = (destination / name).read_bytes(); entries[name] = {'path': name, 'length': len(data), 'sha256': prepare.sha256(data)}
    receipt.update(flowDeckPatch=5, files=[entries[n] for n in sorted(entries)], patchSha256=prepare.sha256(diff.encode('utf-8')))
    manifest.write_text(json.dumps(receipt, indent=2)+'\n', encoding='utf-8'); prepare.verify(destination)
    print(json.dumps({'flowDeckPatch':5, 'files':len(entries), 'compiled':False, 'parentChannelInstalled':False}))


if __name__ == '__main__':
    parser=argparse.ArgumentParser(description=__doc__); parser.add_argument('archive'); parser.add_argument('new_directory')
    args=parser.parse_args(); patch(args.archive,args.new_directory)
