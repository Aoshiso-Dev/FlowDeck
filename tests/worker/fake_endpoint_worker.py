"""Opt-in endpoint protocol peer. Never imports a hardware library."""
import pathlib
import runpy

runpy.run_path(str(pathlib.Path(__file__).with_name("fake_worker.py")), init_globals={"ENDPOINT_SUPPORT": True})
