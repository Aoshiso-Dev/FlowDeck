import pathlib
import runpy

runpy.run_path(str(pathlib.Path(__file__).with_name("fake_guarded_lighting_worker.py")),
              init_globals={"WITHOUT_LIGHTING_DEADLINE": True})
