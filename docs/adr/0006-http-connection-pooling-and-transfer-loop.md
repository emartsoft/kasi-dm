# 0006. HTTP connection pooling, TLS validation and the transfer loop

- Status: Draft — the pool and TLS sections are implemented and accepted for M1.7a; the worker-loop sections are completed in M1.7b/c.
- Date: 2026-10-18
- Requirements: KASI-ENG-001, KASI-ENG-006, KASI-ENG-008, KASI-NFR-011, KASI-RES-002, TRD 5.3, 6.2, 6.4, 6.5, ADR 0004, ADR 0005

## Context

M1.7 turns the probe, scheduler and part-file writer into a real download. Three choices decide whether it meets the TRD:
how long a connection pool lives, how a worker gets its own HTTP/2 connection, and how TLS can be tested without ever
shipping a validation bypass. A shared `SocketsHttpHandler` is the obvious default, but one handler multiplexes every
HTTP/2 segment on the connection the probe opened, so `TC-ENG-002` and `TC-ENG-010` could not prove separate connections
or one handshake per lane. The transfer loop also has to decide what happens to the workers, the write buffers and the
part file when one lease fails or the caller cancels.

## Decision

**Pool lifetime.** `HttpHostPool` belongs to one `DownloadAsync`/`ResumeAsync` call. The runner creates it and disposes it
when the transfer ends. A pool shared by jobs would put two jobs' worker 0 on the same single-connection lane and
serialise them; per-host limits across jobs belong to the engine facade (M1.13).

**Lanes.** A lane is keyed by `(origin, workerId)`. Each lane owns one `SocketsHttpHandler` with
`MaxConnectionsPerServer = 1` and `EnableMultipleHttp2Connections = true`, so a worker reuses its keep-alive connection
for every lease and HTTP/2 gets `K` TCP connections for `K` workers instead of multiplexing on the probe's connection.
`GetInvoker` creates lanes lazily and the pool disposes every invoker it handed out.

**Transport settings.** Every lane sets `AllowAutoRedirect = false` (the prober follows redirects), `AutomaticDecompression = None`
(byte offsets must match the origin), `UseCookies = false` (the engine owns the cookie jar) and the timeouts from
`HttpHostPoolOptions`. Probes and lease requests ask for HTTP/2 with `RequestVersionOrLower`, so TLS servers that offer h2
use it and plaintext or h1-only servers fall back to HTTP/1.1. Lease requests keep `Accept-Encoding: identity`.

**TLS validation.** Production uses the OS default validation: no validation callback, no bypass flag and no switch that
weakens the chain. `HttpHostPoolOptions.CustomTrustRoots` is **internal**, visible only to `Kasi.Engine.Tests`, and is
implemented through `SslClientAuthenticationOptions.CertificateChainPolicy` with
`X509ChainTrustMode.CustomRootTrust` and `X509RevocationMode.NoCheck` for the ephemeral test certificates. `SslStream`
still enforces the host name and the validity window, so a valid leaf passes, an expired one fails with `NotTimeValid`,
a self-signed one with `UntrustedRoot` and a wrong-host one with a name mismatch. No `RemoteCertificateValidationCallback`
is set anywhere in production or test pool code.

**Worker-loop coordination (M1.7b).** Worker 0's first lease is the probe's live bounded `206` body, so the first bytes
are not fetched twice. Other leases are bounded `Range` requests on the worker's lane with `If-Range` when the probe
recorded a strong `ETag` or `Last-Modified`. A split never cuts into a lease in flight. When a lease is superseded by an
endgame peer, the worker drops its buffered bytes, completes the scheduler lease and drains the bounded response so the
connection returns quietly (`TC-ENG-011`); normal leases read to EOF before disposal.

**Failure and cancellation order (M1.7b/c).** One code path cancels the sibling workers, has each worker flush its
pending buffer and advance its lease (or abandon it on failure), takes the segment snapshot, calls
`PartFileWriter.FlushAsync`, disposes the writer **without** `Abandon()` so `.kasipart` survives, and returns a typed
`TransferFailed` carrying the pre-flush snapshot and the flushed byte count. Pause and shutdown use the same path, so a
recorded cursor can never be ahead of the data on disk.

## Consequences

- A 1 GiB, 8-connection HTTPS download performs one handshake per worker lane (`TC-ENG-010`), not one per segment.
- The pool holds one handler and one idle connection per worker for the life of a transfer; that is the cost of
  proving per-worker HTTP/2 connections. Per-host caps across concurrent jobs remain M1.13.
- A server that rejects the test certificate fails during the probe with `ProbeErrorKind.CertificateError` and creates no
  part file. There is no code path that turns validation off.
- The test server's certificate leaves are re-imported through PKCS#12 with `X509KeyStorageFlags.Exportable` because
  Windows SChannel needs a usable private-key handle; this is test infrastructure only.
- Changing lane keying, the HTTP version policy or the TLS chain policy needs a new ADR.
