"""NZXT Control Hub worker. stdout is a bounded JSON Lines protocol, never a log.

Only this process imports liquidctl or opens a Hub. Importing this module does not
enumerate devices. Tests use fake drivers and a recording HID for packet checks.
"""
from __future__ import annotations

import hashlib
import ctypes
import importlib.metadata
import json
import logging
import math
import re
import sys
import time
import uuid

MAX_FRAME = 65536
REQUIRED_LIQUIDCTL = "1.16.0"
COLORS = {"blue": [52, 137, 255], "white": [233, 242, 255], "purple": [160, 121, 236]}
# Accessory identity facts, not a change to the 24-pixel packet encoder.
# Evidence and the conflicting vendor support article are recorded in
# docs/汎用版_F420_RGB識別とLED数の再確認_20260929.md.
CONTROL_HUB_ACCESSORIES = {
    0x38: ("F420 RGB (2026)", 24),
    0x3f: ("F420 RGB Reverse (2026)", 24),
}
_tick_count64 = None


def tick_count64():
    """Use the same OS clock as the C# host, including waits and system sleep."""
    global _tick_count64
    if _tick_count64 is None:
        if sys.platform != "win32":
            raise RuntimeError("FAN_DEADLINE_CLOCK_UNAVAILABLE")
        _tick_count64 = ctypes.WinDLL("kernel32", use_last_error=True).GetTickCount64
        _tick_count64.argtypes = []
        _tick_count64.restype = ctypes.c_uint64
    return _tick_count64()


def check_fan_deadline(deadline):
    check_write_deadline(deadline, "INVALID_FAN_DEADLINE", "COOLING_INPUT_EXPIRED")


def check_lighting_deadline(deadline):
    check_write_deadline(deadline, "INVALID_LIGHTING_DEADLINE", "LIGHTING_FRAME_EXPIRED")


def check_write_deadline(deadline, invalid, expired):
    if type(deadline) is not int or not 0 < deadline <= 0x7fffffffffffffff:
        raise ValueError(invalid)
    now = tick_count64()
    if now >= deadline:
        raise ValueError(expired)
    if deadline - now > 3000:
        raise ValueError(invalid)


def wiring_signature(status):
    return ";".join(f"{c['id']}:{c['controlMode'] if c.get('controlMode') is not None else 'none'}" for c in sorted(status["channels"], key=lambda c: c["id"])) + "|" + ";".join(
        f"{c['id']}:{c['accessory']}" for c in sorted(status["lighting"], key=lambda c: c["id"]))


def identity(device):
    raw_path = getattr(device.device, "path", None) or getattr(device, "address", "")
    path = raw_path.decode("utf-8", "backslashreplace") if isinstance(raw_path, bytes) else str(raw_path)
    serial = getattr(device, "serial_number", None) or None
    key = f"serial:{serial}" if serial else f"path:{path}"
    return {"id": hashlib.sha256(key.encode()).hexdigest()[:24], "description": device.description,
            "serial": serial, "path": path}


def describe_accessory(value):
    code = getattr(value, "value", None)
    known = CONTROL_HUB_ACCESSORIES.get(code) if type(code) is int else None
    if known is not None:
        return f"{known[0]} (0x{code:02x})", known[1]
    # Retain the raw ID in the wiring signature, including unknown accessories.
    # A display name alone is not evidence of model or LED count.
    return (f"{value} (0x{code:02x})" if type(code) is int else str(value)), None


def discover():
    from liquidctl import find_liquidctl_devices
    from liquidctl.driver.control_hub import ControlHub
    devices = [d for d in find_liquidctl_devices(vendor=0x1E71, product=0x2022) if isinstance(d, ControlHub)]
    for device in devices:
        # Backport the post-1.16.0 lighting fix, scoped to this driver instance.
        # https://github.com/liquidctl/liquidctl/commit/6227bbfdfe33db2a8e12361b9a96495c00676275
        # Lighting ports 1/2 are swapped in the protocol; fan masks are unchanged.
        device._CHANNEL_BYTE_MAP = {0: 0x02, 1: 0x01, 2: 0x04, 3: 0x08, 4: 0x10}
        device._flowdeck_latest_status = True
    return devices


class LightingStatusDeferred(ValueError):
    """Yield the shared transport without faulting an otherwise valid stream."""
    def __init__(self):
        super().__init__("LIGHTING_FRAME_EXPIRED")


