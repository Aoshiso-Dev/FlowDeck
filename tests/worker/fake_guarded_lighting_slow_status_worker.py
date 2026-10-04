"""Same production Worker with a memory-only Hub whose status report takes 260ms."""
import pathlib
import runpy

runpy.run_path(str(pathlib.Path(__file__).with_name("fake_guarded_lighting_worker.py")),
              init_globals={"STATUS_REPORT_DELAY_SECONDS": 0.26})
