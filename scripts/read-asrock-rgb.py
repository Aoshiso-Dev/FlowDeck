"""Read ASRock 26ce:01a2 inventory. Only 0x14/0x11 query opcodes; no initialization or setters."""
import hid
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

devices = hid.enumerate(0x26CE, 0x01A2)
if len(devices) != 1:
    raise RuntimeError(f"Expected one ASRock RGB device, found {len(devices)}")
device = hid.device()
device.open_path(devices[0]["path"])
queries = []

def query(opcode, selector):
    if opcode not in (0x14, 0x11):
        raise ValueError("Only read queries are allowed")
    packet = [0] * 65
    packet[1], packet[3] = opcode, selector
    device.write(packet)
    response = device.read(64, 1000)
    queries.append({"opcode": opcode, "selector": selector, "response": response})
    if len(response) < 12:
        raise RuntimeError("ASRock RGB query response was incomplete")
    return response

try:
    counts = query(0x14, 2)
    available = query(0x14, 1)
    swap = query(0x14, 3)
    for zone in range(8):
        if (available[4] >> zone) & 1 and counts[4 + zone] != 0x1E:
            query(0x11, zone)
finally:
    device.close()

report = {"utc": datetime.now(timezone.utc).isoformat(), "vid": "26ce", "pid": "01a2",
          "purpose": "configuration queries only", "queries": queries}
Path(sys.argv[1]).write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps(report))
