import importlib.util
import io
import pathlib
import unittest
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("hub_worker", ROOT / "worker" / "hub_worker.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class FakeDevice:
    description = "NZXT Control Hub"
    serial_number = "fake-serial"
    def __init__(self):
        self.device = type("Handle", (), {"path": b"fake-hid-path"})()
        self.duties = [45] * 5
        self.calls = []
        self.fail = None
        self.lost = False
    def connect(self): self.calls.append(("connect",))
    def disconnect(self): self.calls.append(("disconnect",))
    def initialize(self): return [("Firmware version", "1.2.3", ""), ("LED 1 accessory 1", "F420 RGB", "")]
    def get_status(self):
        if self.lost: raise OSError("disconnected")
        return [row for i in range(5) for row in [(f"Fan {i+1} speed", 0 if i == 0 else 1000+i, "rpm"), (f"Fan {i+1} duty", self.duties[i], "%"), (f"Fan {i+1} control mode", "PWM", "")]]
    def set_fixed_speed(self, channel, duty):
        self.calls.append(("fan", channel, duty))
        if self.fail == channel: raise ValueError("write failed")
        self.duties[int(channel[3:])-1] = duty
    def set_color(self, *args, **kwargs): self.calls.append(("color", *args, kwargs) if kwargs else ("color", *args))


class WorkerTests(unittest.TestCase):
    def setUp(self):
        self.device = FakeDevice()
        self.worker = module.Worker(lambda: [self.device])
        self.counter = 0
        self.call("OpenInitialize", {"identity": module.identity(self.device)}, generation=1)
    def call(self, command, payload=None, generation=None):
        self.counter += 1
        return self.worker.handle({"v": 1, "type": "request", "id": str(self.counter), "workerSessionId": self.worker.session, "deviceGeneration": self.worker.generation if generation is None else generation, "command": command, "payload": payload or {}})
    def test_initialization_has_no_explicit_speed_writes(self):
        self.assertFalse(any(c[0] == "fan" for c in self.device.calls))
    def test_read_zero_rpm_is_present(self):
        c = self.call("ReadStatus")["status"]["channels"][0]
        self.assertEqual(c["rpm"], 0)
        self.assertEqual(c["presence"], "present")
    def test_all_channels_readback_confirmed(self):
        r = self.call("SetFanGroup", {"channels": [f"fan{i}" for i in range(1,6)], "duty": 54})
        self.assertEqual(r["outcome"], "confirmed")
        self.assertEqual([54]*5, [c["reportedDuty"] for c in r["channels"]])
    def test_partial_never_all_success(self):
        self.device.fail = "fan3"
        with patch.object(module.time, "monotonic", side_effect=[0,4]), patch.object(module.time, "sleep"):
            r = self.call("SetFanGroup", {"channels": [f"fan{i}" for i in range(1,6)], "duty": 54})
        self.assertEqual(r["outcome"], "partial")
        self.assertEqual(r["channels"][2]["write"], "failed")
        self.assertEqual(r["channels"][4]["confirmation"], "confirmed")
    def test_subset_cannot_exclude_a_connected_fan(self):
        with self.assertRaisesRegex(ValueError, "ALL_CONNECTED_FANS_REQUIRED"):
            self.call("SetFanGroup", {"channels": ["fan1"], "duty": 50})
    def test_bool_is_not_a_duty(self):
        with self.assertRaisesRegex(ValueError, "OUT_OF_RANGE"):
            self.call("SetFanGroup", {"channels": ["fan1"], "duty": True})
    def endpoint(self, **changes):
        payload = {"channel": "fan3", "duty": 62, "expectedHubId": module.identity(self.device)["id"],
                   "expectedWiringSignature": module.wiring_signature(self.worker.status()), "expectedCurrentDuty": 45}
        payload.update(changes)
        return payload
    def test_endpoint_changes_only_the_addressed_fan(self):
        r = self.call("SetFanEndpoint", self.endpoint())
        self.assertEqual(r["outcome"], "confirmed")
        self.assertEqual([(c["id"], c["reportedDuty"]) for c in r["channels"]], [("fan3", 62)])
        self.assertEqual(self.device.duties, [45, 45, 62, 45, 45])
        self.assertEqual([c for c in self.device.calls if c[0] == "fan"], [("fan", "fan3", 62)])
    def test_endpoint_with_a_valid_deadline_writes_only_one_fan(self):
        payload = self.endpoint(validUntilTick64=2000)
        with patch.object(module, "tick_count64", side_effect=[1000, 1500]):
            result = self.call("SetFanEndpoint", payload)
        self.assertEqual(result["outcome"], "confirmed")
        self.assertEqual(self.device.duties, [45, 45, 62, 45, 45])
    def test_endpoint_deadline_expiring_during_status_read_never_writes(self):
        payload = self.endpoint(validUntilTick64=1200, duty=0)
        with patch.object(module, "tick_count64", side_effect=[1000, 1200]), \
             patch.object(self.device, "get_status", wraps=self.device.get_status) as read, \
             self.assertRaisesRegex(ValueError, "COOLING_INPUT_EXPIRED"):
            self.call("SetFanEndpoint", payload)
        read.assert_called_once()
        self.assertFalse(any(c[0] == "fan" for c in self.device.calls))
        self.assertEqual(self.device.duties, [45] * 5)
    def test_already_expired_endpoint_deadline_does_not_even_read(self):
        payload = self.endpoint(validUntilTick64=1000)
        with patch.object(module, "tick_count64", return_value=1000), \
             patch.object(self.device, "get_status") as read, \
             self.assertRaisesRegex(ValueError, "COOLING_INPUT_EXPIRED"):
            self.call("SetFanEndpoint", payload)
        read.assert_not_called()
        self.assertFalse(any(c[0] == "fan" for c in self.device.calls))
    def test_malformed_or_unbounded_endpoint_deadlines_never_write(self):
        for deadline in [None, True, 0, -1, "1200", 1200.0, 5000, 2 ** 63]:
            with self.subTest(deadline=deadline), patch.object(module, "tick_count64", return_value=1000), \
                 self.assertRaisesRegex(ValueError, "INVALID_FAN_DEADLINE"):
                self.call("SetFanEndpoint", self.endpoint(validUntilTick64=deadline))
        self.assertFalse(any(c[0] == "fan" for c in self.device.calls))
    def test_endpoint_invalid_expectations_never_write(self):
        cases = [({"expectedHubId": "another-hub"}, "DEVICE_CHANGED"),
                 ({"expectedWiringSignature": "old-wiring"}, "WIRING_CHANGED"),
                 ({"expectedCurrentDuty": 44}, "EXTERNAL_DUTY_CHANGE"),
                 ({"expectedCurrentDuty": True}, "EXTERNAL_DUTY_CHANGE"),
                 ({"channel": "sync"}, "INVALID_FAN_ENDPOINT"),
                 ({"channel": ["fan1"]}, "INVALID_FAN_ENDPOINT"),
                 ({"duty": True}, "OUT_OF_RANGE"), ({"duty": 101}, "OUT_OF_RANGE"),
                 ({"channels": ["fan1"]}, "INVALID_ENDPOINT_PARAMETERS")]
        for changes, error in cases:
            with self.subTest(changes=changes), self.assertRaisesRegex(ValueError, error):
                self.call("SetFanEndpoint", self.endpoint(**changes))
        self.assertFalse(any(c[0] == "fan" for c in self.device.calls))
    def test_endpoint_live_duty_change_rejected(self):
        payload = self.endpoint()
        self.device.duties[2] = 70
        with self.assertRaisesRegex(ValueError, "EXTERNAL_DUTY_CHANGE"):
            self.call("SetFanEndpoint", payload)
        self.assertFalse(any(c[0] == "fan" for c in self.device.calls))
    def test_endpoint_live_control_mode_change_rejected(self):
        payload = self.endpoint()
        rows = [(name, "DC" if name == "Fan 3 control mode" else value, unit)
                for name, value, unit in self.device.get_status()]
        with patch.object(self.device, "get_status", return_value=rows), self.assertRaisesRegex(ValueError, "WIRING_CHANGED"):
            self.call("SetFanEndpoint", payload)
        self.assertFalse(any(c[0] == "fan" for c in self.device.calls))
    def test_endpoint_rejects_a_request_from_an_old_connection(self):
        with self.assertRaisesRegex(ValueError, "STALE_GENERATION"):
            self.call("SetFanEndpoint", self.endpoint(), generation=0)
        with self.assertRaisesRegex(ValueError, "STALE_SESSION"):
            self.worker.handle({"v": 1, "type": "request", "id": "old-endpoint", "workerSessionId": "old",
                                "deviceGeneration": 1, "command": "SetFanEndpoint", "payload": self.endpoint()})
        self.assertFalse(any(c[0] == "fan" for c in self.device.calls))
    def test_endpoint_readback_mismatch_is_not_confirmed(self):
        payload = self.endpoint()
        with patch.object(self.device, "set_fixed_speed"), patch.object(module.time, "monotonic", side_effect=[0, 4]):
            r = self.call("SetFanEndpoint", payload)
        self.assertEqual(r["outcome"], "partial")
        self.assertEqual(r["channels"][0]["confirmation"], "mismatch")
        self.assertEqual(r["channels"][0]["reportedDuty"], 45)
    def test_endpoint_disconnect_during_write_is_unknown(self):
        payload = self.endpoint()
        with patch.object(self.device, "set_fixed_speed", side_effect=OSError("lost")) as write:
            r = self.call("SetFanEndpoint", payload)
        write.assert_called_once_with("fan3", 62)
        self.assertEqual(r["outcome"], "unknown")
        self.assertEqual(r["channels"][0]["write"], "unknown")
    def test_wiring_signature_matches_csharp_order_and_null_handling(self):
        status = {"channels": [{"id": "fan3", "controlMode": ""}, {"id": "fan1", "controlMode": None},
                               {"id": "fan2", "controlMode": "PWM"}],
                  "lighting": [{"id": "led2", "accessory": "B"}, {"id": "led1", "accessory": "A"}]}
        self.assertEqual(module.wiring_signature(status), "fan1:none;fan2:PWM;fan3:|led1:A;led2:B")
    def test_lighting_off_uses_off_command_not_screen_color(self):
        r = self.call("SetLighting", {"channels": ["led1"], "preset": "off"})
        self.assertEqual(r["outcome"], "writtenUnverified")
        self.assertIn(("color", "led1", "off", []), self.device.calls)
        self.assertFalse(any(c[0] == "fan" for c in self.device.calls))
    def test_old_generation_rejected(self):
        with self.assertRaisesRegex(ValueError, "STALE_GENERATION"): self.call("ReadStatus", generation=0)
    def test_old_worker_session_rejected(self):
        with self.assertRaisesRegex(ValueError, "STALE_SESSION"):
            self.worker.handle({"v":1,"type":"request","id":"x","workerSessionId":"old","deviceGeneration":1,"command":"ReadStatus"})
    def test_same_device_is_not_selected_when_ambiguous(self):
        self.worker.finder = lambda: [self.device, self.device]
        with self.assertRaisesRegex(ValueError, "DEVICE_NOT_UNIQUE"):
            self.call("OpenInitialize", {"identity": module.identity(self.device)}, generation=2)
    def test_json_noise_and_oversize_recovery(self):
        stream = io.BytesIO(b"noise\n" + b"x" * module.MAX_FRAME + b"{}\n" + b'{"v":1}\r\n')
        self.assertEqual(list(module.read_frames(stream)), [{"v":1}])
    def test_shutdown_disconnects(self):
        self.call("Shutdown")
        self.assertIsNone(self.worker.device)


class GuardedLightingTests(unittest.TestCase):
    setUp = WorkerTests.setUp
    call = WorkerTests.call

    def lighting(self, **changes):
        payload = {"channels": ["led1"], "mode": "fixed", "colors": [[17, 34, 51]], "speed": 2, "reverse": False,
                   "expectedHubId": module.identity(self.device)["id"],
                   "expectedWiringSignature": module.wiring_signature(self.worker.status())}
        payload.update(changes)
        return payload

    def colors(self):
        return [c for c in self.device.calls if c[0] == "color"]

    def add_ports(self):
        self.worker.leds.extend({"id": f"led{i}", "accessory": "RGB", "ledCount": None} for i in (2, 3))

    def test_only_selected_port_is_written_and_never_claims_readback(self):
        self.add_ports()
        result = self.call("SetLightingEndpoints", self.lighting(channels=["led2"]))
        self.assertEqual(result["outcome"], "writtenUnverified")
        self.assertEqual([(c["id"], c["confirmation"]) for c in result["channels"]], [("led2", "writtenUnverified")])
        self.assertEqual(self.colors(), [("color", "led2", "fixed", [[17, 34, 51]], {"speed": "normal", "direction": "forward"})])
        self.assertFalse(any(c[0] == "fan" for c in self.device.calls))

    def test_expired_deadline_is_rejected_without_status_or_color_io(self):
        payload = self.lighting(validUntilTick64=1000)
        with patch.object(module, "tick_count64", return_value=1000), patch.object(self.device, "get_status") as read, \
             self.assertRaisesRegex(ValueError, "LIGHTING_FRAME_EXPIRED"):
            self.call("SetLightingEndpoints", payload)
        read.assert_not_called()
        self.assertEqual(self.colors(), [])

    def test_deadline_expiring_inside_status_returns_unwritten_port(self):
        payload = self.lighting(validUntilTick64=1200)
        with patch.object(module, "tick_count64", side_effect=[1000, 1100, 1200]):
            result = self.call("SetLightingEndpoints", payload)
        self.assertEqual(result["outcome"], "rejected")
        self.assertEqual(result["error"], "LIGHTING_FRAME_EXPIRED")
        self.assertEqual(result["channels"][0]["write"], "notExecuted")
        self.assertEqual(self.colors(), [])

    def test_deadline_after_one_write_retains_partial_result_and_stops_remaining_ports(self):
        self.add_ports()
        payload = self.lighting(channels=["led1", "led2", "led3"], validUntilTick64=1200)
        with patch.object(module, "tick_count64", side_effect=[1000, 1050, 1100, 1200]):
            result = self.call("SetLightingEndpoints", payload)
        self.assertEqual(result["outcome"], "partial")
        self.assertEqual(result["error"], "LIGHTING_FRAME_EXPIRED")
        self.assertEqual([c["write"] for c in result["channels"]], ["completed", "notExecuted", "notExecuted"])
        self.assertEqual(len(self.colors()), 1)

    def test_three_port_frame_does_not_wait_for_three_fan_reports(self):
        self.add_ports()
        payload = self.lighting(channels=["led1", "led2", "led3"], validUntilTick64=1500)
        now = [1000]
        read, write = self.device.get_status, self.device.set_color
        def next_report():
            now[0] += 250  # The pinned driver discards queued reports and waits for a new one.
            return read()
        def color(*args, **kwargs):
            now[0] += 5
            return write(*args, **kwargs)
        with patch.object(module, "tick_count64", side_effect=lambda: now[0]), \
             patch.object(self.device, "get_status", side_effect=next_report) as status, \
             patch.object(self.device, "set_color", side_effect=color):
            result = self.call("SetLightingEndpoints", payload)
        self.assertEqual(result["outcome"], "writtenUnverified")
        self.assertEqual([c["write"] for c in result["channels"]], ["completed"] * 3)
        self.assertEqual(status.call_count, 1)
        self.assertEqual([c[1] for c in self.colors()], ["led1", "led2", "led3"])
        self.assertLess(now[0], 1500)

    def test_frame_status_is_never_reused_for_a_later_request(self):
        self.add_ports()
        payload = self.lighting(channels=["led1", "led2", "led3"], validUntilTick64=1500)
        original = self.device.get_status()
        changed = [(name, "DC" if name == "Fan 1 control mode" else value, unit) for name, value, unit in original]
        with patch.object(module, "tick_count64", return_value=1000), \
             patch.object(self.device, "get_status", side_effect=[original, changed]) as status:
            first = self.call("SetLightingEndpoints", payload)
            second = self.call("SetLightingEndpoints", payload)
        self.assertEqual(first["outcome"], "writtenUnverified")
        self.assertEqual((second["outcome"], second["error"]), ("rejected", "WIRING_CHANGED"))
        self.assertEqual(status.call_count, 2)
        self.assertEqual(len(self.colors()), 3)

    def test_frame_checks_identity_after_each_port_even_with_shared_status(self):
        self.add_ports()
        payload = self.lighting(channels=["led1", "led2", "led3"], validUntilTick64=1500)
        write = self.device.set_color
        def replace(*args, **kwargs):
            write(*args, **kwargs)
            self.device.serial_number = "replacement"
        with patch.object(module, "tick_count64", return_value=1000), \
             patch.object(self.device, "set_color", side_effect=replace):
            result = self.call("SetLightingEndpoints", payload)
        self.assertEqual((result["outcome"], result["error"]), ("partial", "DEVICE_CHANGED"))
        self.assertEqual([c["write"] for c in result["channels"]], ["completed", "notExecuted", "notExecuted"])

    def test_non_frame_commands_still_recheck_wiring_for_each_port(self):
        self.add_ports()
        payload = self.lighting(channels=["led1", "led2", "led3"])
        original = self.device.get_status()
        changed = [(name, "DC" if name == "Fan 1 control mode" else value, unit) for name, value, unit in original]
        with patch.object(self.device, "get_status", side_effect=[original, changed]) as status:
            result = self.call("SetLightingEndpoints", payload)
        self.assertEqual((result["outcome"], result["error"]), ("partial", "WIRING_CHANGED"))
        self.assertEqual(status.call_count, 2)
        self.assertEqual(len(self.colors()), 1)

    def test_malformed_deadlines_never_read_or_write(self):
        for value in [None, True, 0, -1, "2000", 2000.0, 5000, 2 ** 63]:
            payload = self.lighting(validUntilTick64=value)
            with self.subTest(value=value), patch.object(module, "tick_count64", return_value=1000), \
                 patch.object(self.device, "get_status") as read, self.assertRaisesRegex(ValueError, "INVALID_LIGHTING_DEADLINE"):
                self.call("SetLightingEndpoints", payload)
            read.assert_not_called()
        self.assertEqual(self.colors(), [])

    def test_invalid_fields_effects_and_targets_never_write(self):
        cases = [{"unknown": 1}, {"expectedHubId": None}, {"expectedWiringSignature": ""},
                 {"channels": ["sync"]}, {"channels": ["led1", "led1"]}, {"channels": ["led5"]},
                 {"mode": "color-cycle"}, {"colors": [[True, 0, 0]]}, {"speed": 5}, {"reverse": 1}]
        for changes in cases:
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                self.call("SetLightingEndpoints", self.lighting(**changes))
        self.assertEqual(self.colors(), [])

    def test_identity_and_wiring_mismatch_are_rejected(self):
        for changes, error in [({"expectedHubId": "other"}, "DEVICE_CHANGED"),
                               ({"expectedWiringSignature": "old"}, "WIRING_CHANGED")]:
            result = self.call("SetLightingEndpoints", self.lighting(**changes))
            self.assertEqual((result["outcome"], result["error"]), ("rejected", error))
        self.assertEqual(self.colors(), [])

    def test_live_wiring_change_during_status_prevents_output(self):
        payload = self.lighting()
        status = [(name, "DC" if name == "Fan 1 control mode" else value, unit)
                  for name, value, unit in self.device.get_status()]
        with patch.object(self.device, "get_status", return_value=status):
            result = self.call("SetLightingEndpoints", payload)
        self.assertEqual((result["outcome"], result["error"]), ("rejected", "WIRING_CHANGED"))
        self.assertEqual(self.colors(), [])

    def test_replacement_after_first_port_cannot_receive_second_port_write(self):
        self.add_ports()
        payload = self.lighting(channels=["led1", "led2", "led3"])
        write = self.device.set_color
        def replace(*args, **kwargs):
            write(*args, **kwargs)
            self.device.serial_number = "replacement"
        with patch.object(self.device, "set_color", side_effect=replace):
            result = self.call("SetLightingEndpoints", payload)
        self.assertEqual((result["outcome"], result["error"]), ("partial", "DEVICE_CHANGED"))
        self.assertEqual([c["write"] for c in result["channels"]], ["completed", "notExecuted", "notExecuted"])
        self.assertEqual(len(self.colors()), 1)

    def test_any_exception_during_a_color_write_remains_unknown(self):
        self.add_ports()
        payload = self.lighting(channels=["led1", "led2", "led3"])
        for error in [OSError("lost"), ValueError("commit failed")]:
            with self.subTest(error=type(error)), patch.object(self.device, "set_color", side_effect=error) as write:
                result = self.call("SetLightingEndpoints", payload)
            write.assert_called_once()
            self.assertEqual(result["outcome"], "unknown")
            self.assertEqual([c["write"] for c in result["channels"]], ["unknown", "notExecuted", "notExecuted"])

    def test_partial_write_failure_retains_completed_and_unknown_ports(self):
        self.add_ports()
        with patch.object(self.device, "set_color", side_effect=[None, OSError("lost")]) as write:
            result = self.call("SetLightingEndpoints", self.lighting(channels=["led1", "led2", "led3"]))
        self.assertEqual(write.call_count, 2)
        self.assertEqual(result["outcome"], "partial")
        self.assertEqual([c["write"] for c in result["channels"]], ["completed", "unknown", "notExecuted"])

    def test_old_connection_generation_and_session_cannot_send_color(self):
        payload = self.lighting()
        with self.assertRaisesRegex(ValueError, "STALE_GENERATION"):
            self.call("SetLightingEndpoints", payload, generation=0)
        with self.assertRaisesRegex(ValueError, "STALE_SESSION"):
            self.worker.handle({"v": 1, "type": "request", "id": "stale", "workerSessionId": "old",
                                "deviceGeneration": 1, "command": "SetLightingEndpoints", "payload": payload})
        self.assertEqual(self.colors(), [])


if __name__ == "__main__": unittest.main(verbosity=2)
