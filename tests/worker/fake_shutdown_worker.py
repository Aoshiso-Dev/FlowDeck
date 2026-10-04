"""Protocol-only helper: imports no hardware libraries and opens no devices."""
import json
import os
import sys

session = "shutdown-test"
print(json.dumps({"v": 1, "type": "ready", "workerSessionId": session,
                  "pythonVersion": "3.13.12", "liquidctlVersion": "1.16.0"}), flush=True)
for line in sys.stdin:
    request = json.loads(line)
    print("diagnostic test", file=sys.stderr, flush=True)
    print(json.dumps({"v": 1, "type": "response", "id": request["id"], "workerSessionId": session,
                      "deviceGeneration": request["deviceGeneration"], "outcome": "confirmed",
                      "devices": [{"id": "fake", "description": "fake", "serial": str(os.getpid()), "path": "fake"}]}), flush=True)
