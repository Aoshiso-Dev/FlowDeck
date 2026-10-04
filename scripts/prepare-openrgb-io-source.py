"""Add checked ASRock exchanges and explicit HID ownership to the pinned helper source."""
import argparse
import difflib
import importlib.util
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DIRECTORY = "Controllers/ASRockPolychromeUSBController/"


def once(text, before, after):
    if text.count(before) != 1:
        raise ValueError("Unexpected pinned source anchor: " + before[:100])
    return text.replace(before, after)


def patch(archive, destination):
    spec = importlib.util.spec_from_file_location("prepare", ROOT / "scripts/prepare-openrgb-source.py")
    prepare = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(prepare)
    prepare.prepare(archive, destination)
    destination = Path(destination)
    originals, updates = {}, {}
    for name in ("ASRockPolychromeUSBController.h", "ASRockPolychromeUSBController.cpp", "ASRockPolychromeUSBControllerDetect.cpp"):
        originals[DIRECTORY + name] = (destination / (DIRECTORY + name)).read_text(encoding="utf-8-sig")
    header = originals[DIRECTORY + "ASRockPolychromeUSBController.h"]
    header = once(header, '#include "RGBController.h"', '#include "RGBController.h"\n#include "FlowDeckAsrockIo.h"')
    header = once(header, '    ~PolychromeUSBController();', '    ~PolychromeUSBController();\n    FlowDeckAsrockIoSnapshot FlowDeckIoState() const { return flowdeck_io.Snapshot(); }')
    header = once(header, 'private:\n', 'private:\n    FlowDeckAsrockIo flowdeck_io;\n')
    updates[DIRECTORY + "ASRockPolychromeUSBController.h"] = header
    source = originals[DIRECTORY + "ASRockPolychromeUSBController.cpp"]
    constructor = 'PolychromeUSBController::PolychromeUSBController(hid_device* dev_handle, const char* path)\n'
    source = once(source, constructor, constructor + '''    : flowdeck_io([dev_handle](const unsigned char* data, std::size_t size) { return hid_write(dev_handle, data, size); },
         [dev_handle](unsigned char* data, std::size_t size, int timeout) { return hid_read_timeout(dev_handle, data, size, timeout); })
''')
    source = once(source, 'PolychromeUSBController::~PolychromeUSBController()\n{\n\n}',
                  'PolychromeUSBController::~PolychromeUSBController()\n{\n    hid_close(dev);\n}')
    # Replace only the eight exact write/read pairs, including the upstream 64-byte header query.
    # A checked exchange supplies the reply in the caller's buffer; intervening clears must go.
    pattern = re.compile(r'hid_write\(dev, usb_buf, (?:64|65)\);(.*?)hid_read\(dev, usb_buf, 64\);', re.S)
    matches = list(pattern.finditer(source))
    if len(matches) != 8:
        raise ValueError("Unexpected ASRock exchange count")
    for match in matches:
        between = re.sub(r'/\*.*?\*/', '', match[1], flags=re.S)
        between = between.replace('memset(usb_buf, 0x00, sizeof(usb_buf));', '')
        if between.strip():
            raise ValueError("Unexpected operation inside an exchange")
    source = pattern.sub('flowdeck_io.Exchange(usb_buf);', source)
    updates[DIRECTORY + "ASRockPolychromeUSBController.cpp"] = source
    detector = originals[DIRECTORY + "ASRockPolychromeUSBControllerDetect.cpp"]
    detector = once(detector, '#include <hidapi.h>', '#include <hidapi.h>\n#include <memory>')
    detector = once(detector, '    hid_device*         dev;\n', '')
    old = '''    dev = hid_open_path(info->path);

    if(dev)
    {
        PolychromeUSBController*     controller     = new PolychromeUSBController(dev, info->path);
        RGBController_PolychromeUSB* rgb_controller = new RGBController_PolychromeUSB(controller);

        detected_controllers.push_back(rgb_controller);
    }
'''
    new = '''    // The detector owns HID until the native controller is constructed; that controller
    // owns it thereafter. Constructor failures cannot leak a handle or release ownership early.
    std::unique_ptr<hid_device, decltype(&hid_close)> device(hid_open_path(info->path), hid_close);
    if(device)
    {
        auto controller = std::make_unique<PolychromeUSBController>(device.get(), info->path);
        device.release();
        auto rgb_controller = std::make_unique<RGBController_PolychromeUSB>(controller.get());
        controller.release();
        detected_controllers.push_back(rgb_controller.get());
        rgb_controller.release();
    }
'''
    detector = once(detector, old, new)
    updates[DIRECTORY + "ASRockPolychromeUSBControllerDetect.cpp"] = detector
    added = DIRECTORY + "FlowDeckAsrockIo.h"
    updates[added] = (ROOT / "native/OpenRgb/FlowDeckAsrockIo.h").read_text(encoding="utf-8")
    changes = []
    for name, content in updates.items():
        (destination / name).write_text(content, encoding="utf-8", newline="\n")
        changes.extend(difflib.unified_diff(originals.get(name, '').splitlines(True), content.splitlines(True),
                                          fromfile='a/' + name, tofile='b/' + name))
    diff = (destination / 'flowdeck.patch').read_text(encoding="utf-8") + ''.join(changes)
    (destination / 'flowdeck.patch').write_text(diff, encoding="utf-8", newline="\n")
    receipt_path = destination / "flowdeck-source.json"
    receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
    records = {entry['path']: entry for entry in receipt['files']}
    for name in updates:
        data = (destination / name).read_bytes()
        records[name] = {'path': name, 'length': len(data), 'sha256': prepare.sha256(data)}
    receipt.update(flowDeckPatch=2, files=[records[n] for n in sorted(records)], patchSha256=prepare.sha256(diff.encode("utf-8")))
    receipt_path.write_text(json.dumps(receipt, indent=2) + '\n', encoding="utf-8", newline="\n")
    prepare.verify(destination)
    print(json.dumps({'path': str(destination.resolve()), 'flowDeckPatch': 2, 'files': len(records), 'checkedExchanges': 8, 'compiled': False}))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('archive')
    parser.add_argument('new_directory')
    args = parser.parse_args()
    patch(args.archive, args.new_directory)
