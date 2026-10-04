# OpenRGB mock-hardware fixture sources

This directory supplies the complete C++ compilation inputs for the three
prebuilt test fixtures in `tests/FlowDeck.Tests/Fixtures`. The published
fixtures were rebuilt from these inputs with relative include paths so that
assertion strings do not disclose the original developer's directory. It does not include
or launch the OpenRGB application, device discovery, or a hardware driver.

| Directory | Test binary | OpenRGB base |
| --- | --- | --- |
| `authorization` | `AsrockAuthorizationClient.bin` | FlowDeck patch 5 |
| `sdk` | `AsrockSdkIntegration.bin` | FlowDeck patch 6 |
| `full-server` | `AsrockFullServerIntegration.bin` | FlowDeck patch 8 |

Upstream: [OpenRGB revision 81bbe18a84c2e507006f19dd252e397e40a56bfe](https://github.com/CalcProgrammer1/OpenRGB/tree/81bbe18a84c2e507006f19dd252e397e40a56bfe).
The files preserve upstream and FlowDeck modifications under GPL-2.0-or-later;
see `LICENSE-GPL-2.0.txt`, `UPSTREAM-COPYRIGHT.txt`, and individual file notices.
The shared `nlohmann/json.hpp` is JSON for Modern C++ 3.11.3, under its own MIT
license in `shared/nlohmann/LICENSE.MIT`; its embedded third-party notices are
preserved. These components are not relicensed by FlowDeck's root license.

The source inputs were copied from the original compilation directories only
after their executables matched the historical test binaries by SHA-256. Local
build logs and object files are excluded. Generated C++ stubs, extracted SDK
handlers, record definitions, and schemas are included as editable source;
their original preparation recipes are `scripts/build-openrgb-authorization-client.py`,
`scripts/build-openrgb-integration-fixture.py`, and
`scripts/build-openrgb-full-server-fixture.py` in the repository. The native
test harnesses and patch preparation scripts remain in `tests/native`,
`native/OpenRgb`, and `scripts`. Original provenance records identify the
upstream inputs and transformations; historical `artifacts/` paths in those
records are provenance labels, not build requirements for this bundle.

## Build

Install Visual Studio Build Tools with the C++ x64 tools and a Windows SDK.
Open an **x64 Native Tools Command Prompt for Visual Studio**, then run from
the repository root with Python 3.10 or later:

```powershell
python third-party/OpenRGB-Fixtures/build.py --verify-only
python third-party/OpenRGB-Fixtures/build.py artifacts/openrgb-fixtures-rebuilt
```

Use a fresh output directory. The script checks the bundled source manifest
and reference binaries, compiles all three fixtures, and records the results
in `build-verification.json`. It does not execute any fixture or replace the
existing `.bin` files. Qt, OpenRGB's full build environment, `.tools`, and the
original build directories are not required. The SDK and full-server fixtures
use the Windows Winsock and Kernel32 system libraries; the authorization
fixture uses Kernel32.

The original flags (`/std:c++17 /EHsc /W4 /utf-8 /permissive- /MT /O2` plus
the per-fixture defines and sources) are recorded in `manifest.json` and used
by `build.py`. A rebuild with a different MSVC/SDK version or PE timestamps
need not be byte-identical to the historical fixture. Successful compilation
does not prove hardware behavior. Do not replace the checked-in fixtures
without separately reviewing their provenance and running the relevant
mock-hardware tests.

Relative include paths are used even when the checkout path contains spaces or
non-ASCII characters. Build products are created in the requested output directory.

`manifest.json` records the published source paths and hashes, the original
build-directory correspondence, the historical hashes, and the current reference
binary hashes. It excludes
itself to avoid a self-referential hash.
