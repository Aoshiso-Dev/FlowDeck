# FlowDeck local build of LibreHardwareMonitor

Upstream: LibreHardwareMonitor 0.9.6, commit `3d331e3370efb858411f19511373eff65a218701` (the commit recorded in the NuGet package).
Source: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/3d331e3370efb858411f19511373eff65a218701
Archive SHA-256: `5A83EE3F504A85EFB6AFEE4112447E60CACA1B7EC2E2D71F4651570B8B9B4230`.
`UPSTREAM.json` records original file hashes before changes. The MPL-2.0 license and upstream third-party notices are included.

FlowDeck patch 1:

- Build from these sources for .NET 10 / Windows x64, with assembly metadata `FlowDeck.LhmPatch=1` and informational version `0.9.6-flowdeck.1`. NuGet lock files pin the build dependencies. No installed driver or PawnIO module is changed.
- `Nct677X.Update` releases its recursive ISA mutex in `finally`. Clear its temperature cache at the start of an acquired update, so an invalid register cannot retain an earlier sample.
- `Sensor` exposes `ISensorUpdateEvidence.UpdateSequence`, incremented on a value assignment even if history is disabled. This is assignment evidence, not by itself proof of a physical measurement. FlowDeck still verifies the supported SuperIO acquisition path.
- `Amd17Cpu.Processor.UpdateSensors` releases the PCI mutex and restores thread affinity in `finally`. Missing/invalid known CCD temperatures invalidate the derived maximum and average instead of reusing an older CCD value.
- The complete modified library sources, build files, resources, license and provenance are copied into `third-party/LibreHardwareMonitor` in build and publish output. Recipients can rebuild the library from this directory with `dotnet build LibreHardwareMonitorLib.csproj -c Release`.
- Five upstream files were converted from Windows-1252 to UTF-8 without changing their text: `Hardware/Sensor.cs`, `Hardware/Controller/TBalancer/TBalancerGroup.cs`, `Hardware/Cpu/CpuGroup.cs`, `Hardware/Gpu/AmdGpuGroup.cs`, `Hardware/Motherboard/Lpc/LMSensors.cs`.

This patch does not certify additional hardware models, all LHM backends, or real-device behavior. Hardware read/write tests are separate from the simulated fault tests.

FlowDeck patch 2 (includes patch 1):

- `Mutexes.WaitMutex` returns false when its mutex was not initialized or could not be opened. Missing shared-bus ownership must not let the callers access PCI, ISA, EC, or shared USB resources. Actual timeouts and disposed handles still fail; successful recursive acquisition and Windows abandoned-mutex ownership keep their existing balanced-release behavior.
- Assembly metadata is now `FlowDeck.LhmPatch=2`, with informational version `0.9.6-flowdeck.2`. FlowDeck acquisition evidence and publication verification require this patch level. The upstream assembly version remains `0.9.6.0`.
- Tests use unnamed mutexes and deliberately uninitialized CPU/SuperIO transport objects. They verify skipped hardware paths and unavailable readings without opening real drivers or hardware. This correction does not certify coexistence with OpenRGB or another external controller.

FlowDeck patch 3 (includes patches 1–2):

- AMD GPU shared PM logging no longer treats every copy of a frozen log as a new measurement. `AmdPmLogSnapshot` compares two memory copies, validates the header and sensor entries, requires an advancing driver stamp after attachment/discontinuity, and retains one sample identity for repeated copies. Timestamp units/timezone are not guessed from the undocumented driver stamp. After an observation gap of 3 seconds the reader requires new progress again.
- `Sensor.SourceSample` preserves the identity of such a validated sample alongside its assignment counter. Ordinary direct assignments clear this optional identity. FlowDeck records the first observation time and retains it across repeated copies, including retry after a partially failed update. The existing common-runtime 3-second age limit then marks a frozen reading stale.
- The compiled AMD log-to-sensor path does not use the alternate query to override a supported but unvalidated shared logger. A successful direct OD5/ODN reading from the current update remains usable, and is not overwritten by a repeated shared-log sample. Sensors unsupported by shared logging retain their query fallback. Zero fan RPM remains a value; absent entries do not gain new assignment evidence.
- Assembly metadata is `FlowDeck.LhmPatch=3`, informational version `0.9.6-flowdeck.3`; FlowDeck and publication verification require this patch. The assembly version and all embedded PawnIO modules remain unchanged.
- Tests exercise copied in-process memory, the compiled AMD parser, acquisition timestamps, and the common telemetry runtime without loading ADL or opening a GPU. Two equal memory copies and observed driver progress do not certify the physical sampling behavior of every driver, nor complete the remaining CPU/GPU backend audit.
- ADL structure reference: https://gpuopen-librariesandsdks.github.io/adl/structADLPMLogData.html (`ulLastUpdated`, `ulActiveSampleRate`, `ulValues`).

