"""Deadline protocol peer, using the real worker's OS clock helper. No hardware library is imported."""
import pathlib
import runpy

runpy.run_path(str(pathlib.Path(__file__).with_name("fake_worker.py")),
              init_globals={"ENDPOINT_SUPPORT": True, "DEADLINE_SUPPORT": True})
