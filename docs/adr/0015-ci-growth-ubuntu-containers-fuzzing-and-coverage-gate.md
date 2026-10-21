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

## Decision (16c)

**Fuzzing runs on Windows.** libFuzzer works natively on Windows, the platform Kasi ships on, and `libfuzzer-dotnet` has a Windows
build, so the nightly fuzz job runs on `windows-latest` (the 16c plan assumed Ubuntu), and the same script runs on a developer
machine. SharpFuzz 2.3.0 (MIT) is referenced by the new `tests/Kasi.Fuzz` only; the `sharpfuzz` instrumentation tool is a local
dotnet tool (`dotnet-tools.json`); `libfuzzer-dotnet` (MIT, release v2025.05.02.0904) is downloaded by the workflow and checked by
SHA-256.

**One invariant check.** `FileNameFuzz` (in `tests/Kasi.Fuzz`) holds the rules of KASI-ENG-003: a name of 1 to 200 characters,
well-formed UTF-16, no control, reserved or bidirectional character, no leading or trailing space, no trailing dot, a bare name that
stays in its folder. The fuzz harness, the unit tests and the saved crash corpus all call it, replacing the copy that lived in a
test class and could not be reached from an executable.

**Budget and limits.** 3 minutes, as TPL 7.2 gives this target; a hang over 2 seconds ends a run (`-timeout=2`), and so does a
process over 512 MB resident (`-rss_limit_mb=512`; the TPL names 256 MB of allocation, but the .NET runtime alone is a large part
of that, so 512 MB is the practical RSS limit). A finding is minimised, uploaded and fails the run; the owner adds the minimised
file to `tests/fixtures/fuzz/crashes` with the fix and files a High defect, and every build replays that folder as a unit test.

**Verified by a planted bug.** An index-out-of-range injected into the sanitiser was found and minimised to a 20-byte input in
under a minute; the injection was removed.

**The gate check judges evidence, not a run.** `build/Check-M1Gate.ps1` separates the judgement (a pure function over an evidence
object) from the collection (git, the GitHub CLI, files), so its logic is tested on canned evidence. It treats a missing piece as
open, never as passed: fewer than three scheduled nights, a night with a `SKIPPED` suite, no benchmark session, no coverage report,
and a Hyper-V power cut nobody has recorded in `docs/m1-gate-evidence.json` all keep the gate open.

## Consequences

- A pull request that lowers `Kasi.Engine` below 80% fails CI, and the figure is in the job summary.
- The margin is about 6.5 points; large new engine code needs tests with it.
- Only the Windows x64 job gates. The Ubuntu leg of 16b adds its own coverage later, and the gate stays on the Windows figure.