FlowDeck patch 4 (includes patches 1–3):

- NVIDIA standard thermal readings are bounded by the returned count and matched to their original target/controller pair. Reordering does not change the sensor's meaning. Missing or ambiguous identities, malformed replies and API failure produce unavailable values rather than an unused array entry or another sensor's reading.
- The requested thermal structure version is V2; its three temperature fields now use signed 32-bit integers as specified by NVIDIA, with the same 20-byte sensor layout. Unusable temperatures are not published.
- Additional RTX 50 core temperature is assigned to the unique GPU target rather than array element zero. When the standard list initially has no GPU target, a separate core sensor is available for the existing additional-temperature path. Invalid extra data does not erase a valid standard core reading. Missing hotspot data is null, not a fabricated zero.
- The existing RTX 50/40/earlier additional layouts use the original hardware name, so a user rename cannot change the interpretation of the array. This preserves upstream layouts; it does not independently certify undocumented layout semantics on new hardware.
- NVIDIA fan RPM is matched by CoolerId, with count, duplicate and identifier bounds. A failed identified cooler read is not replaced with an unrelated legacy tachometer value. Devices initially using the legacy tachometer keep that source. Valid 0 RPM remains a measurement.
- Assembly metadata is `FlowDeck.LhmPatch=4`, informational version `0.9.6-flowdeck.4`. FlowDeck and publication verification require this patch. Assembly version and embedded PawnIO modules are unchanged.
- Simulated tests call the compiled reply-to-sensor methods without initializing NVAPI or opening a GPU. Real-driver acceptance and the remaining CPU/GPU backend audit are separate.
- Official thermal V2 structure: https://docs.nvidia.com/nvapi/structNV__GPU__THERMAL__SETTINGS__V2.html ; thermal API: https://docs.nvidia.com/nvapi/group__gputhermal.html .

FlowDeck patch 5 (includes patches 1–4):

- `PawnIo.ExecuteChecked` checks HRESULT and exact response length. `ExecuteHr` reports E_HANDLE for a missing/closed module (including zero-output commands), rejects malformed byte counts, and never turns a failed native call with an unusable error code into success. Existing `Execute` callers keep their prior behavior; the stricter method is used by Ryzen SMU.
- Ryzen SMU uses checked transport for all of its commands. Its combined `UpdateAndReadPmTable` holds one PCI mutex acquisition through refresh and copy and releases it in `finally`. A failed update does not proceed to reading an earlier table. The internal delegate constructor allows deterministic in-memory fault tests; the production constructor still loads only its existing embedded module.
- `Hardware.RyzenSMU` uses the combined transaction, keeps its two-attempt bound, and returns no table after failure or an invalid leading value. It does not expose a partially valid tail or a fabricated zero element. Closing an unsupported/uninitialized SMU is safe.
- Intel CPU derived maximum/average require every known core to have a current usable temperature. A missing core no longer lowers the average through division by the full core count; stale derived values are cleared until complete readings return. Valid individual core readings are retained.
- Assembly metadata is `FlowDeck.LhmPatch=5`, informational version `0.9.6-flowdeck.5`; FlowDeck and publication verification require it. Assembly version and all embedded PawnIO binaries remain unchanged.
- Tests use absent/disposed handles, an injected SMU executor and unnamed mutexes, and the compiled Intel aggregation method. They do not open a CPU/GPU or install a driver. Actual firmware sampling, other PawnIO callers, CPU/GPU paths and coexistence remain separate acceptance work.

FlowDeck patch 6 (includes patches 1–5):

