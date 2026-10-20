# 0015. CI growth: the coverage gate, Ubuntu containers and nightly fuzzing

- Status: Accepted incrementally for M1.16 (16a landed first; 16b and 16c extend it)
- Date: 2026-10-20
- Requirements: KASI-NFR-018; later steps KASI-ENG-002, KASI-ENG-003, KASI-ENG-011
- Related: ADR 0013, TPL 4, TPL 7.2

## Context

M1 closes with the engine gate (plan.md 4.2): coverage of at least 80%, conformance against real FTP and proxy servers, fuzzing of
the filename parser, and three nights of fault injection. CI so far builds, runs the unit and integration tests on Windows x64
and ARM64, and publishes a coverage report without enforcing anything.

## Decision (16a)

**Measure first, then gate.** `Kasi.Engine` line coverage was measured with every test running (unit and integration, Release,
Coverlet through ReportGenerator, the same assembly filters as CI): **86.5%**. That is above 80%, so the gate is switched on
directly, without a ratchet or a test-writing step.

**Read the report, not the summary.** `build/Test-CoverageGate.ps1` reads the `line-rate` of the `Kasi.Engine` package from the
merged Cobertura file that ReportGenerator already writes. A regular expression over the Markdown summary would break with any
formatting change.

**A gate that cannot run must not pass.** The script exits 0 at or above the threshold, 1 below it, and 2 when the file or the
assembly is missing. The `ci.yml` step has no `hashFiles` guard: if coverage collection breaks and the report never appears, the
job fails instead of the gate quietly disappearing.

**The threshold is an input** (`-Threshold`, default 0.80) so the script's tests can show the verdict flip: they run it on canned
Cobertura files at, above and below the threshold, and with a missing file or assembly.

## Consequences

- A pull request that lowers `Kasi.Engine` below 80% fails CI, and the figure is in the job summary.
- The margin is about 6.5 points; large new engine code needs tests with it.
- Only the Windows x64 job gates. The Ubuntu leg of 16b adds its own coverage later, and the gate stays on the Windows figure.
