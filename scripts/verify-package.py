"""Verify a relocated package, using its embedded Python and its production worker.

Probe enumerates the selected VID/PID family only. No initialization or output write.
"""
import json
import pathlib
import subprocess
import sys

package = pathlib.Path(sys.argv[1]).resolve()
python = package / "runtime" / "python" / "python.exe"
worker = package / "worker" / "hub_worker.py"

with subprocess.Popen([str(python), "-u", str(worker)], cwd=package,
                      stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                      text=True, encoding="utf-8") as process:
    try:
        ready = json.loads(process.stdout.readline())
        assert ready["pythonVersion"].startswith("3.13."), ready
        assert ready["liquidctlVersion"] == "1.16.0", ready
        request = {"v": 1, "type": "request", "id": "package-probe",
                   "workerSessionId": ready["workerSessionId"], "deviceGeneration": 0,
                   "command": "Probe", "payload": {}}
        process.stdin.write(json.dumps(request) + "\n")
        process.stdin.flush()
        result = json.loads(process.stdout.readline())
        assert result["outcome"] == "confirmed", result
        print(json.dumps({"package": str(package), "ready": ready, "probe": result}, indent=2))
    finally:
        process.stdin.close()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
        errors = process.stderr.read()
        if errors:
            print(errors, file=sys.stderr)
