---
name: map-signals
description: Advance the CAN signal map by one verified increment. Reads the persistent signal-map state, picks the highest-value action that can be done without the vehicle, does it, verifies it with tests, and updates state. Queues anything needing hardware for the next in-car session. Use when the user runs /map-signals, or asks to continue, resume, or make progress on CAN signal mapping / decoding / reverse-engineering.
---

# Signal mapping loop

One invocation = one meaningful increment of the signal map, then a state update.

The state file is the memory between runs. **Read it first, write it last, every time.**

- State: `.local/signal-map/STATE.md`
- Car queue: `.local/signal-map/car-session-checklist.md`
- Background/rationale: `.local/CAN_TOOLING_PLAN.md`
- Hardware errata: `docs/FRAME_LAYOUT_AUDIT.md`

If `.local/signal-map/STATE.md` does not exist, create it from the template in §6 by
surveying the repo, then stop and report. That bootstrap *is* a full run.

## 1. The split that makes this loop work

Two kinds of work. Never confuse them.

**Offline** — no vehicle needed. Fully actionable now:
- decode captures already in `.local/signal-map/captures/` or `LeafGoldenData`
- fix `[CanSignal]` layouts against recorded bytes
- generator work (big-endian support, DBC import)
- write/repair decode unit tests
- score decoders against `EV-can_AZE0.dbc` / `CAR-can_AZE0.dbc` / `QC-CAN_ALL.dbc`

**Needs the car** — cannot be done here. Only *queued*:
- capturing a bus that has never been captured
- anything requiring a stimulus (turn signal, brake, gear, charge plug)
- confirming a physical pinout

**A run must never stall on car-blocked work.** If the top item needs the car, append it to
the car checklist and move to the next offline item. Only report "nothing to do" when the
offline backlog is genuinely empty.

## 2. Run procedure

1. **Read state.** `.local/signal-map/STATE.md`. Note the run number.
2. **Ingest new evidence.** Any capture directory under `.local/signal-map/captures/` not yet
   listed in state: parse its `summary.json`, fold every CAN ID into the Unknown/Candidate
   tables, record the source. New evidence outranks backlog — fold it in before choosing work.
3. **Pick ONE item**, highest value first:
   1. A `Broken` entry with recorded raw bytes — highest value: known-wrong, fixable, testable.
   2. A blocker other items depend on (e.g. big-endian generator support gates most DBC import).
   3. An `Unknown ID` that the DBC can explain — cheap conversion to Candidate.
   4. A `Candidate` with capture bytes available to promote toward Confirmed.
   5. Backlog item.
4. **Do it.** Real edits, not a plan. Scope one item per run.
5. **Verify.** Any signal work needs a decode unit test with hand-computed bytes:
   ```bash
   dotnet run --project tests/ObdInsight.Tests -c Debug
   ```
   Generator changes also need:
   ```bash
   dotnet run --project tests/ObdInsight.SourceGeneration.Tests -c Debug
   ```
   `dotnet test` does not work here — the projects are exes. Never accept a Verify snapshot
   without reading the diff.
6. **Update state.** Increment run number, move rows between tables, record what changed and
   what the next run should pick up. Append anything car-blocked to the checklist.
7. **Report** in 5 lines or fewer: what moved, test result, what's next.

## 3. Promotion rules

Evidence standard per tier — do not promote without it.

| Tier | Requires |
|---|---|
| Unknown | ID observed on the wire, no decoder |
| Candidate | a decoder exists (DBC-derived or hand-written), no hardware confirmation |
| Confirmed | decodes real captured bytes to a physically plausible value **and** has a regression test pinning those exact bytes |
| Broken | decodes captured bytes to an implausible value |

"Plausible" means checked against known physical state at capture time (see the ground-truth
block at the top of `docs/FRAME_LAYOUT_AUDIT.md`). A value that merely parses is not confirmed.

Watch for the two failure modes that already bit this repo:
- **Counters and CRCs masquerading as signals.** A monotonically incrementing field is a
  message counter (0x284 bytes 6-7). Check `changedMask` in capture summaries — a byte that
  changes every single frame while the car is parked is almost never a physical signal.
- **Motorola start bits transcribed as Intel.** Most Leaf DBC signals are big-endian. Until
  the generator supports `ByteOrder`, every BE signal is hand-converted, and that conversion
  is the single largest source of wrong layouts here.

## 4. Working with capture files

`RawCaptureCommand` (DevTools → "Raw CAN capture (unfiltered)") writes per session:
- `capture.log` — time-ordered `<elapsed_ms> <F|E|M> <id> <payload>`; `M` rows are operator
  markers, `E` rows are bus events like `BUFFER FULL`
- `summary.json` — per-ID count, Hz, DLCs, first/last payload, `changedMask`, distinct count

Marker labels are the stimulus record. To correlate: take the frames in the window before a
marker and the window after, per ID, and diff. Bits set in `changedMask` are where to look;
bits clear in it can be ignored outright.

If `bufferFullCount > 0`, the adapter dropped frames — treat counts and rates as lower bounds
and do not conclude an ID is absent from a capture that overflowed.

## 5. Boundaries

- Do not invent signal definitions. Every entry traces to a DBC, a capture, or a documented
  community source, and state records which.
- Do not delete `Broken` rows to make the table look better. Fix or explain them.
- Do not commit unless asked.
- Do not widen scope mid-run. One item, verified, state updated.
- Keep `.local/` out of git (already ignored).

## 6. STATE.md template

```markdown
# Signal Map State

Run: 0 · Updated: <YYYY-MM-DD>

## Summary
Confirmed N · Candidate N · Broken N · Unknown N · Car-blocked N

## Confirmed
| Bus | CAN ID | Signal | Decoder | Evidence (bytes) | Test |
|---|---|---|---|---|---|

## Candidate
| Bus | CAN ID | Signal | Source | Why not confirmed |
|---|---|---|---|---|

## Broken
| Bus | CAN ID | Signal | Symptom | Raw bytes | Hypothesis |
|---|---|---|---|---|---|

## Unknown IDs seen on the wire
| Bus | CAN ID | Hz | DLC | changedMask | Capture |
|---|---|---|---|---|---|

## Offline backlog (ordered)
1. ...

## Car-blocked (mirrors car-session-checklist.md)
1. ...

## Run log
- Run 0 (<date>): bootstrapped from repo survey.
```
