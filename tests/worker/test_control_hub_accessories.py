"""Accessory discovery and stale-wiring protection with no USB access."""
import unittest
from unittest.mock import patch

from liquidctl.util import Hue2Accessory
from test_worker import FakeDevice, module


class ControlHubAccessoryTests(unittest.TestCase):
    def opened(self, accessories):
        device = FakeDevice()
        worker = module.Worker(lambda: [device])
        info = [(f"LED {port} accessory {index}", value, "")
                for port, items in accessories.items() for index, value in enumerate(items, 1)]
        with patch.object(device, "initialize", return_value=info):
            result = worker.handle({"v": 1, "type": "request", "id": "open",
                "workerSessionId": worker.session, "deviceGeneration": 1,
                "command": "OpenInitialize", "payload": {"identity": module.identity(device)}})
        self.assertEqual(device.calls, [("connect",)])
        return worker, device, result["status"]

    def test_current_three_port_inventory_has_names_and_24_leds_without_output(self):
        worker, _, status = self.opened({1: [Hue2Accessory(0x3f)], 2: [Hue2Accessory(0x3f)], 3: [Hue2Accessory(0x38)]})
        self.assertEqual(status["lighting"], [
            {"id": "led1", "accessory": "F420 RGB Reverse (2026) (0x3f)", "ledCount": 24},
            {"id": "led2", "accessory": "F420 RGB Reverse (2026) (0x3f)", "ledCount": 24},
            {"id": "led3", "accessory": "F420 RGB (2026) (0x38)", "ledCount": 24}])
        self.assertEqual(worker.status(), status)

    def test_unknown_and_name_only_accessories_do_not_gain_a_count(self):
        _, _, status = self.opened({1: [Hue2Accessory(0x39)], 2: ["F420 RGB (2026)"]})
        self.assertEqual(status["lighting"], [
            {"id": "led1", "accessory": "Unknown (0x39)", "ledCount": None},
            {"id": "led2", "accessory": "F420 RGB (2026)", "ledCount": None}])

    def test_mixed_known_and_unknown_chain_has_no_fabricated_total(self):
        _, _, status = self.opened({1: [Hue2Accessory(0x38), Hue2Accessory(0x39)]})
        self.assertIsNone(status["lighting"][0]["ledCount"])
        self.assertIn("0x39", status["lighting"][0]["accessory"])

    def test_known_chain_reports_full_count_instead_of_clamping_to_encoder_limit(self):
        _, _, status = self.opened({1: [Hue2Accessory(0x38), Hue2Accessory(0x3f)]})
        self.assertEqual(status["lighting"][0]["ledCount"], 48)

    def test_model_change_or_old_unknown_binding_is_rejected_before_color_output(self):
        worker, device, status = self.opened({1: [Hue2Accessory(0x3f)]})
        for old in ["Unknown (0x3f)", "F420 RGB (2026) (0x38)"]:
            with self.subTest(old=old):
                prior = {**status, "lighting": [{**status["lighting"][0], "accessory": old}]}
                result = worker.guarded_lighting(["led1"], "fixed", [[1, 2, 3]], "normal", "forward",
                    {"expectedHubId": module.identity(device)["id"], "expectedWiringSignature": module.wiring_signature(prior)})
                self.assertEqual(result["outcome"], "rejected")
                self.assertEqual(result["error"], "WIRING_CHANGED")
                self.assertEqual(device.calls, [("connect",)])


if __name__ == "__main__":
    unittest.main(verbosity=2)
