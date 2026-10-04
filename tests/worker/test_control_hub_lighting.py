"""Exercise the pinned liquidctl packet encoder without opening USB hardware."""
import importlib.util
import json
import pathlib
import unittest
from unittest.mock import patch

from liquidctl.driver.control_hub import ControlHub

ROOT = pathlib.Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("hub_worker", ROOT / "worker" / "hub_worker.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class RecordingHid:
    path = b"fake-hid-path"

    def __init__(self):
        self.writes = []

    def write(self, data):
        self.writes.append(bytes(data))


class ControlHubLightingTests(unittest.TestCase):
    def test_embedded_definition_matches_worker_discovery_and_real_encoder(self):
        # Read the build input only in tests. Production Worker never adopts external JSON definitions.
        definition = json.loads((ROOT / "device-definitions" / "controllers.json").read_text(encoding="utf-8"))["devices"][0]
        transport, lighting, cooling = definition["transport"], definition["lighting"], definition["cooling"]
        self.assertEqual(module.REQUIRED_LIQUIDCTL, transport["liquidctlVersion"])
        device, handle = self.discovered_device()
        with patch("liquidctl.find_liquidctl_devices", return_value=[device]) as finder:
            self.assertEqual(module.discover(), [device])
            finder.assert_called_once_with(vendor=transport["vendorId"], product=transport["productId"])
        self.assertEqual([c for c in device._speed_channels if c != "sync"], cooling["endpoints"])
        self.assertEqual([c for c in device._color_channels if c != "sync"], lighting["endpoints"])
        self.assertEqual(self.guarded(device, lighting["endpoints"])["outcome"], "writtenUnverified")
        self.assertEqual([row[2] for row in handle.writes[::2]], lighting["channelMasks"])
        self.assertEqual(lighting["frameColors"], "grb")
        self.assertTrue(all(row[4:] == bytes([34, 17, 51]) * lighting["encodedPixelsPerEndpoint"] for row in handle.writes[::2]))
        for mode in lighting["nativeEffects"]:
            colors = [] if mode in ("off", "spectrum-wave", "super-rainbow") else [[17, 34, 51]]
            with self.subTest(mode=mode):
                result, recorded = self.effect(mode, colors)
                self.assertEqual(result["outcome"], "writtenUnverified")
                if colors:
                    self.assertEqual(lighting["nativeColors"], "grb")
                    self.assertIn(bytes([34, 17, 51]), recorded.writes[0])

    def discovered_device(self):
        handle = RecordingHid()
        device = ControlHub(handle, "NZXT Control Hub", 5, 5)
        with patch("liquidctl.find_liquidctl_devices", return_value=[object(), device]) as finder:
            self.assertEqual(module.discover(), [device])
            finder.assert_called_once_with(vendor=0x1E71, product=0x2022)
        self.assertEqual(handle.writes, [])
        return device, handle

    def test_presets_address_each_port_once_without_fan_writes(self):
        # Upstream 6227bbf: physical lighting ports 1/2 use masks 2/1.
        # Regressions were led2 -> 4 and led3 -> 6 (two ports simultaneously).
        for preset in (*module.COLORS, "off"):
            with self.subTest(preset=preset):
                device, handle = self.discovered_device()
                worker = module.Worker(lambda: [device])
                worker.device = device
                worker.leds = [{"id": f"led{i}"} for i in range(1, 6)]
                result = worker.handle({"v": 1, "type": "request", "id": preset,
                    "workerSessionId": worker.session, "deviceGeneration": 0,
                    "command": "SetLighting", "payload": {
                        "channels": [row["id"] for row in worker.leds], "preset": preset}})
                self.assertEqual(result["outcome"], "writtenUnverified")
                self.assertEqual(len(handle.writes), 10)
                rgb = module.COLORS.get(preset, [0, 0, 0])
                expected_pixels = bytes([rgb[1], rgb[0], rgb[2]]) * 24
                for i, mask in enumerate((2, 1, 4, 8, 16)):
                    pixels, commit = handle.writes[i*2:i*2+2]
                    self.assertEqual(pixels[:4], bytes([0x26, 0x04, mask, 0]))
                    self.assertEqual(pixels[4:], expected_pixels)
                    self.assertEqual(commit[:3], bytes([0x26, 0x06, mask]))
                self.assertFalse(any(row[0] == 0x62 for row in handle.writes))

    def effect(self, mode, colors, channels=None, speed=2, reverse=False):
        device, handle = self.discovered_device()
        worker = module.Worker(lambda: [device]); worker.device = device
        worker.leds = [{"id": "led1"}, {"id": "led2"}, {"id": "led3"}]
        result = worker.handle({"v": 1, "type": "request", "id": "effect", "workerSessionId": worker.session,
            "deviceGeneration": 0, "command": "SetLightingEffect", "payload": {
                "channels": channels or ["led2"], "mode": mode, "colors": colors, "speed": speed, "reverse": reverse}})
        return result, handle

    def test_native_effect_packets_address_only_selected_port(self):
        for mode, colors, opcode in [("fixed", [[52,137,255]], 0x26), ("off", [], 0x26),
                ("fading", [[10,20,30],[0,0,0]], 0x2a), ("breathing", [[52,137,255]], 0x2a),
                ("spectrum-wave", [], 0x2a), ("covering-marquee", [[10,20,30]], 0x2a), ("super-rainbow", [], 0x2a)]:
            with self.subTest(mode=mode):
                result, handle = self.effect(mode, colors)
                self.assertEqual(result["outcome"], "writtenUnverified")
                self.assertTrue(all(p[0] == opcode and p[2] == 1 for p in handle.writes))
                self.assertFalse(any(p[0] == 0x62 for p in handle.writes))

    def test_breathing_is_color_to_black_fading(self):
        _, handle = self.effect("breathing", [[52,137,255]])
        packet = handle.writes[0]
        self.assertEqual(packet[4], 1)
        self.assertIn(bytes([137,52,255,0,0,0]), packet)

    def test_unconnected_duplicate_or_broadcast_channel_rejected(self):
        for channels in [["led4"], ["sync"], ["led2", "led2"]]:
            with self.assertRaises(ValueError): self.effect("fixed", [[0,0,0]], channels=channels)

    def test_malformed_effects_are_rejected_before_any_write(self):
        for mode, colors, speed in [("rainbow", [], 2), ("fixed", [], 2), ("fixed", [[256,0,0]], 2),
                ("fixed", [[True,0,0]], 2), ("fading", [[0,0,0]]*9, 2), ("fixed", [[0,0,0]], -1)]:
            with self.assertRaises(ValueError): self.effect(mode, colors, speed=speed)

    def test_sync_color_frame_is_only_color_and_commit_packets(self):
        result, handle = self.effect("fixed", [[30,60,90]], channels=["led1","led2","led3"])
        self.assertEqual(result["outcome"], "writtenUnverified")
        self.assertEqual([p[2] for p in handle.writes], [2,2,1,1,4,4])
        self.assertTrue(all(p[0] == 0x26 for p in handle.writes))

    def guarded(self, device, channels):
        worker = module.Worker(lambda: [device]); worker.device = device
        worker.leds = [{"id": f"led{i}", "accessory": "RGB", "ledCount": None} for i in range(1, 6)]
        status = {"channels": [], "lighting": worker.leds, "firmware": None}
        with patch.object(worker, "status", return_value=status):
            return worker.handle({"v": 1, "type": "request", "id": "guarded", "workerSessionId": worker.session,
                "deviceGeneration": 0, "command": "SetLightingEndpoints", "payload": {
                    "channels": channels, "mode": "fixed", "colors": [[17, 34, 51]], "speed": 2, "reverse": False,
                    "expectedHubId": module.identity(device)["id"], "expectedWiringSignature": module.wiring_signature(status)}})

    def test_guarded_frames_preserve_port_masks_and_grb_encoding(self):
        device, handle = self.discovered_device()
        result = self.guarded(device, [f"led{i}" for i in range(1, 6)])
        self.assertEqual(result["outcome"], "writtenUnverified")
        self.assertEqual([p[2] for p in handle.writes], [2, 2, 1, 1, 4, 4, 8, 8, 16, 16])
        for pixels in handle.writes[::2]:
            self.assertEqual(pixels[:2], b"\x26\x04")
            self.assertEqual(pixels[4:], bytes([34, 17, 51]) * 24)
        self.assertFalse(any(p[0] == 0x62 for p in handle.writes))

    def test_deadline_frame_shares_one_status_and_preserves_selected_port_packets(self):
        device, handle = self.discovered_device()
        worker = module.Worker(lambda: [device]); worker.device = device
        worker.leds = [{"id": f"led{i}", "accessory": "RGB", "ledCount": 24} for i in range(1, 6)]
        status = {"channels": [], "lighting": worker.leds, "firmware": None}
        with patch.object(module, "tick_count64", return_value=1000), \
             patch.object(worker, "status", return_value=status) as read:
            result = worker.handle({"v": 1, "type": "request", "id": "deadline-frame", "workerSessionId": worker.session,
                "deviceGeneration": 0, "command": "SetLightingEndpoints", "payload": {
                    "channels": ["led1", "led2", "led3"], "mode": "fixed", "colors": [[0, 255, 255]], "speed": 2, "reverse": False,
                    "expectedHubId": module.identity(device)["id"], "expectedWiringSignature": module.wiring_signature(status),
                    "validUntilTick64": 1500}})
        self.assertEqual(result["outcome"], "writtenUnverified")
        read.assert_called_once()
        self.assertEqual([p[2] for p in handle.writes], [2, 2, 1, 1, 4, 4])
        self.assertTrue(all(p[0] == 0x26 for p in handle.writes))
        for pixels in handle.writes[::2]:
            self.assertEqual(pixels[4:], bytes([255, 0, 255]) * 24)

    def test_failed_commit_after_color_data_is_unknown_and_does_not_write_next_port(self):
        device, handle = self.discovered_device()
        write = handle.write
        def incomplete(data):
            if handle.writes: raise ValueError("commit rejected after pixel data")
            write(data)
        with patch.object(handle, "write", side_effect=incomplete):
            result = self.guarded(device, ["led1", "led2"])
        self.assertEqual(result["outcome"], "unknown")
        self.assertEqual(len(handle.writes), 1)
        self.assertEqual(handle.writes[0][:3], bytes([0x26, 0x04, 2]))
        self.assertEqual([r["write"] for r in result["channels"]], ["unknown", "notExecuted"])

    def test_compatibility_is_instance_local_and_preserves_fan_mapping(self):
        original = dict(ControlHub._CHANNEL_BYTE_MAP)
        device, handle = self.discovered_device()
        self.assertEqual(ControlHub._CHANNEL_BYTE_MAP, original)
        for index in range(5):
            device.set_fixed_speed(f"fan{index+1}", 50)
        self.assertEqual([row[2] for row in handle.writes], [1, 2, 4, 8, 16])
        self.assertTrue(all(row[:2] == b"\x62\x01" for row in handle.writes))

    def test_individual_fan_packet_contains_only_selected_mask_and_duty(self):
        for index in range(5):
            with self.subTest(channel=index + 1):
                device, handle = self.discovered_device()
                device.set_fixed_speed(f"fan{index + 1}", 67)
                expected = bytearray([0x62, 0x01, 1 << index, 0, 0, 0, 0, 0])
                expected[3 + index] = 67
                expected.extend(bytes(64 - len(expected)))
                self.assertEqual(handle.writes, [bytes(expected)])


if __name__ == "__main__":
    unittest.main(verbosity=2)
