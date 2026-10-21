# Benchmarks

`Kasi.Bench` measures the engine (TPL 6). Design and decisions: [ADR 0014](adr/0014-benchmark-protocol-and-idm-comparison.md).
Benchmarks are not tests: they need a quiet machine, never run in pull-request CI, and the comparison with IDM needs the public
internet. Build first (`dotnet build -c Release`); the server and the bench both come from the solution.

```powershell
$bench = "bench/Kasi.Bench/bin/Release/net10.0/Kasi.Bench.dll"

# the 1 Gbit/s check: one 16-connection download from a Kasi.TestServer process, with a raw HttpClient control
dotnet $bench local-1gbit --size 4294967296 --connections 16 --runs 3 --out bench/results/local-1gbit-$(Get-Date -Format yyyy-MM-dd).json

# every source in bench/sources.json (8 then 16 connections, 5 runs), plus the local TestServer sources
dotnet $bench throughput --sources bench/sources.json --connections 8,16 --runs 5 --local --line-mbps 940 --window night --out bench/results/throughput-$(Get-Date -Format yyyy-MM-dd).json

# engine-only resource signals (baseline and idle take --idle-seconds, default 600; baseline is the sampler alone)
dotnet $bench resources --scenario baseline --idle-seconds 10
dotnet $bench resources --scenario jobs20
dotnet $bench resources --scenario load100

# regression tracking against the previous session: a throughput drop over 5% or a memory rise over 10% is flagged (exit 1)
dotnet $bench compare bench/results/previous.json bench/results/current.json

# micro-benchmarks (BenchmarkDotNet)
dotnet $bench --filter "*SegmentScheduler*"
```

## Reading the 1 Gbit/s result

The control line is what this machine plus this server can do with a plain `HttpClient` and the same number of range streams.
The engine passes at 1000 Mbit/s or more. If the control itself is below 1000 Mbit/s the verdict is **inconclusive**: the machine
cannot judge the engine, so run it on the benchmark machine (TPL 3.4). A miss with a good control is a finding for the tuning
sprint before M2. Hashing the result is done after the clock stops and is not part of the number, but it must match.

## Sources

`bench/sources.json` has one entry per TPL 6.2 archetype. Each is a **placeholder** until the owner supplies the real source
(a regional mirror, a European mirror, a CDN file, a 2 GiB cloud object, a capped origin and a range-less origin); placeholders are
skipped with a message and never fetched. The file is reviewed monthly. A signed cloud URL carries a query string, so it is never
stored in the file. The `local` section is served by `Kasi.TestServer` and runs without any of that.

## Results

`bench/results/*.json` (schema version 1): machine, line speed, `window` (`business` or `night`), whether the DNS cache was flushed,
and one row per timed download with its SHA-256 result. Record the line speed from a reference speed test before each session and
run each source at both times of day (TPL 6.1). The IDM comparison and the scorecard arrive in M1.15b; those sessions are owner
evidence.

## First readings (15a, developer machine, loopback)

Recorded for the tuning discussion, not as gate results. Machine: the developer laptop, server in its own process, loopback.

| Check | Reading | Target |
| --- | --- | --- |
| 1 Gbit/s, 16 connections, 4 GiB (`local-1gbit`) | engine median about 10.8 Gbit/s; raw `HttpClient` control about 23 Gbit/s | at least 1 Gbit/s |
| Idle engine, working set / CPU | 38 MB / 0.4% of one core | below 80 MB |
| 20 active jobs, peak working set | 85 MB | below 250 MB |
| About 100 MB/s, one job, CPU | 73% of one core (peak working set 75 MB) | below 5% of one core |

The CPU reading at 100 MB/s is far over the target. It includes the kernel time of the loopback receive path, which a real NIC
mostly offloads, and NFR-005 is measured on the whole app with Windows Performance Recorder, so it is a finding for the tuning
sprint (profile the read loop, buffer copies and checkpointing), not a failure of this PR.

The sampler once cost about a third of a core by itself: `Process.Refresh` and `WorkingSet64` enumerate every process on the
machine. It now reads `Environment.WorkingSet` and the process handle, and the `baseline` scenario (sampler alone, about 1%) is
there to catch that kind of mistake.
