"""Real worker framing, guards and OS clock with a memory-only Hub. No liquidctl import."""
import importlib.util
import pathlib
import time

root = pathlib.Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("hub_worker", root / "worker" / "hub_worker.py")
worker_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker_module)


class MemoryHub:
    description = "Memory-only lighting Hub"
    serial_number = "fake-lighting"
    device = type("Handle", (), {"path": b"fake-lighting"})()

    def connect(self): pass
    def disconnect(self): pass
    def initialize(self):
        return [(f"LED {i} accessory 1", "RGB", "") for i in (1, 2, 3)]
    def get_status(self):
        time.sleep(globals().get("STATUS_REPORT_DELAY_SECONDS", 0))
        return [row for i in range(1, 6) for row in (
            (f"Fan {i} speed", 1000, "rpm"), (f"Fan {i} duty", 50, "%"), (f"Fan {i} control mode", "PWM", ""))]
    def set_color(self, channel, mode, colors, **kwargs):
        if colors == [[254, 0, 0]] and channel == "led2":
            raise OSError("Simulated incomplete HID commit")
    def set_fixed_speed(self, *args):
        raise AssertionError("Lighting tests must never write fan output")


worker_type = worker_module.Worker
worker_module.Worker = lambda: worker_type(lambda: [MemoryHub()])
worker_module.importlib.metadata.version = lambda package: worker_module.REQUIRED_LIQUIDCTL
if globals().get("WITHOUT_LIGHTING_DEADLINE", False):
    original_capabilities = worker_module.worker_capabilities
    worker_module.worker_capabilities = lambda: [c for c in original_capabilities() if c != "LightingDeadlineTick64"]
worker_module.main()
