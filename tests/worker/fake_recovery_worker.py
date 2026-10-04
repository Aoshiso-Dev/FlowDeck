"""Protocol fixture only. State lives in a test directory; no hardware imports or I/O."""
import json
import os
from pathlib import Path
import sys
import time

root = Path(__file__).parent
state_path = root / "state.json"
session = str(os.getpid())
opened = False
print(json.dumps({"v": 1, "type": "ready", "workerSessionId": session,
                  "pythonVersion": "3.13.12", "liquidctlVersion": "1.16.0",
                  "capabilities": ["SetFanEndpoint", "FanDeadlineTick64"]}), flush=True)
for line in sys.stdin:
    request = json.loads(line)
    command = request["command"]
    with (root / "commands.jsonl").open("a", encoding="utf-8") as log:
        log.write(json.dumps({"pid": os.getpid(), "command": command}) + "\n")
    state = json.loads(state_path.read_text(encoding="utf-8"))
    response = {"v": 1, "type": "response", "id": request["id"], "workerSessionId": session,
                "deviceGeneration": request["deviceGeneration"], "outcome": "confirmed"}
    if command == "Probe":
        response["devices"] = [state["identity"]]
    elif command == "OpenInitialize":
        assert request["payload"]["identity"] == state["identity"]
        opened = True
        response["status"] = state["status"]
    elif not opened:
        response.update(outcome="unknown", error="HUB_DISCONNECTED")
    elif command == "ReadStatus":
        if (root / "block-read").exists():
            (root / "block-read").unlink()
            print("read-blocked", file=sys.stderr, flush=True)
            time.sleep(60)  # The test cancels this read and verifies process termination.
        response["status"] = state["status"]
    elif command == "SetFanEndpoint":
        payload = request["payload"]
        channel = next(c for c in state["status"]["channels"] if c["id"] == payload["channel"])
        assert payload["expectedHubId"] == state["identity"]["id"]
        assert payload["expectedWiringSignature"] == state["wiring"]
        assert channel["duty"] == payload["expectedCurrentDuty"]
        channel["duty"] = payload["duty"]
        state["writes"].append({"channel": channel["id"], "duty": channel["duty"]})
        state_path.write_text(json.dumps(state), encoding="utf-8")
        response.update(status=state["status"], channels=[{"id": channel["id"], "write": "completed",
                        "reportedDuty": channel["duty"], "confirmation": "confirmed"}])
    else:
        raise RuntimeError("Unexpected fixture command: " + command)
    print(json.dumps(response), flush=True)