class LightingStatusReader:
    """Bounded receive-only path for the pinned ControlHub's 0x67/0x02 report.

    Start with a fresh blocking sample. While streaming, drain queued reports
    without discarding them or waiting for the next 500 ms fan report. A queued
    report has no device timestamp: use the PREVIOUS drain as its conservative
    acquisition time. Never make an old sample young just by reading it again.
    Fan commands and their readback continue to use Worker.status().
    """
    MAX_AGE = 0.750

    def __init__(self, device):
        self.device = device
        self.sample = None
        self.sampled_at = self.drained_at = 0.0
        self.last_frame = float("-inf")
        self.confirmed_fan_sample_at = float("-inf")

    def seed(self, status):
        self.sample = status
        self.sampled_at = self.drained_at = time.monotonic()
        self.confirmed_fan_sample_at = float("-inf")

    def read(self):
        from liquidctl.error import Timeout
        began = time.monotonic()
        if self.sample is None or not 0 <= began - self.drained_at <= self.MAX_AGE:
            self.sample = None
            raise ValueError("LIGHTING_STATUS_STALE")
        try:
            for _ in range(64):
                try:
                    msg = self.device.device.read(self.device._READ_LENGTH, timeout=1)
                except Timeout:
                    break
                if len(msg) < 2:
                    raise ValueError("INVALID_HUB_STATUS_REPORT")
                if bytes(msg[:2]) == b"\x67\x02":
                    if len(msg) < 57 or any(v not in (0, 1, 2) for v in msg[16:21]) or any(v > 100 for v in msg[40:45]):
                        raise ValueError("INVALID_HUB_STATUS_REPORT")
                    channels = [{"id": f"fan{i+1}", "presence": "present" if msg[16+i] else "unknown",
                                 "rpm": msg[25+2*i] << 8 | msg[24+2*i], "duty": msg[40+i],
                                 "controlMode": (None, "DC", "PWM")[msg[16+i]]} for i in range(5)]
                    self.sample = {**self.sample, "channels": channels}
                    self.sampled_at = self.drained_at
                if time.monotonic() - began > 0.010:
                    # A single read can resume late after OS scheduling. Keep its
                    # conservative timestamp, but do not output until a later
                    # attempt proves the receive queue is drained.
                    raise LightingStatusDeferred()
            else:
                raise ValueError("HUB_STATUS_BACKLOG")
            now = time.monotonic()
            age = math.ceil((now - self.sampled_at) * 1000)
            if not 0 <= age <= 750:
                raise ValueError("LIGHTING_STATUS_STALE")
            self.drained_at = began
            return {**self.sample, "sampleAgeMilliseconds": age}
        except LightingStatusDeferred:
            raise
        except Exception:
            self.sample = None
            raise


