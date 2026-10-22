# 0017. Native messaging host and process lifecycle

- Status: Accepted (host relay, launcher and registration M2.2a; fuzzing and the AOT publish check M2.2b)
- Date: 2026-10-21
- Requirements: KASI-BRW-006, KASI-NFR-010
- Related: TRD 5.1, 5.2, 9.3, 9.4; ADR 0016; `docs/plans/m2-2.md`

## Context

The browser starts `Kasi.NativeHost.exe` and talks to it over stdin and stdout with the native messaging frame (4-byte little-endian
length, then UTF-8 JSON). Kasi listens on a per-user named pipe (ADR 0016). The host sits between the two, receives bytes from a browser
extension and must treat them as untrusted.

## Decision

1. **Two pumps, one writer.** Kasi pushes `rules` and `refreshLink` whenever it likes, so the host is not request/response. A
   stdin-to-pipe pump and a pipe-to-stdout pump run at the same time; stdout has a single writer (`NativeFrameWriter`) with one lock and one
   flush per frame, so frames never interleave.
2. **The strict parse is the validation.** Every browser frame goes through `IpcSerializer.Deserialize` before it is forwarded, and the host
   never forwards bytes it did not parse. `rules` and `refreshLink` (Kasi-to-extension types) are refused when they come from the browser,
   because Kasi would refuse them and close the connection. The frame reader is `IpcFraming` from `Kasi.Ipc`: it is the same wire shape and
   cap, so there is one implementation to fuzz.
3. **Failure policy.** A frame that cannot be trusted to be a frame (length 0, over 64 MiB, cut short) cannot be resynchronised: the host
   logs a fixed line to stderr and exits with code 2 without writing a frame. A well-framed invalid message gets an `error` reply
   (`INVALID_MESSAGE` or `UNSUPPORTED_VERSION`) that carries the request's id when the message has a usable one (otherwise `unknown`) and a fixed
   detail, never the input, and the host keeps running. Kasi unreachable gives `APP_NOT_RUNNING`, Kasi's executable missing gives
   `APP_NOT_INSTALLED`; the extension then resumes the browser's own download (TC-BRW-002).
4. **Limits (TRD 9.3).** Browser to host 64 MiB, host to browser 1 MiB (Chrome's limit). A message from Kasi that would exceed 1 MiB
   becomes an `INTERNAL` error with the same id; the writer refuses an oversized payload as a backstop.
5. **Starting Kasi.** `AppLauncher` connects first; if nobody serves the pipe and `Kasi.exe` exists next to the host it starts
   `Kasi.exe --tray` (never a PATH lookup) and retries the pipe with a backoff of 50 ms doubling to 500 ms until 5 s have passed. The process
   is started through the shell (`UseShellExecute`) so it does not inherit the browser's stdin and stdout handles, which would keep the
   protocol streams open. `--tray` is implemented by the application in a later M2 task; until then the launcher is tested with a fake
   starter. A pipe served by another user's process is `APP_NOT_RUNNING` and is never retried into.
6. **Reconnect.** *Changed in M2.5a (ADR 0020):* when Kasi closes the pipe the user has quit Kasi, so the host writes one
   `error APP_NOT_RUNNING` (id `unknown`) to the browser and exits with code 3; it does not launch Kasi again. The extension then shows
   "not connected" at once and reconnects only when there is a reason (a matching download, the popup, a browser start), which starts
   a new host and, through it, Kasi. This closes plan m2-4 G8 and the known gap of ADR 0019. What stays: a send on a dead pipe
   reconnects once through the launcher, replaying the last `hello` (Kasi closes a connection that does not open with one), and sends again.
7. **Stdout belongs to the protocol.** `Console.Out` is redirected to stderr at start-up and logs carry fixed text only: never a URL, a
   cookie, a header or an exception message (CLAUDE.md rule 3). The host references only `Kasi.Ipc`.
8. **Registration.** `src/Kasi.NativeHost/Registration/com.martsoft.kasi.json` is the manifest template and
   `scripts/native-host/Register-NativeHost.ps1` registers it per user (HKCU) for Chrome, Edge and Firefox for development. The installer
   takes over in M4; allow-listing a non-listed extension is refused by the browser (TC-BRW-007 belongs to that work).

9. **Fuzzing (TC-BRW-006).** `NativeHostFuzzTests` feeds 100,000 seeded malformed frames (`MalformedFrames`: broken JSON and UTF-8,
   wrong types, unknown types and fields, duplicate properties, deep nesting, huge strings, control characters, bad `v`, values over
   their caps, messages only Kasi may send) through one `HostRelay` session: each gets an `error` with a fixed detail, nothing reaches
   Kasi, the log stays empty, memory does not grow, and the host still relays a valid message at the end. A table-driven test breaks
   the framing 1,000 ways (zero length, over 64 MiB up to 4 GiB, cut header, cut payload): exit code 2, the frames before the break
   answered, never a partial frame. The nightly SharpFuzz target `HostMessageFuzz` (`tests/Kasi.Fuzz`, 5 minutes in `nightly-fuzz.yml`,
   `Run-Fuzz.ps1 -Target host`) instruments `Kasi.Ipc` and `Kasi.NativeHost` and holds the invariants in one place, with an oracle: the
   strict parse alone decides the reply. Its seeds are one valid message of each type, and its findings are replayed as a unit test from
   `tests/fixtures/fuzz/host-crashes`. The first run found a real defect in the 2.2a relay: `TryReadId` threw on invalid UTF-8 in an
   `id` string and would have ended the host; it now treats that as "no id" (saved as `invalid-utf8-in-id`).
10. **Native AOT in CI.** The `native-host-aot` job in `ci.yml` publishes `Kasi.NativeHost` for `win-x64` (single file; trimming and AOT
    warnings are errors), fails when the executable is over 5 MB, records the cold start (median of 20 launches) in the job summary
    without gating on it, and runs `AotExecutableTests` against the published executable over real stdin and stdout pipes: 1,000 of
    the malformed frames come back as errors with exit code 0, and a broken frame gives exit code 2, nothing on stdout and the fixed
    line on stderr. The measured numbers on the development machine: 3.48 MB and a 22 to 24 ms cold start (TRD target 50 ms, judged
    on the benchmark machine). The managed fuzz target runs the same relay code the AOT build compiles, which is why the process
    sample exists: it is the only place the trimmed build is exercised.

## Consequences

- The relay runs in a test process with fake streams and a fake pipe, so the failure policy is tested without a browser.
- A host that cannot reach Kasi costs the first message up to 5 s; the extension's own ack timeout decides what the user sees.
- Exit codes: 0 stdin closed, 1 unexpected failure, 2 broken frame, 3 Kasi closed the pipe (after one `APP_NOT_RUNNING` error).
