"""Real JSON framing and discovery logic; a fake driver only, no USB calls."""
import importlib.util
from pathlib import Path
from liquidctl.util import Hue2Accessory

spec = importlib.util.spec_from_file_location("test_worker", Path(__file__).with_name("test_worker.py"))
fixture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixture)
FakeDevice, module = fixture.FakeDevice, fixture.module


class AccessoryHub(FakeDevice):
    def initialize(self):
        return [(f"LED {port} accessory {index}", Hue2Accessory(code), "")
                for port, codes in {1: [0x3f], 2: [0x38], 3: [0x38, 0x3f], 4: [0x39]}.items()
                for index, code in enumerate(codes, 1)]

    def set_color(self, *args, **kwargs):
        raise AssertionError("Discovery tests must not write lighting")

    def set_fixed_speed(self, *args, **kwargs):
        raise AssertionError("Discovery tests must not write fans")


worker_type = module.Worker
module.Worker = lambda: worker_type(lambda: [AccessoryHub()])
module.main()