class Worker:
    def __init__(self, finder=discover):
        self.finder = finder
        self.session = uuid.uuid4().hex
        self.generation = 0
        self.device = None
        self.leds = []
        self.firmware = None
        self.fans = []
        self.lighting_reader = None

    def close(self):
        self.lighting_reader = None
        if self.device is not None:
            try:
                self.device.disconnect()
            finally:
                self.device = None

    def status(self):
        if self.device is None:
            raise RuntimeError("HUB_DISCONNECTED")
        if self.lighting_reader is not None:
            self.lighting_reader.sample = None
            self.lighting_reader.confirmed_fan_sample_at = float("-inf")
        values = {key: value for key, value, _unit in self.device.get_status()}
        channels = []
        for i in range(1, 6):
            mode = values.get(f"Fan {i} control mode")
            rpm = values.get(f"Fan {i} speed")
            duty = values.get(f"Fan {i} duty")
            channels.append({"id": f"fan{i}", "presence": "present" if mode in ("DC", "PWM") else "unknown",
                             "rpm": rpm if type(rpm) is int and rpm >= 0 else None,
                             "duty": duty if type(duty) is int and 0 <= duty <= 100 else None,
                             "controlMode": mode})
        result = {"channels": channels, "lighting": self.leds, "firmware": self.firmware}
        if self.lighting_reader is not None:
            self.lighting_reader.seed(result)
        return result

    def lighting_status(self):
        if self.lighting_reader is None:
            return self.status()
        if self.lighting_reader.device is not self.device:
            raise ValueError("DEVICE_CHANGED")
        result = self.lighting_reader.read()
        self.lighting_reader.last_frame = time.monotonic()
        return result

    def guarded_lighting(self, channels, mode, colors, speed, direction, expected):
        """No color readback is available. A successful write remains unverified.

        Accessories in the signature were captured at initialization. The fresh
        status read verifies fan wiring, not undetectable LED hot-plug or color.
        Deadline-bound frames use the bounded latest-report reader when available.
        Other drivers share one fresh blocking status within this request only.
        Device identity and the original deadline are still checked per port.
        Every set_color may contain multiple HID writes and is not atomic.
        """
        rows, stopped, uncertain = [], None, False
        frame_status = None
        for channel in channels:
            row = {"id": channel, "write": "notExecuted", "reportedDuty": None,
                   "confirmation": "unknown", "error": stopped}
            if stopped is None:
                try:
                    if "validUntilTick64" in expected:
                        check_lighting_deadline(expected["validUntilTick64"])
                    if self.device is None or identity(self.device)["id"] != expected["expectedHubId"]:
                        raise ValueError("DEVICE_CHANGED")
                    if "validUntilTick64" not in expected:
                        before = self.status()
                    else:
                        if frame_status is None:
                            frame_status = self.lighting_status()
                        before = frame_status
                    if identity(self.device)["id"] != expected["expectedHubId"]:
                        raise ValueError("DEVICE_CHANGED")
                    if wiring_signature(before) != expected["expectedWiringSignature"]:
                        raise ValueError("WIRING_CHANGED")
                    if "validUntilTick64" in expected:
                        check_lighting_deadline(expected["validUntilTick64"])
                except Exception as error:
                    # This port has not been written; earlier ports may have been.
                    stopped = str(error)[:300] if isinstance(error, ValueError) else "HUB_DISCONNECTED"
                    row["error"] = stopped
                else:
                    try:
                        self.device.set_color(channel, mode, colors, speed=speed, direction=direction)
                        row.update(write="completed", confirmation="writtenUnverified")
                    except Exception as error:
                        # Even ValueError here can follow a partially sent HID sequence.
                        stopped, uncertain = "LIGHTING_WRITE_FAILED", True
                        row.update(write="unknown", error=type(error).__name__)
                        logging.exception("Guarded lighting write incomplete: %s", channel)
            rows.append(row)
        outcome = "writtenUnverified" if stopped is None else "partial" if any(r["write"] == "completed" for r in rows) else "unknown" if uncertain else "rejected"
        return {"outcome": outcome, "channels": rows, "error": stopped}

    def handle(self, request):
        if not isinstance(request, dict) or request.get("v") != 1 or request.get("type") != "request":
            raise ValueError("PROTOCOL_MISMATCH")
        if request.get("workerSessionId") != self.session:
            raise ValueError("STALE_SESSION")
        if not isinstance(request.get("id"), str) or not 1 <= len(request["id"]) <= 128:
            raise ValueError("INVALID_ID")
        command = request.get("command")
        generation = request.get("deviceGeneration")
        if type(generation) is not int or generation < 0:
            raise ValueError("INVALID_GENERATION")
        if command == "OpenInitialize":
            if generation <= self.generation:
                raise ValueError("STALE_GENERATION")
        elif generation != self.generation:
            raise ValueError("STALE_GENERATION")
        payload = request.get("payload", {})
        if not isinstance(payload, dict):
            raise ValueError("INVALID_PAYLOAD")
        result = {"outcome": "confirmed"}
        if command == "Probe":
            result["devices"] = [identity(d) for d in self.finder()]
        elif command == "OpenInitialize":
            selected = payload.get("identity", {})
            matches = [d for d in self.finder() if identity(d)["id"] == selected.get("id")]
            if len(matches) != 1:
                raise ValueError("DEVICE_NOT_UNIQUE")
            self.close()
            self.generation = generation
            self.device = matches[0]
            try:
                self.device.connect()
                infos = self.device.initialize() or []
                if getattr(self.device, "_flowdeck_latest_status", False):
                    self.lighting_reader = LightingStatusReader(self.device)
                self.leds = []
                self.firmware = None
                grouped = {}
                for name, value, _unit in infos:
                    if name == "Firmware version":
                        self.firmware = str(value)
                    match = re.fullmatch(r"LED (\d+) accessory \d+", name)
                    if match:
                        grouped.setdefault(f"led{match[1]}", []).append(describe_accessory(value))
                self.leds = [{"id": key, "accessory": ", ".join(name for name, _ in accessories),
                              "ledCount": sum(count for _, count in accessories) if all(count is not None for _, count in accessories) else None}
                             for key, accessories in sorted(grouped.items())]
                result["status"] = self.status()
                self.fans = [c["id"] for c in result["status"]["channels"] if c["controlMode"] in ("DC", "PWM")]
            except Exception:
                self.close()
                raise
        elif command == "ReadStatus":
            # Monitoring may drain the report just returned by a confirmed fan
            # write for at most 100 ms. A long fan transaction can pause frames
            # beyond last_frame's lifetime; it need not add another 500 ms read.
            # Neither monitoring nor queue drains extend this post-fan window.
            # Fan prechecks/readback and non-frame lighting still use status().
            reader = self.lighting_reader
            now = time.monotonic() if reader is not None else 0
            streaming = reader is not None and 0 <= now - reader.last_frame <= 0.750
            reuse = reader is not None and reader.device is self.device and (
                streaming or
                reader.sample is not None and 0 <= now - reader.confirmed_fan_sample_at <= 0.100)
            try:
                result["status"] = reader.read() if reuse else self.status()
                if reuse and not streaming and not 0 <= time.monotonic() - reader.confirmed_fan_sample_at <= 0.100:
                    # Even an empty receive can resume late. Recheck the window
                    # after draining, before returning a post-fan cached sample.
                    result["status"] = self.status()
            except LightingStatusDeferred:
                # Monitoring has no frame-skip result. Complete a fresh normal
                # read instead; never report a temporary scheduling delay as a
                # disconnect or pretend the cached telemetry was just acquired.
                result["status"] = self.status()
        elif command in ("SetFanGroup", "SetFanEndpoint"):
            channels, duty = payload.get("channels"), payload.get("duty")
            deadline_tick64 = None
            if type(duty) is not int or not 0 <= duty <= 100:
                raise ValueError("OUT_OF_RANGE")
            if command == "SetFanGroup" and (not isinstance(channels, list) or not channels or len(set(channels)) != len(channels) or set(channels) != set(self.fans)):
                raise ValueError("ALL_CONNECTED_FANS_REQUIRED")
            if command == "SetFanEndpoint":
                required = {"channel", "duty", "expectedHubId", "expectedWiringSignature", "expectedCurrentDuty"}
                if set(payload) not in (required, required | {"validUntilTick64"}):
                    raise ValueError("INVALID_ENDPOINT_PARAMETERS")
                if "validUntilTick64" in payload:
                    deadline_tick64 = payload["validUntilTick64"]
                    check_fan_deadline(deadline_tick64)
            before = self.status()
            if set(c["id"] for c in before["channels"] if c["controlMode"] in ("DC", "PWM")) != set(self.fans):
                raise ValueError("WIRING_CHANGED")
            if command == "SetFanEndpoint":
                channel = payload.get("channel")
                if type(channel) is not str or channel not in self.fans:
                    raise ValueError("INVALID_FAN_ENDPOINT")
                if payload.get("expectedHubId") != identity(self.device)["id"]:
                    raise ValueError("DEVICE_CHANGED")
                if payload.get("expectedWiringSignature") != wiring_signature(before):
                    raise ValueError("WIRING_CHANGED")
                expected_duty = payload.get("expectedCurrentDuty")
                current = next(c for c in before["channels"] if c["id"] == channel)
                if type(expected_duty) is not int or not 0 <= expected_duty <= 100 or current["duty"] != expected_duty:
                    raise ValueError("EXTERNAL_DUTY_CHANGE")
                channels = [channel]
            rows, lost = [], False
            for channel in channels:
                row = {"id": channel, "write": "notExecuted", "reportedDuty": None, "confirmation": "unknown", "error": None}
                if not lost:
                    # Status I/O above may consume the remaining freshness budget. Never refresh this deadline.
                    if deadline_tick64 is not None:
                        check_fan_deadline(deadline_tick64)
                    try:
                        self.device.set_fixed_speed(channel, duty)
                        row["write"] = "completed"
                    except OSError as error:
                        row.update(write="unknown", error=type(error).__name__)
                        lost = True
                    except Exception as error:
                        row.update(write="failed", error=type(error).__name__)
                        logging.exception("Fan write failed: %s", channel)
                rows.append(row)
            latest = None
            if not lost:
                deadline = time.monotonic() + 3
                while True:
                    try:
                        latest = self.status()
                    except Exception:
                        logging.exception("Fan readback failed")
                        break
                    readings = {c["id"]: c["duty"] for c in latest["channels"]}
                    for row in rows:
                        row["reportedDuty"] = readings.get(row["id"])
                        row["confirmation"] = "confirmed" if readings.get(row["id"]) == duty and row["write"] == "completed" else "mismatch"
                    if all(r["confirmation"] == "confirmed" for r in rows) or time.monotonic() >= deadline:
                        break
                    time.sleep(0.05)
            outcome = "confirmed" if all(r["confirmation"] == "confirmed" for r in rows) else "unknown" if latest is None else "partial"
            reader = self.lighting_reader
            if outcome == "confirmed" and reader is not None and reader.device is self.device and reader.sample is latest:
                reader.confirmed_fan_sample_at = reader.sampled_at
            result.update(outcome=outcome, status=latest, channels=rows, error=None if outcome == "confirmed" else "DUTY_MISMATCH" if latest else "HUB_DISCONNECTED")
        elif command in ("SetLighting", "SetLightingEffect", "SetLightingEndpoints"):
            if command == "SetLightingEndpoints":
                fields = {"channels", "mode", "colors", "speed", "reverse", "expectedHubId", "expectedWiringSignature"}
                if set(payload) not in (fields, fields | {"validUntilTick64"}):
                    raise ValueError("INVALID_LIGHTING_ENDPOINT_PARAMETERS")
                if any(type(payload[key]) is not str or not payload[key].strip() or len(payload[key]) > limit
                       for key, limit in (("expectedHubId", 2048), ("expectedWiringSignature", 4096))):
                    raise ValueError("INVALID_LIGHTING_EXPECTATION")
                if "validUntilTick64" in payload:
                    check_lighting_deadline(payload["validUntilTick64"])
            channels, preset = payload.get("channels"), payload.get("preset")
            known = {c["id"] for c in self.leds}
            if not isinstance(channels, list) or not channels or any(type(c) is not str for c in channels) or len(set(channels)) != len(channels):
                raise ValueError("INVALID_LIGHTING_CHANNELS")
            if command == "SetLightingEndpoints" and (len(channels) > 5 or any(c not in ("led1", "led2", "led3", "led4", "led5") for c in channels)):
                raise ValueError("INVALID_LIGHTING_CHANNELS")
            if command == "SetLighting":
                if set(channels) != known:
                    raise ValueError("ALL_CONNECTED_LIGHTING_REQUIRED")
                if preset not in (*COLORS, "off"):
                    raise ValueError("OUT_OF_RANGE")
                mode, colors, speed, direction = ("off", [], "normal", "forward") if preset == "off" else ("fixed", [COLORS[preset]], "normal", "forward")
            else:
                if not set(channels).issubset(known):
                    raise ValueError("UNCONFIGURED_LIGHTING_CHANNEL")
                mode, colors = payload.get("mode"), payload.get("colors")
                speed_value, reverse = payload.get("speed"), payload.get("reverse", False)
                ranges = {"fixed": (1, 1), "breathing": (1, 1), "fading": (1, 8),
                          "covering-marquee": (1, 8), "off": (0, 0), "spectrum-wave": (0, 0), "super-rainbow": (0, 0)}
                if type(mode) is not str or mode not in ranges or type(colors) is not list or not ranges[mode][0] <= len(colors) <= ranges[mode][1]:
                    raise ValueError("INVALID_LIGHTING_EFFECT")
                if any(type(c) is not list or len(c) != 3 or any(type(v) is not int or not 0 <= v <= 255 for v in c) for c in colors):
                    raise ValueError("INVALID_COLOR")
                if type(speed_value) is not int or not 0 <= speed_value <= 4 or type(reverse) is not bool:
                    raise ValueError("INVALID_EFFECT_SPEED")
                speed = ("slowest", "slower", "normal", "faster", "fastest")[speed_value]
                direction = "backward" if reverse else "forward"
                if mode == "breathing":
                    mode, colors = "fading", [colors[0], [0, 0, 0]]
            if command == "SetLightingEndpoints":
                result = self.guarded_lighting(channels, mode, colors, speed, direction, payload)
                return {"v": 1, "type": "response", "id": request["id"], "workerSessionId": self.session,
                        "deviceGeneration": self.generation, **result}
            rows, lost = [], False
            for channel in channels:
                row = {"id": channel, "write": "notExecuted", "reportedDuty": None, "confirmation": "unknown", "error": None}
                if not lost:
                    try:
                        if command == "SetLighting":
                            self.device.set_color(channel, mode, colors)
                        else:
                            self.device.set_color(channel, mode, colors, speed=speed, direction=direction)
                        row.update(write="completed", confirmation="writtenUnverified")
                    except OSError as error:
                        row.update(write="unknown", error=type(error).__name__)
                        lost = True
                    except Exception as error:
                        row.update(write="failed", error=type(error).__name__)
                        logging.exception("Lighting write failed: %s", channel)
                rows.append(row)
            outcome = "writtenUnverified" if all(r["write"] == "completed" for r in rows) else "partial" if any(r["write"] == "completed" for r in rows) else "unknown" if lost else "failed"
            result.update(outcome=outcome, channels=rows, error=None if outcome == "writtenUnverified" else "LIGHTING_WRITE_FAILED")
        elif command in ("Close", "Shutdown"):
            self.close()
        else:
            raise ValueError("UNKNOWN_COMMAND")
        return {"v": 1, "type": "response", "id": request["id"], "workerSessionId": self.session,
                "deviceGeneration": self.generation, **result}