- ISA-bus ownership in F718XX, IT87XX and W836XX Update, SetControl and GetReport, plus Nct677X SetControl and GetReport, is now released in finally, including exceptions on the LPC transport path. Each method releases only the lease it acquired; recursive callers retain their outer lease.
- LPC discovery releases its ISA lease before optional BMC work even when detection fails. A failed constructor attempts to close every previously discovered Super I/O object, preserves the original failure, and does not leak an unadopted port. Slot-selection failure closes the newly created module handle immediately. PawnIo.Close disposes its SafeFileHandle wrapper even when the handle is invalid or was already closed.
- Small internal constructor seams separate discovery/module construction from transport access. Regression tests execute the compiled logic using absent or invalid transports and unnamed mutexes; they do not load a module, open hardware, or write registers.
- Assembly metadata is FlowDeck.LhmPatch=6, informational version 0.9.6-flowdeck.6. FlowDeck acquisition and publication verification require patch 6. Assembly version, source pin and all embedded PawnIO module bytes remain unchanged.
- This does not enable additional fan-writing definitions, certify freshness of previously unsupported Super I/O families, or establish coexistence with external OpenRGB. Resource-profile auditing and physical acceptance remain separate.

FlowDeck patch 7 (includes patches 1–6):

- All LPC module calls now use ExecuteChecked, including slot selection, BAR discovery and writes. Failed, short, malformed or oversized replies cannot become zero-valued readings or successful writes. Byte/word reads reject values outside their unsigned range. Legacy Execute callers outside LPC/Ryzen SMU retain their existing behavior and require separate auditing.
- ExecuteHr copies only the declared input prefix, checks allocation arithmetic, and uses a small injectable native-call boundary for deterministic protocol tests. The native checked execution and module-load paths retain a SafeFileHandle reference for the whole system call.
- Module loading transfers handle ownership only after successful loading. Invalid/closed handles, failed loading and exceptions during resource reading/loading dispose the original wrapper. No module binary or native driver is changed.
- Interrupted LPC detection attempts its active configuration-exit sequence before closing the module, including partially entered sequences. Cleanup closes the module even if exiting fails. The existing secondary ITE port exception remains unchanged.
- A failed SuperIO discovery/wrapping path records a bounded component/type/HRESULT diagnostic, closes adopted resources, and returns a motherboard with only independently initialized children. EC initialization is independent. This prevents an LPC failure from escaping motherboard construction before Computer.Open can initialize CPU/GPU groups. Motherboard shutdown attempts every child and bus cleanup before reporting failures.
- Internal constructors/delegates support tests with managed byte buffers, inert non-owning handle markers, synthetic hardware and unnamed mutexes. They do not open drivers, call native I/O or probe hardware. The operating-system initialization sequence and actual hardware still require acceptance testing.
- Assembly metadata is FlowDeck.LhmPatch=7, informational version 0.9.6-flowdeck.7. FlowDeck and publication verification require patch 7. No additional fan-writing definitions, unsupported-family acquisition certification or external-process coexistence certification is introduced.

FlowDeck patch 8 (includes patches 1–7):

- Computer.Open tracks attempted shared-resource initialization and rolls back already adopted groups on failure. Failed group closures remain reachable for an explicit Close retry, are no longer exposed as active hardware, and prevent reopening until cleanup completes. Shared opcode/mutex resources remain available while a failed group may still need them for cleanup.
- Computer.Close attempts all adopted groups even if a removal listener or another group fails. Removal notifications cannot bypass group closure; a successful group closure is not repeated. Shared-resource closure failures are retained independently and retried without re-closing successful resources. Reset completes closure before reopening. Lifecycle calls are serialized and callback reentry is rejected.
- Dynamic group changes are refused while a previous group has not closed. Tests inject SMBIOS/shared-resource initialization and use compiled motherboard groups with synthetic child hardware; no OpCode, native module, named hardware mutex or device initialization is run by these tests.
- This change observes exceptions reported by each group. Native backend constructors, swallowed return codes/errors inside AMD/Intel/NVIDIA groups, and their per-resource retry behavior remain separate audit items. It does not certify that an opaque successful Close proves physical hardware release.
- Assembly metadata is FlowDeck.LhmPatch=8, informational version 0.9.6-flowdeck.8. FlowDeck and publication verification require patch 8; upstream identity, assembly version and embedded PawnIO modules are unchanged.
