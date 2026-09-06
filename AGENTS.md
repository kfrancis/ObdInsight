# ObdInsight

ObdInsight is an EV diagnostics library with a Windows console app in `src/ObdInsight`.
The MAUI project is a template shell, not the working application. Use the SDK selected
by `global.json` and the existing `.editorconfig`.

## Completing work

Carry the requested change through implementation, relevant verification, and fixes for
failures it causes. Local builds and deterministic tests may be run and repeated without
asking at each step. If runtime behavior is part of the request, exercise it with the
existing simulation/replay facilities where possible. Report what was verified and any
remaining hardware or environment gap.

Keep effort proportional to the request. Read the files and design notes relevant to the
change; a small edit does not require a repository survey, a new plan document, or the
full test suite. Prefer current code and tests over outdated progress notes. Update stale
guidance when the requested change depends on it.

Vehicle interaction requires a requested hardware session; an adapter address in the
environment is not authorization by itself. Queue hardware-dependent work and continue
independent offline work. Do not commit, push, or publish unless requested.

## Project constraints

- Core owns protocols, sessions, and vehicle capabilities; platform transports live in
  `Transports.*`, consumer telemetry in `Telemetry`, and replay fixtures in `Simulation`.
  Keep Core free of UI/platform dependencies and Console/Serilog output; use `ILogger`.
- Keep vehicle-specific behavior under `Vehicles/Implementations`. Shared communication
  code uses abstractions such as `IEcuWakeupStrategy`, not vehicle-name checks.
- Preserve ELM reader ownership and atomic context-plus-query exchanges. Interrupted I/O
  invalidates the session graph; do not redirect it onto a replacement connection or add
  blanket query retries.
- Preserve observation quality, acquisition time, freshness, and connection generation.
  Missing, invalid, stale, or unsupported measurements must not become valid zero values.
- Define CAN layouts with the existing attributes. `ByteOrder = CanByteOrder.Motorola`
  supports DBC `@0`; Intel is the default. Retain the annotation namespaces used by the
  generators. `MinValue`/`MaxValue` do not provide runtime validation.
- Trace signal definitions to DBCs, captures, or documented sources. Confirmation requires
  physical ground truth and a regression test for the captured bytes. An unchanged bit
  only means it did not change in that capture; absent frames do not prove lack of support.
- Keep `.local/` untracked. Avoid copying real VINs, adapter identifiers, or credentials
  from local captures/configuration into new committed fixtures or reports.

## Verification

Use the affected TUnit executable; these are the repository's established test commands:

```powershell
dotnet run --project tests/ObdInsight.Tests -c Debug
dotnet run --project tests/ObdInsight.SourceGeneration.Tests -c Debug
```

Decoder changes need production-parser tests with independently derived expected values.
Generator changes need the relevant generator tests and consumer decoding checks. Review
Verify snapshot diffs before accepting generated baselines. Evidence-only or documentation
changes need source/link consistency checks, not invented decoder tests.

Build affected projects directly. A whole-solution build includes MAUI workload requirements;
use `.github/workflows/ci.yml` for the broader non-MAUI validation scope when needed.
`tests/ObdInsight.IntegrationTests` requires a real vehicle and `LEAF_BLE_ADDRESS`; it is
compile-only in CI. Skipped hardware tests are not hardware verification.

## Read when relevant

- ELM exchanges, cancellation, or recovery: `docs/ELM_TRANSACTION_SAFETY.md` and
  `docs/RESILIENCE_DESIGN.md`.
- Monitoring or telemetry consumers: `docs/STREAMING_MONITOR_DESIGN.md`,
  `docs/TELEMETRY_SESSION_DESIGN.md`, and `docs/OBSERVATION_SEMANTICS.md`.
- Adapter changes: `docs/BLE_TRANSPORT_DESIGN.md` or `docs/CANABLE_SUPPORT.md`.
- Signal-mapping work: `.claude/skills/map-signals/SKILL.md` and the relevant captures.
  Reconcile `.local/signal-map/STATE.md` with newer evidence and current tests before
  choosing backlog work. Treat `docs/FRAME_LAYOUT_AUDIT.md` as dated hardware evidence.
  One increment is the default for an unqualified `/map-signals`; an explicit broader
  objective defines completion, including when state must first be bootstrapped.
