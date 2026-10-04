"""Collect only non-secret Wi-Fi status. No control writes or board reset."""
import json
import sys
import time
from pathlib import Path
import serial

records = []
with serial.Serial(baudrate=115200, timeout=0.3) as port:
    port.dtr = False
    port.rts = False
    port.port = sys.argv[1]
    port.open()
    deadline = time.monotonic() + (min(60, max(1, int(sys.argv[3]))) if len(sys.argv) > 3 else 30)
    while time.monotonic() < deadline:
        try:
            frame = json.loads(port.readline(4096))
        except (ValueError, UnicodeError):
            continue
        if isinstance(frame.get('link'), dict):
            link = frame['link']
            safe = {k: link.get(k) for k in ('transport', 'status', 'ip', 'rssi', 'tlsError', 'freeHeap', 'maxAllocHeap', 'caBytes', 'caParseError', 'caSha256')}
            if not records or safe['status'] != records[-1]['status'] or safe['tlsError'] != records[-1]['tlsError']:
                records.append(safe)
                print(json.dumps(safe, ensure_ascii=False), flush=True)
Path(sys.argv[2]).write_text(json.dumps(records, ensure_ascii=False, indent=2), encoding='utf-8')
