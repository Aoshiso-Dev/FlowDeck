"""Read-only enumeration through the production worker; never initializes or writes a Hub."""
import json
import pathlib
import subprocess
import sys

root = pathlib.Path(__file__).resolve().parents[1]
worker = subprocess.Popen([sys.executable, "-u", str(root / "worker" / "hub_worker.py")], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8")
try:
    ready = json.loads(worker.stdout.readline())
    request = {"v":1,"type":"request","id":"inventory","workerSessionId":ready["workerSessionId"],"deviceGeneration":0,"command":"Probe","payload":{}}
    worker.stdin.write(json.dumps(request) + "\n")
    worker.stdin.flush()
    result = json.loads(worker.stdout.readline())
    print(json.dumps({"ready":ready,"probe":result}, ensure_ascii=True, indent=2))
finally:
    worker.stdin.close()
    try: worker.wait(timeout=5)
    except subprocess.TimeoutExpired: worker.kill()
    stderr = worker.stderr.read()
    if stderr: print(stderr, file=sys.stderr)
