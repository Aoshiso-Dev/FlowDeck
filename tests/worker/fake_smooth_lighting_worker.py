"""Real Worker, pinned packet encoder, fake HID with 500 ms status reports. No USB discovery."""
import importlib.util
import pathlib
import time
from liquidctl.driver.control_hub import ControlHub
from liquidctl.error import Timeout

root = pathlib.Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("hub_worker", root / "worker/hub_worker.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class Hid:
    path = b"fake-smooth-hid"
    serial_number = "fake-smooth-hid"

    def __init__(self):
        self.next_report = time.monotonic() + .5

    def clear_enqueued_reports(self):
        self.next_report = time.monotonic() + .5

    def read(self, length, *, timeout=2000):
        delay = max(0, self.next_report - time.monotonic())
        if timeout == 1 and delay > .001:
            raise Timeout()
        time.sleep(delay)
        self.next_report = time.monotonic() + .5
        msg = bytearray(64)
        msg[:2] = b"\x67\x02"
        for i in range(5):
            msg[16+i] = 2
            msg[24+2*i:26+2*i] = (1300).to_bytes(2, "little")
            msg[40+i] = 50
        return msg

    def write(self, data):
        # Never accept cooling, discovery/configuration, broadcast or other ports.
        assert bytes(data[:2]) in (b"\x26\x04", b"\x26\x06")
        assert data[2] in (2, 1, 4)


class Hub(ControlHub):
    _flowdeck_latest_status = True
    _CHANNEL_BYTE_MAP = {0: 2, 1: 1, 2: 4, 3: 8, 4: 16}

    def __init__(self):
        super().__init__(Hid(), "Memory-only smooth lighting Hub", 5, 5)

    def connect(self): pass
    def disconnect(self): pass
    def initialize(self, **kwargs):
        return [(f"LED {i} accessory 1", "RGB", "") for i in (1, 2, 3)]


worker_type = module.Worker
module.Worker = lambda: worker_type(lambda: [Hub()])
module.main()
