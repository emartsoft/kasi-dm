# 0001. Kasi.TestServer content function and chaos control contract

- Status: accepted
- Date: 2026-10-17
- Requirements: KASI-NFR-009, KASI-RES-001 to 006, KASI-ENG-001 to 013 (verification), TPL 3.1 and 3.2

## Context

TPL 3.1 says byte `i` of file `seed` is `hash(seed, i / 4096)[i % 32]` without naming the hash, and TPL 3.2 says a
profile is set "through the `X-Kasi-Chaos` header or the control API" without defining either. Tests in other
languages (the extension's Playwright suite) and in later milestones must be able to recompute expected bytes and drive
the server the same way, so the contract has to be written down once.

## Decision

**Content.** `hash(seed, block)` is `SHA-256(UTF-8(seed) ‖ int64-little-endian(block))`; byte `i` is `hash(seed, i / 4096)[i % 32]`.
Seeds are 1 to 64 characters. The same seed and size always give the same bytes and the same SHA-256, so tests compare
against `GeneratedContent.Sha256Hex(seed, size)` instead of storing fixtures.

**Chaos.** A profile is JSON (`ChaosProfile`). It is registered with `POST /_chaos` (or `ChaosRegistry.Register` in
process), which returns `{ "id": "c1" }`. A request opts in with `X-Kasi-Chaos: c1`; requests without the header, and
the `/_chaos` routes themselves, are never affected. `DELETE /_chaos` removes every profile. An unknown id is `400`.

**Determinism.** Randomised faults draw from `Random(seed * 1_000_003 + requestIndex)`, where `requestIndex` counts the
requests that named the profile, starting at 0. The same requests in the same order replay the same faults. Concurrent
requests may be numbered in a different order between runs, so replay-sensitive tests issue requests sequentially.

**Connections.** "Connection index" is the zero-based order in which the server accepted connections since it started.
`MaxConnectionsPerClient` counts connections open from one client address at accept time and answers `503` on the
connections beyond the limit.

## Consequences

- Changing the hash, block size or RNG derivation silently invalidates every recorded seed; any change needs a new ADR.
- Chaos cannot be selected per connection from outside the process other than by header, which is enough for HttpClient
  default headers and for browsers through the extension's test hooks.
- HTTP/2 and TLS are not served yet; add them (with a generated certificate) when the first HTTP/2 test case needs them.
