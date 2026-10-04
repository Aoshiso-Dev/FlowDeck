"""Add synchronous ASRock SDK execution and read-only operation receipts to source patch 2."""
import argparse
import difflib
import importlib.util
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DIR = 'Controllers/ASRockPolychromeUSBController/'


def module(name, file):
    spec = importlib.util.spec_from_file_location(name, ROOT / 'scripts' / file)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


def once(text, before, after):
    if text.count(before) != 1:
        raise ValueError('Unexpected source anchor: ' + before[:100])
    return text.replace(before, after)


def function(text, signature):
    start = text.index(signature + '\n{')
    end = text.index('\n}', start) + 2
    return text[start:end]


def patch(archive, destination):
    module('io_source', 'prepare-openrgb-io-source.py').patch(archive, destination)
    prepare = module('prepare', 'prepare-openrgb-source.py')
    destination = Path(destination)
    names = [DIR + 'RGBController_ASRockPolychromeUSB.h', DIR + 'RGBController_ASRockPolychromeUSB.cpp',
             'RGBController/RGBController.h', 'NetworkServer.cpp', 'NetworkServer.h', 'NetworkProtocol.h']
    originals = {n: (destination / n).read_text(encoding='utf-8-sig') for n in names}
    updates = dict(originals)
    base = updates['RGBController/RGBController.h']
    for name in ('UpdateLEDs', 'UpdateMode'):
        base = once(base, '    void                    ' + name + '();', '    virtual void            ' + name + '();')
    base = once(base, '    void                    Shutdown();', '''    void                    Shutdown();
    virtual bool            FlowDeckManaged() const { return false; }
    virtual bool            FlowDeckAcceptsCommand(unsigned int, unsigned int) const { return true; }
    virtual bool            FlowDeckLastOperationSucceeded() const { return true; }
    virtual std::string     FlowDeckOperationState() const { return ""; }''')
    updates['RGBController/RGBController.h'] = base
    header = updates[DIR + 'RGBController_ASRockPolychromeUSB.h']
    header = once(header, '#include "ASRockPolychromeUSBController.h"', '#include "ASRockPolychromeUSBController.h"\n#include "FlowDeckAsrockOperation.h"')
    header = once(header, '    void        SetupZones();', '''    void        SetupZones();
    void        UpdateLEDs() override;
    void        UpdateMode() override;
    bool        FlowDeckManaged() const override { return true; }
    bool        FlowDeckAcceptsCommand(unsigned int command, unsigned int protocol) const override
    {
        return protocol == 6 && flowdeck_operation.Snapshot().stage != FlowDeckOperationStage::Failed &&
            (command == 1050 || command == 1051 || command == 1052 || command == 1101);
    }
    bool        FlowDeckLastOperationSucceeded() const override { return flowdeck_operation.Succeeded(); }
    std::string FlowDeckOperationState() const override { return flowdeck_operation.Json().dump(); }''')
    header = once(header, 'private:\n', 'private:\n    FlowDeckAsrockOperation flowdeck_operation;\n')
    updates[DIR + 'RGBController_ASRockPolychromeUSB.h'] = header
    source = updates[DIR + 'RGBController_ASRockPolychromeUSB.cpp']
    definitions = [
        ('DeviceUpdateLEDs()', 1050, 'if(zones_info.empty()) throw std::runtime_error("No ASRock zones");'),
        ('DeviceUpdateZoneLEDs(int zone)', 1051, 'if(zone < 0 || static_cast<std::size_t>(zone) >= zones.size()) throw std::out_of_range("ASRock zone");'),
        ('DeviceUpdateSingleLED(int led)', 1052, 'if(led < 0 || static_cast<std::size_t>(led) >= leds.size()) throw std::out_of_range("ASRock LED");'),
        ('DeviceUpdateMode()', 1101, 'if(active_mode < 0 || static_cast<std::size_t>(active_mode) >= modes.size()) throw std::out_of_range("ASRock mode");')]
    for name, command, validation in definitions:
        old = function(source, 'void RGBController_PolychromeUSB::' + name)
        body = old[old.index('{') + 1:-1]
        replacement = 'void RGBController_PolychromeUSB::' + name + '''
{
    flowdeck_operation.Run(''' + str(command) + ''', [this] { return controller->FlowDeckIoState(); }, [&]
    {
        ''' + validation + '\n' + body + '''
    });
}'''
        source = once(source, old, replacement)
    old = function(source, 'void RGBController_PolychromeUSB::DeviceConfigureZone(int zone_idx)')
    source = once(source, old, '''void RGBController_PolychromeUSB::DeviceConfigureZone(int)
{
    // Resizing is rejected before changing the native cached table or sending HID.
    flowdeck_operation.Run(1000, [this] { return controller->FlowDeckIoState(); }, []
    { throw std::runtime_error("ASRock resizing is not supported by the owned helper"); });
}''')
    for name, reason in [('LEDs', 'UPDATELEDS'), ('Mode', 'UPDATEMODE')]:
        source += '''
// The SDK must not acknowledge this request while a coalescing device thread still owns it.
void RGBController_PolychromeUSB::Update''' + name + '''()
{
    {
        std::shared_lock<std::shared_mutex> lock(AccessMutex);
        DeviceUpdate''' + name + '''();
    }
    SignalUpdate(RGBCONTROLLER_UPDATE_REASON_''' + reason + ''');
}
'''
    updates[DIR + 'RGBController_ASRockPolychromeUSB.cpp'] = source
    server = updates['NetworkServer.cpp']
    for name, parameters in [
        ('UpdateLEDs', 'unsigned int controller_id'), ('UpdateZoneLEDs', 'unsigned int controller_id'),
        ('UpdateSingleLED', 'unsigned int controller_id'), ('UpdateSaveMode', 'unsigned int controller_id, bool save_mode')]:
        signature = 'NetPacketStatus NetworkServer::ProcessRequest_RGBController_' + name + '(NetworkClientInfo* client_info, unsigned int data_size, unsigned char* data_ptr, ' + parameters + ')'
        old = function(server, signature)
        new = once(old, '    return(NET_PACKET_STATUS_OK);', '    return(controllers[controller_idx]->FlowDeckLastOperationSucceeded() ? NET_PACKET_STATUS_OK : NET_PACKET_STATUS_ERROR_GENERIC);')
        server = once(server, old, new)
    # Single-LED requests also use the controller's queue; they cannot change cached colors
    # between another request's cache update and its synchronous hardware call.
    immediate = '''            case NET_PACKET_ID_RGBCONTROLLER_UPDATESINGLELED:
                status = ProcessRequest_RGBController_UpdateSingleLED(client_info, header.pkt_size, data, header.pkt_dev_id);
                break;
'''
    server = once(server, immediate, '')
    queue_case = '\n            case NET_PACKET_ID_RGBCONTROLLER_UPDATELEDS:'
    server = once(server, queue_case, '\n            case NET_PACKET_ID_RGBCONTROLLER_UPDATESINGLELED:' + queue_case)
    thread_case = '                    case NET_PACKET_ID_RGBCONTROLLER_UPDATELEDS:'
    server = once(server, thread_case, '''                    case NET_PACKET_ID_RGBCONTROLLER_UPDATESINGLELED:
                        status = ProcessRequest_RGBController_UpdateSingleLED(queue_entry.client_info, queue_entry.header.pkt_size, queue_entry.data, this_thread->id);
                        break;

''' + thread_case)
    server = once(server, '        switch(header.pkt_id)\n', '''        bool flowdeck_unsupported = false;
        if(header.pkt_id >= 1000 && header.pkt_id < 2000)
        {
            std::shared_lock<std::shared_mutex> lock(controller_ids_mutex);
            bool valid;
            const auto index = index_from_id(header.pkt_dev_id, client_info->client_protocol_version, &valid);
            flowdeck_unsupported = valid && !controllers[index]->FlowDeckAcceptsCommand(header.pkt_id, client_info->client_protocol_version);
        }
        if(flowdeck_unsupported) status = NET_PACKET_STATUS_ERROR_UNSUPPORTED;
        else switch(header.pkt_id)
''')
    server = once(server, '            case NET_PACKET_ID_REQUEST_CONTROLLER_COUNT:', '''            case NET_PACKET_ID_FLOWDECK_OPERATION:
                status = ProcessRequest_FlowDeckOperation(client_info, header.pkt_dev_id, header.pkt_size);
                break;

            case NET_PACKET_ID_REQUEST_CONTROLLER_COUNT:''')
    server += '\n#include "FlowDeckAsrockSdk.inc"\n'
    updates['NetworkServer.cpp'] = server
    header = updates['NetworkServer.h']
    anchor = '    void                                SendAck(NetworkClientInfo* client_info, unsigned int acked_pkt_dev_id, unsigned int acked_pkt_id, NetPacketStatus status);'
    updates['NetworkServer.h'] = once(header, anchor, '    NetPacketStatus                     ProcessRequest_FlowDeckOperation(NetworkClientInfo* client_info, unsigned int controller_id, unsigned int data_size);\n' + anchor)
    protocol = updates['NetworkProtocol.h']
    anchor = '    NET_PACKET_ID_REQUEST_CONTROLLER_DATA       = 1,'
    updates['NetworkProtocol.h'] = once(protocol, anchor, '    NET_PACKET_ID_FLOWDECK_OPERATION            = 0x46440001, /* Owned-helper read-only operation receipt */\n' + anchor)
    updates[DIR + 'FlowDeckAsrockOperation.h'] = (ROOT / 'native/OpenRgb/FlowDeckAsrockOperation.h').read_text(encoding='utf-8')
    updates['FlowDeckAsrockSdk.inc'] = (ROOT / 'native/OpenRgb/FlowDeckAsrockSdk.inc').read_text(encoding='utf-8')
    diff = (destination / 'flowdeck.patch').read_text(encoding='utf-8')
    for name, text in updates.items():
        (destination / name).write_text(text, encoding='utf-8', newline='\n')
        diff += ''.join(difflib.unified_diff(originals.get(name, '').splitlines(True), text.splitlines(True), fromfile='a/' + name, tofile='b/' + name))
    (destination / 'flowdeck.patch').write_text(diff, encoding='utf-8', newline='\n')
    manifest_path = destination / 'flowdeck-source.json'
    receipt = json.loads(manifest_path.read_text(encoding='utf-8'))
    entries = {e['path']: e for e in receipt['files']}
    for name in updates:
        data = (destination / name).read_bytes()
        entries[name] = {'path': name, 'length': len(data), 'sha256': prepare.sha256(data)}
    receipt.update(flowDeckPatch=3, files=[entries[n] for n in sorted(entries)], patchSha256=prepare.sha256(diff.encode('utf-8')))
    manifest_path.write_text(json.dumps(receipt, indent=2) + '\n', encoding='utf-8')
    prepare.verify(destination)
    print(json.dumps({'flowDeckPatch': 3, 'files': len(entries), 'compiled': False}))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('archive'); parser.add_argument('new_directory')
    args = parser.parse_args()
    patch(args.archive, args.new_directory)
