"""Read a firmware greeting without sending a control command or resetting the board."""
import json
import sys
import time
from pathlib import Path
import serial

with serial.Serial(baudrate=115200, timeout=0.25) as port:
    port.dtr = False
    port.rts = False
    port.port = sys.argv[1]
    port.open()
    deadline = time.monotonic() + 8
    while time.monotonic() < deadline:
        line = port.readline(4096)
        try:
            frame = json.loads(line)
        except (ValueError, UnicodeError):
            continue
        if frame.get("type") == "hello" and frame.get("deviceType") == "M5GO":
            text = json.dumps(frame, ensure_ascii=False, indent=2)
            Path(sys.argv[2]).write_text(text, encoding="utf-8")
            print(text)
            break
    else:
        raise SystemExit("No M5GO hello received")
