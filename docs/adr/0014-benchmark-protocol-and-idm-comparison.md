# 0014. Benchmark protocol and the IDM comparison

- Status: Accepted incrementally for M1.15 (15a landed first; 15b extends it)
- Date: 2026-10-20
- Requirements: KASI-NFR-001, KASI-NFR-002; engine-only early signals for KASI-NFR-004 and KASI-NFR-005
- Related: TPL 6, ADR 0010, ADR 0013

## Context

Kasi's headline claim is speed parity with IDM (KASI-NFR-001), plus 1 Gbit/s on 16 connections (KASI-NFR-002). Both need
numbers that can be repeated, compared week to week and trusted. There is no Kasi command line yet (`Kasi.App` is a shell),
and the public sources, the capped and range-less origins and IDM itself belong to the owner and the benchmark machine.

## Decision (15a)

**The Kasi side is `Kasi.Bench`.** `Kasi.Bench` keeps its BenchmarkDotNet micro-benchmarks and gains macro modes
(`throughput`, `local-1gbit`, `resources`, `compare`) that drive `DownloadEngine` directly. A download is timed from the call
that starts the job to the job being Completed (part file sealed and renamed). Hashing the result is done afterwards and is not
charged to throughput, but the hash must match.

**The server is a separate process.** `Kasi.TestServer` runs as its own process so that byte generation does not compete with
the engine for cores and the engine's memory and CPU can be sampled without the server in them.

**The 1 Gbit/s check has a control.** A raw `HttpClient` run with the same number of parallel range streams measures what this
machine and this server can do. The engine passes at 1000 Mbit/s or more; when the control itself is below that, the verdict is
*inconclusive*, not failed. A miss with a good control is a finding for the tuning sprint, not a build failure.

**Resource numbers are engine-only early signals.** NFR-004 and NFR-005 are about the whole app. The bench process's working set
(an upper bound for the private working set) and CPU are reported against the targets, and the real gate comes with the app.

**Sources are data, and placeholders are never fetched.** `bench/sources.json` holds one entry per TPL 6.2 archetype, marked
`placeholder` until the owner supplies the real source; a loader rejects URLs with a query string, a fragment or credentials
(CLAUDE.md rule 3) and repeated ids. A `local` section lists TestServer routes so the harness runs end to end without the
owner's servers. A signed cloud URL carries a query string, so it is supplied at run time and never stored.

**Results are sessions.** `bench/results/*.json` (schema version 1) records the machine, the line speed, the time-of-day
window, whether the DNS cache was flushed, and one row per timed download. `compare` takes the median per source and
connection count and flags a throughput drop of more than 5% or a memory increase of more than 10% against the previous
session (TPL 6.4); failed-hash rows are ignored.

**Benchmarks are not tests and not PR CI.** They need a quiet machine and, for the comparison, the public internet. Only the
unit tests and a small local smoke run in CI, and the smoke checks the pipeline and the SHA-256, not the number.

**The tool measures itself.** The first sampler read the process through `Process.Refresh` and `WorkingSet64`, which enumerate
every process on the machine, and cost a third of a core on its own. It reads `Environment.WorkingSet` and the process handle
now, and the `baseline` scenario (sampler alone) stays as a check on the instrument.

## Consequences

- A session file from any machine can be compared with an earlier one.
- Until the owner supplies the sources, `throughput` has only the local sources to run; that is stated, not hidden.
- 15b adds the alternating IDM harness on top of the same session schema.
