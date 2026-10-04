import json
import sys
import time

session = "fake-session"
generation = 0
endpoint_support = globals().get("ENDPOINT_SUPPORT", False)
deadline_support = globals().get("DEADLINE_SUPPORT", False)
if deadline_support:
    import pathlib
    import runpy
    deadline_check = runpy.run_path(str(pathlib.Path(__file__).resolve().parents[2] / "worker" / "hub_worker.py"))["check_fan_deadline"]
ready = {"v":1,"type":"ready","workerSessionId":session,"pythonVersion":"3.13.12","liquidctlVersion":"1.16.0"}
if endpoint_support: ready["capabilities"] = ["SetFanEndpoint"]
if deadline_support: ready["capabilities"].append("FanDeadlineTick64")
print(json.dumps(ready),flush=True)
for line in sys.stdin:
    r=json.loads(line)
    command=r["command"]
    result={"v":1,"type":"response","id":r["id"],"workerSessionId":session,"deviceGeneration":generation,"outcome":"confirmed"}
    if command=="Probe": result["devices"]=[{"id":"fake","description":"fake","serial":"fake","path":"fake"}]
    elif command=="OpenInitialize":
        generation=r["deviceGeneration"]
        result["deviceGeneration"]=generation
        result["status"]={"channels":[],"lighting":[]}
    elif command=="SetFanGroup":
        duty=r["payload"]["duty"]
        if duty==71: time.sleep(20)
        elif duty==72: sys.exit(7)
        elif duty==73: result["deviceGeneration"]=generation-1
        elif duty==74:
            print("log"*30000,file=sys.stderr,flush=True)
            old=dict(result,workerSessionId="old-session")
            print(json.dumps(old),flush=True)
    elif command=="ReadStatus": result["status"]={"channels":[],"lighting":[]}
    elif command=="SetFanEndpoint" and endpoint_support:
        payload = dict(r["payload"])
        if deadline_support and "validUntilTick64" in payload:
            try:
                deadline_check(payload.pop("validUntilTick64"))
            except ValueError as error:
                result.update(outcome="rejected", error=str(error))
        if payload != {"channel":"fan4","duty":63,"expectedHubId":"fake",
                            "expectedWiringSignature":"fan4:PWM|led1:RGB","expectedCurrentDuty":45}:
            result.update(outcome="rejected",error="INVALID_ENDPOINT_PAYLOAD")
        elif result["outcome"] != "rejected":
            result["status"] = {"channels":[{"id":"fan4","presence":"present","duty":63,"rpm":1000,"controlMode":"PWM"}],"lighting":[]}
            result["channels"] = [{"id":"fan4","write":"completed","reportedDuty":63,"confirmation":"confirmed"}]
    print(json.dumps(result),flush=True)
