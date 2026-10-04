"""Read a boot advertisement without reset, configuration changes, or control commands."""
import json
import sys
import time
from pathlib import Path
import serial

with serial.Serial(baudrate=115200, timeout=0.4) as port:
    port.dtr = False
    port.rts = False
    port.port = sys.argv[1]
    port.open()
    deadline = time.monotonic() + 10
    report = None
    while time.monotonic() < deadline:
        try:
            frame = json.loads(port.readline(4096))
        except (ValueError, UnicodeError):
            continue
        if frame.get("type") == "hello":
            report = {key: frame.get(key) for key in ("deviceId", "firmwareVersion", "capabilities", "display")}
            break
    if report is None:
        raise RuntimeError("M5GO hello not received")
    Path(sys.argv[2]).write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report))
