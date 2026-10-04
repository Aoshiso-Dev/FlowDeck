"""Run the pinned local esptool with a larger Windows serial receive buffer.

Some Windows serial drivers drop bytes in large SLIP frames. For read_flash,
use smaller protocol blocks as well as a larger receive queue. The esptool
reader still verifies the full returned data digest. Driver defaults and
write_flash sector sizes are unchanged.
"""
from pathlib import Path
import runpy
import site
import struct
import sys

import serial

if sys.platform == "win32":
    original_open = serial.Serial.open

    def buffered_open(self):
        original_open(self)
        self.set_buffer_size(rx_size=65536, tx_size=65536)

    serial.Serial.open = buffered_open

tool = Path(__file__).resolve().parents[1] / ".tools/pio-core/packages/tool-esptoolpy/esptool.py"
sys.path.insert(0, str(tool.parent))
site.addsitedir(str(tool.parent / "_contrib"))
if "read_flash" in sys.argv:
    from esptool.loader import ESPLoader

    original_read = ESPLoader.read_flash

    def small_block_read(self, offset, length, progress_fn=None):
        previous = self.FLASH_SECTOR_SIZE
        try:
            self.FLASH_SECTOR_SIZE = 1024
            return original_read(self, offset, length, progress_fn)
        finally:
            self.FLASH_SECTOR_SIZE = previous

    ESPLoader.read_flash = small_block_read
    original_check = ESPLoader.check_command

    def single_packet_check(self, description, op=None, data=b"", *args, **kwargs):
        if op == self.ESP_READ_FLASH:
            offset, length, block, _ = struct.unpack("<IIII", data)
            data = struct.pack("<IIII", offset, length, block, 1)
        return original_check(self, description, op, data, *args, **kwargs)

    ESPLoader.check_command = single_packet_check

runpy.run_path(str(tool), run_name="__main__")
