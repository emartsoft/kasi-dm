# 0015. CI growth: the coverage gate, Ubuntu containers and nightly fuzzing

- Status: Accepted incrementally for M1.16 (16a and 16b landed; 16c extends it)
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

## Decision (16b)

**One image, built by the test.** `tests/Kasi.Engine.Tests/Containers/Dockerfile` installs the Ubuntu 24.04 packages for vsftpd,
Squid, Dante, OpenSSL and CA certificates on a base pinned by digest. Testcontainers builds it once per run and each test class
starts the role it needs with its own configuration (mapped in as files), so there is no third-party image to trust or to drift,
and the daemons are the distribution's own. Testcontainers (MIT, tests only) is the one new package.

**Ubuntu only, and out of the Windows jobs.** GitHub's Windows runners cannot run Linux containers. The tests carry
`Category=Integration` and `Category=ContainerIntegration`; the Windows integration step now filters
`Category=Integration&Category!=ContainerIntegration`, and the unit step already excludes `Integration`. The new
`ci-conformance-linux.yml` runs only the container category. It needs no secrets, so it runs on pull requests.

**A missing Docker is a failure in CI and a skip on a laptop.** The workflow sets `KASI_REQUIRE_DOCKER=1`, like
`KASI_REQUIRE_VHD` for the disk-full test; without it the tests skip with the reason.

**What is checked.** vsftpd: passive FTP and explicit FTPS (a generated self-signed certificate pinned as the trust root) each
download 16 MiB and match its SHA-256, a transfer stopped after a few MiB resumes with `REST` at the flushed cursor, and a wrong
password is an authentication failure that does not echo the password. Squid: a plain HTTP download, an HTTPS download tunnelled
with `CONNECT`, and Basic proxy credentials accepted when right and refused without echoing them. Dante: a download over SOCKS5
with username and password, and a wrong password. The origin is a `Kasi.TestServer` bound to all interfaces; the containers reach
it as `kasi-origin.test` (a name already in the test certificate) mapped to the Docker host gateway, and only the proxy ever
resolves that name.

**Reused behaviour, new assertions only where a real daemon differs.** The in-process FTP and proxy servers keep proving the
engine's detailed behaviour (dropped data connections, retries, bandwidth caps); the containers prove the same transfers against
the software users run.

**What running them against the real daemons found** (all in the test setup, none in the engine):
- vsftpd 3.0.5 rejects `ssl_tlsv1_2` and exits with code 2, so the option is not set.
- vsftpd run as process 1 of a container dies with exit code 139 when a .NET client opens a second FTPS session; with Docker's init
  process in front it is an ordinary child and the transfers work, so the container is created with `Init = true`.
- Squid tried the IPv6 address Docker's host gateway also has, and failed with "network unreachable" from inside the container,
  so the containers keep the IPv4 address of `kasi-origin.test` only.

A container that does not start fails with its own log in the message. Breaking the `REST` offset in the FTP transfer makes the
vsftpd resume test fail.

## Consequences

- A pull request that lowers `Kasi.Engine` below 80% fails CI, and the figure is in the job summary.
- The margin is about 6.5 points; large new engine code needs tests with it.
- Only the Windows x64 job gates. The Ubuntu leg of 16b adds its own coverage later, and the gate stays on the Windows figure.