def read_frames(stream):
    """Bounded memory, CRLF and fragmented streams; discard an oversized line in full."""
    while True:
        line = stream.readline(MAX_FRAME + 1)
        if not line:
            return
        if len(line) > MAX_FRAME or not line.endswith(b"\n"):
            while line and not line.endswith(b"\n"):
                line = stream.readline(MAX_FRAME + 1)
            logging.warning("Discarded oversized or unterminated frame")
            continue
        try:
            yield json.loads(line.decode("utf-8"))
        except (ValueError, UnicodeError):
            logging.warning("Discarded invalid JSON frame")


def worker_capabilities():
    capabilities = ["Probe", "OpenInitialize", "ReadStatus", "SetFanGroup", "SetFanEndpoint", "SetLighting", "SetLightingEffect", "SetLightingEndpoints", "Close", "Shutdown"]
    if sys.platform == "win32":
        capabilities.extend(("FanDeadlineTick64", "LightingDeadlineTick64", "LightingLatestStatusV1"))
    return capabilities


def main():
    logging.basicConfig(stream=sys.stderr, level=logging.INFO)
    version = importlib.metadata.version("liquidctl")
    if version != REQUIRED_LIQUIDCTL:
        raise RuntimeError(f"liquidctl {REQUIRED_LIQUIDCTL} required, installed {version}")
    worker = Worker()

    def send(message):
        data = (json.dumps(message, separators=(",", ":"), ensure_ascii=True, allow_nan=False) + "\n").encode()
        if len(data) > MAX_FRAME:
            raise ValueError("FRAME_TOO_LARGE")
        sys.stdout.buffer.write(data)
        sys.stdout.buffer.flush()

    send({"v": 1, "type": "ready", "workerSessionId": worker.session, "liquidctlVersion": version,
          "pythonVersion": sys.version.split()[0], "capabilities": worker_capabilities()})
    try:
        for request in read_frames(sys.stdin.buffer):
            try:
                response = worker.handle(request)
            except Exception as error:
                logging.exception("Request failed")
                response = {"v": 1, "type": "response", "id": request.get("id", "") if isinstance(request, dict) else "",
                            "workerSessionId": worker.session, "deviceGeneration": worker.generation,
                            "outcome": "rejected" if isinstance(error, ValueError) else "unknown", "error": str(error)[:300]}
            send(response)
            if isinstance(request, dict) and request.get("command") == "Shutdown" and response.get("outcome") == "confirmed":
                break
    finally:
        worker.close()


if __name__ == "__main__":
    main()
