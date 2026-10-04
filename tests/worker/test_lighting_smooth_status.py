"""Pinned ControlHub encoder and receive path on a fake HID; never opens USB."""
import importlib.util
import pathlib
import unittest
from unittest.mock import patch

from liquidctl.driver.control_hub import ControlHub
from liquidctl.error import Timeout

ROOT = pathlib.Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("smooth_worker", ROOT / "worker/hub_worker.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def report(mode=2, duty=50, rpm=1300):
    msg = bytearray(64)
    msg[:2] = b"\x67\x02"
    for i in range(5):
        msg[16+i] = mode
        msg[24+2*i:26+2*i] = rpm.to_bytes(2, "little")
        msg[40+i] = duty
    return msg


class Hid:
    path = b"memory-only-smooth-lighting"

    def __init__(self):
        self.queue, self.writes = [], []
        self.failure = None

    def read(self, length, *, timeout):
        assert timeout == 1
        if self.failure:
            raise self.failure
        if self.queue:
            return self.queue.pop(0)
        raise Timeout()

    def write(self, data):
        self.writes.append(bytes(data))


class SmoothStatusTests(unittest.TestCase):
    def setUp(self):
        self.now = 1.0
        self.clock = patch.object(module.time, "monotonic", side_effect=lambda: self.now)
        self.clock.start()
        self.addCleanup(self.clock.stop)
        self.tick = patch.object(module, "tick_count64", side_effect=lambda: int(self.now * 1000))
        self.tick.start()
        self.addCleanup(self.tick.stop)
        self.hid = Hid()
        self.device = ControlHub(self.hid, "Memory-only ControlHub", 5, 5)
        self.device._CHANNEL_BYTE_MAP = {0: 2, 1: 1, 2: 4, 3: 8, 4: 16}
        self.worker = module.Worker(lambda: [self.device])
        self.worker.device = self.device
        self.worker.leds = [{"id": f"led{i}", "accessory": "RGB", "ledCount": 24} for i in range(1, 6)]
        self.reader = self.worker.lighting_reader = module.LightingStatusReader(self.device)
        self.original = {"channels": [{"id": f"fan{i}", "presence": "present", "rpm": 1300, "duty": 50, "controlMode": "PWM"} for i in range(1, 6)],
                         "lighting": self.worker.leds, "firmware": None}
        self.reader.seed(self.original)
        self.worker.fans = [c["id"] for c in self.original["channels"]]

    def call(self, command, payload):
        return self.worker.handle({"v": 1, "type": "request", "id": "smooth", "workerSessionId": self.worker.session,
                                   "deviceGeneration": 0, "command": command, "payload": payload})

    def frame(self, **changes):
        payload = {"channels": ["led1", "led2", "led3"], "mode": "fixed", "colors": [[17, 34, 51]], "speed": 2, "reverse": False,
                   "expectedHubId": module.identity(self.device)["id"], "expectedWiringSignature": module.wiring_signature(self.original),
                   "validUntilTick64": int(self.now * 1000) + 500}
        payload.update(changes)
        return self.call("SetLightingEndpoints", payload)

    def test_three_ports_and_monitoring_run_between_half_second_reports_without_blocking_status_or_fan_writes(self):
        last_report = self.now
        with patch.object(self.device, "get_status", side_effect=AssertionError("blocking status on a frame")):
            for i in range(180):
                self.now = 1 + i * .034
                if self.now - last_report >= .5:
                    self.hid.queue.append(report())
                    last_report = self.now
                self.assertEqual("writtenUnverified", self.frame()["outcome"])
                if i % 30 == 0:
                    status = self.call("ReadStatus", {})["status"]
                    self.assertLessEqual(status["sampleAgeMilliseconds"], 550)
                    self.assertEqual(self.original["channels"], status["channels"])
        self.assertEqual(1080, len(self.hid.writes))
        self.assertEqual([2, 2, 1, 1, 4, 4] * 180, [p[2] for p in self.hid.writes])
        self.assertTrue(all(p[0] == 0x26 for p in self.hid.writes))

    def test_cached_sample_age_is_not_reset_and_missing_reports_stop_output(self):
        self.now += .5
        self.assertGreaterEqual(self.reader.read()["sampleAgeMilliseconds"], 500)
        self.now += .251
        result = self.frame()
        self.assertEqual("LIGHTING_STATUS_STALE", result["error"])
        self.assertEqual([], self.hid.writes)
        self.assertIsNone(self.reader.sample)

    def test_queued_report_age_is_conservative_and_latest_report_wins(self):
        self.now += .3
        self.hid.queue.extend([report(duty=40), report(duty=60)])
        status = self.reader.read()
        self.assertGreaterEqual(status["sampleAgeMilliseconds"], 300)
        self.assertTrue(all(c["duty"] == 60 for c in status["channels"]))
        self.now += .02
        self.assertGreaterEqual(self.reader.read()["sampleAgeMilliseconds"], 320)

    def test_wiring_change_blocks_all_ports(self):
        self.hid.queue.append(report(mode=1))
        self.assertEqual("WIRING_CHANGED", self.frame()["error"])
        self.assertEqual([], self.hid.writes)

    def delayed_report(self, mode=2):
        self.hid.queue.append(report(mode=mode))
        read = self.hid.read
        def delayed(*args, **kwargs):
            result = read(*args, **kwargs)
            self.now += .025
            return result
        return patch.object(self.hid, "read", side_effect=delayed)

    def test_one_late_report_skips_a_frame_then_resumes_without_rejuvenating_the_sample(self):
        with self.delayed_report():
            result = self.frame()
        self.assertEqual("LIGHTING_FRAME_EXPIRED", result["error"])
        self.assertTrue(all(r["write"] == "notExecuted" for r in result["channels"]))
        self.assertEqual([], self.hid.writes)
        self.assertEqual(1.0, self.reader.sampled_at)
        self.now += .034
        self.assertEqual("writtenUnverified", self.frame()["outcome"])
        self.assertEqual(6, len(self.hid.writes))
        self.assertGreaterEqual(self.reader.read()["sampleAgeMilliseconds"], 59)

    def test_late_wiring_change_is_retained_and_rejected_on_the_next_frame(self):
        with self.delayed_report(mode=1):
            self.assertEqual("LIGHTING_FRAME_EXPIRED", self.frame()["error"])
        self.assertEqual("WIRING_CHANGED", self.frame()["error"])
        self.assertEqual([], self.hid.writes)

    def test_monitoring_falls_back_to_a_fresh_read_after_a_short_receive_delay(self):
        self.frame()
        with self.delayed_report(), patch.object(self.worker, "status", return_value=self.original) as fresh:
            self.assertEqual(self.original, self.call("ReadStatus", {})["status"])
            fresh.assert_called_once()

    def test_missing_reports_after_a_deferred_read_still_expire(self):
        with self.delayed_report():
            self.frame()
        self.now = 1.751
        self.assertEqual("LIGHTING_STATUS_STALE", self.frame()["error"])
        self.assertIsNone(self.reader.sample)
        self.assertEqual([], self.hid.writes)

    def test_repeated_short_delays_recover_between_reports_without_extra_writes(self):
        for _ in range(30):
            self.now += .45
            count = len(self.hid.writes)
            with self.delayed_report():
                self.assertEqual("LIGHTING_FRAME_EXPIRED", self.frame()["error"])
            self.assertEqual(count, len(self.hid.writes))
            self.now += .034
            self.assertEqual("writtenUnverified", self.frame()["outcome"])
            self.assertEqual(count + 6, len(self.hid.writes))
            self.assertGreaterEqual(self.reader.read()["sampleAgeMilliseconds"], 509)

    def test_continuous_slow_reports_cannot_extend_freshness_without_a_completed_drain(self):
        errors = []
        for _ in range(40):
            with self.delayed_report():
                errors.append(self.frame()["error"])
            if errors[-1] == "LIGHTING_STATUS_STALE":
                break
        self.assertGreater(len(errors), 1)
        self.assertEqual("LIGHTING_STATUS_STALE", errors[-1])
        self.assertTrue(all(e == "LIGHTING_FRAME_EXPIRED" for e in errors[:-1]))
        self.assertIsNone(self.reader.sample)
        self.assertEqual([], self.hid.writes)

    def test_disconnect_after_a_deferred_report_is_not_treated_as_another_skipped_frame(self):
        with self.delayed_report():
            self.assertEqual("LIGHTING_FRAME_EXPIRED", self.frame()["error"])
        self.hid.failure = OSError("unplugged")
        self.assertNotEqual("LIGHTING_FRAME_EXPIRED", self.frame()["error"])
        self.assertIsNone(self.reader.sample)
        self.assertEqual([], self.hid.writes)

    def test_disconnect_malformed_report_and_backlog_invalidate_sample_before_writing(self):
        for kind in ("disconnect", "short", "bad-mode", "bad-duty", "backlog"):
            with self.subTest(kind=kind):
                self.reader.seed(self.original)
                self.hid.queue = []
                self.hid.failure = OSError("unplugged") if kind == "disconnect" else None
                if kind == "short": self.hid.queue = [b"\x67\x02"]
                if kind == "bad-mode": self.hid.queue = [report(mode=3)]
                if kind == "bad-duty": self.hid.queue = [report(duty=101)]
                if kind == "backlog": self.hid.queue = [report()] * 64
                self.assertEqual("rejected", self.frame()["outcome"])
                self.assertIsNone(self.reader.sample)
                self.assertEqual([], self.hid.writes)

    def test_expired_frame_and_changed_identity_never_reuse_good_cache(self):
        with self.assertRaisesRegex(ValueError, "LIGHTING_FRAME_EXPIRED"):
            self.frame(validUntilTick64=1000)
        self.assertEqual("DEVICE_CHANGED", self.frame(expectedHubId="replacement")["error"])
        self.assertEqual([], self.hid.writes)

    def test_monitoring_does_not_extend_stream_lifetime_and_nonframe_writes_still_force_fresh_status(self):
        self.frame()
        self.now += .5
        self.call("ReadStatus", {})
        self.assertEqual(1.0, self.reader.last_frame)
        with patch.object(self.worker, "status", return_value=self.original) as fresh:
            self.now += .251
            self.call("ReadStatus", {})
            fresh.assert_called_once()
        with patch.object(self.worker, "status", return_value=self.original) as fresh:
            payload = {"channels": ["led1"], "mode": "fixed", "colors": [[17, 34, 51]], "speed": 2, "reverse": False,
                       "expectedHubId": module.identity(self.device)["id"], "expectedWiringSignature": module.wiring_signature(self.original)}
            self.assertEqual("writtenUnverified", self.call("SetLightingEndpoints", payload)["outcome"])
            fresh.assert_called_once()

    def test_fan_write_checks_fresh_duty_even_when_lighting_cache_matches_old_duty(self):
        changed = {**self.original, "channels": [{**c, "duty": 70} for c in self.original["channels"]]}
        with patch.object(self.worker, "status", return_value=changed) as fresh:
            with self.assertRaisesRegex(ValueError, "EXTERNAL_DUTY_CHANGE"):
                self.call("SetFanEndpoint", {"channel": "fan1", "duty": 60, "expectedCurrentDuty": 50,
                    "expectedHubId": module.identity(self.device)["id"], "expectedWiringSignature": module.wiring_signature(self.original)})
            fresh.assert_called_once()
        self.assertEqual([], self.hid.writes)

    def fan_readback_fixture(self, *, mismatch=False):
        def values():
            self.now += .5
            written = any(packet[:2] == b"\x62\x01" for packet in self.hid.writes)
            duty = 60 if written and not mismatch else 50
            return [row for i in range(1, 6) for row in (
                (f"Fan {i} control mode", "PWM", ""),
                (f"Fan {i} speed", 1300, "rpm"),
                (f"Fan {i} duty", duty if i == 1 else 50, "%"))]
        return patch.object(self.device, "get_status", side_effect=values)

    def fan(self, **changes):
        payload = {"channel": "fan1", "duty": 60, "expectedCurrentDuty": 50,
                   "expectedHubId": module.identity(self.device)["id"],
                   "expectedWiringSignature": module.wiring_signature(self.original)}
        payload.update(changes)
        return self.call("SetFanEndpoint", payload)

    def test_monitoring_reuses_confirmed_fan_readback_after_a_long_frame_pause(self):
        self.frame()
        with self.fan_readback_fixture() as fresh:
            self.assertEqual("confirmed", self.fan()["outcome"])
            self.assertEqual(2, fresh.call_count)
            self.assertGreater(self.now - self.reader.last_frame, .750)
            sampled = self.reader.sampled_at
            self.now += .020
            status = self.call("ReadStatus", {})["status"]
            self.assertEqual(2, fresh.call_count)
            self.assertEqual(60, status["channels"][0]["duty"])
            self.assertGreaterEqual(status["sampleAgeMilliseconds"], 20)
            self.assertEqual(sampled, self.reader.sampled_at)
            self.assertEqual(1.0, self.reader.last_frame)

    def test_monitoring_cannot_extend_the_confirmed_fan_readback_window(self):
        with self.fan_readback_fixture() as fresh:
            self.assertEqual("confirmed", self.fan()["outcome"])
            sampled = self.now
            for offset in (0, .050, .099):
                self.now = sampled + offset
                self.call("ReadStatus", {})
                self.assertEqual(2, fresh.call_count)
                self.assertEqual(sampled, self.reader.sampled_at)
            self.now = sampled + .101
            self.call("ReadStatus", {})
            self.assertEqual(3, fresh.call_count)
            # A monitoring read cannot arm another post-fan reuse window.
            self.call("ReadStatus", {})
            self.assertEqual(4, fresh.call_count)

    def test_initial_status_without_a_confirmed_fan_write_does_not_enable_readback_reuse(self):
        with self.fan_readback_fixture() as fresh:
            self.call("ReadStatus", {})
            self.call("ReadStatus", {})
            self.assertEqual(2, fresh.call_count)

    def test_failed_fan_readback_does_not_enable_monitoring_reuse(self):
        with self.fan_readback_fixture(mismatch=True) as fresh, patch.object(module.time, "sleep"):
            self.assertNotEqual("confirmed", self.fan()["outcome"])
            before = fresh.call_count
            self.call("ReadStatus", {})
            self.assertEqual(before + 1, fresh.call_count)

    def test_next_fan_command_still_gets_two_new_reports_after_a_confirmed_readback(self):
        with self.fan_readback_fixture() as fresh:
            self.assertEqual("confirmed", self.fan()["outcome"])
            self.assertEqual("confirmed", self.fan(expectedCurrentDuty=60)["outcome"])
            self.assertEqual(4, fresh.call_count)

    def test_rejected_fan_precheck_clears_the_previous_confirmed_readback_window(self):
        with self.fan_readback_fixture() as fresh:
            self.assertEqual("confirmed", self.fan()["outcome"])
            with self.assertRaisesRegex(ValueError, "EXTERNAL_DUTY_CHANGE"):
                self.fan(expectedCurrentDuty=50)
            self.assertEqual(3, fresh.call_count)
            self.call("ReadStatus", {})
            self.assertEqual(4, fresh.call_count)

    def test_post_fan_monitoring_drains_wiring_changes_before_the_next_frame(self):
        with self.fan_readback_fixture() as fresh:
            self.assertEqual("confirmed", self.fan()["outcome"])
            self.hid.queue.append(report(mode=1))
            status = self.call("ReadStatus", {})["status"]
            self.assertEqual(2, fresh.call_count)
            self.assertEqual("DC", status["channels"][0]["controlMode"])
            count = len(self.hid.writes)
            self.assertEqual("WIRING_CHANGED", self.frame()["error"])
            self.assertEqual(count, len(self.hid.writes))

    def test_post_fan_receive_delay_falls_back_to_a_fresh_monitoring_read(self):
        with self.fan_readback_fixture() as fresh:
            self.assertEqual("confirmed", self.fan()["outcome"])
            with self.delayed_report():
                status = self.call("ReadStatus", {})["status"]
            self.assertEqual(3, fresh.call_count)
            self.assertEqual(60, status["channels"][0]["duty"])

    def test_post_fan_monitoring_late_timeout_cannot_extend_the_100ms_window(self):
        with self.fan_readback_fixture() as fresh:
            self.assertEqual("confirmed", self.fan()["outcome"])
            def late_timeout(*args, **kwargs):
                self.now += .150
                raise Timeout()
            with patch.object(self.hid, "read", side_effect=late_timeout):
                self.call("ReadStatus", {})
            self.assertEqual(3, fresh.call_count)

    def test_post_fan_monitoring_does_not_hide_a_disconnect(self):
        with self.fan_readback_fixture() as fresh:
            self.assertEqual("confirmed", self.fan()["outcome"])
            self.hid.failure = OSError("unplugged")
            with self.assertRaisesRegex(OSError, "unplugged"):
                self.call("ReadStatus", {})
            self.assertEqual(2, fresh.call_count)
            self.assertIsNone(self.reader.sample)

    def test_post_fan_monitoring_rejects_future_timestamp_or_another_device_cache(self):
        for condition in ("future", "another-device", "invalidated"):
            with self.subTest(condition=condition), self.fan_readback_fixture() as fresh:
                self.reader.device = self.device
                self.hid.writes.clear()
                self.assertEqual("confirmed", self.fan()["outcome"])
                if condition == "future": self.now -= .001
                if condition == "another-device": self.reader.device = object()
                if condition == "invalidated": self.reader.sample = None
                self.call("ReadStatus", {})
                self.assertEqual(3, fresh.call_count)


if __name__ == "__main__":
    unittest.main(verbosity=2)
