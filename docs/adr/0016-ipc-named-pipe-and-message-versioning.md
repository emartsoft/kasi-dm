# 0016. IPC: strict message contracts, the schema and the named pipe

- Status: Accepted (contracts M2.1a, pipe M2.1b)
- Date: 2026-10-21
- Requirements: KASI-NFR-010, KASI-BRW-006
- Related: TRD 5.1, 5.2, 9.3, 9.4; `docs/plans/m2-1.md`

## Context

The browser extension talks to the application through a native messaging host. TRD 9.4 fixes the message set and says unknown types and
fields are rejected. Three programs read these messages (the application, the Native AOT host and the TypeScript extension), and the
input comes from a browser, so it has to be treated as untrusted.

## Decision

1. **One definition, no schema library in C#.** `docs/protocol/kasi-messages.schema.json` (draft-07) is the contract. The extension generates its
   types from it and validates with Ajv (M2.3). C# does not validate against the schema at run time, which would need a new package;
   it has records, a strict serializer and the same caps, and tests keep the three in step through shared sample messages
   (`tests/fixtures/ipc/valid` and `invalid`) and a test that compares the schema's caps with `IpcLimits`.
2. **Strictness lives in the serializer settings, each proven by a test.** `System.Text.Json` source generation (Native AOT safe) with
   unknown members disallowed, duplicate properties disallowed, `required` honoured, non-nullable members refusing `null`, enums as exact
   names with numbers refused, and the `type` discriminator allowed anywhere in the object. What the JSON shape cannot say (lengths, counts,
   ranges, URL schemes) is checked by `IpcValidator`, on receive and again on send so a bug cannot emit what the peer would refuse.
3. **Versioning.** `v` is 1. A message with another `v` is `UNSUPPORTED_VERSION` even when its shape differs, because the version is
   read on its own when the full parse fails. The `hello` handshake carries `protocolMin` and `protocolMax`; a change that old peers
   cannot read raises `v`.
4. **No input in errors.** A rejection is an `IpcProtocolException` whose text is built from the code only and which has no inner exception
   (a JSON error can quote a path or a value). URLs and cookie values are secrets (CLAUDE.md rule 3).
5. **A new message, `openSettings`.** TRD 9.1 says the options page opens Kasi's settings, and TRD 9.4 had no message for it. The protocol
   gains `openSettings` (extension to Kasi, answered with `ack`) and TRD 9.4 is updated with it. The alternative, a `kasi://` handler
   registered by the installer, was rejected: the pipe, the launcher and the allow-listing already exist and nothing new would be
   registered on the machine.
6. **`Kasi.Ipc` references no other Kasi project**, so the Native AOT host stays small; the layering test now says so.

## Consequences

- The three consumers cannot drift unnoticed: changing the schema, the records or a sample file without the others fails a test.
- The schema checks URLs less strictly than `System.Uri`; the C# side is the authority, and the extension may be stricter.
- Each field cap is a number to keep in two places (the schema and `IpcLimits`), guarded by a test.

## The pipe (M2.1b)

- **Name and access (TRD 5.1, 9.3).** `\\.\pipe\kasi-<user SID>`. The server creates it with an explicit access list of exactly two entries:
  allow the current user read, write and create-new-instance (the server opens one instance per host), and deny the NETWORK SID. Nothing
  for Everyone, Users, Administrators or SYSTEM. The pipe is owned by the user. `PipeOptions.CurrentUserOnly` is not used, because it
  cannot be combined with an explicit list.
- **No squatting.** The first instance is created with `FirstPipeInstance`, so if another process already holds the name `Start()` throws
  instead of Kasi serving a pipe someone else can read. The client checks, before it sends a byte, that the process serving the pipe
  belongs to the same Windows user (process token owner, via `GetNamedPipeServerProcessId`); if the owner differs or cannot be read
  (for example an elevated server read from a normal client) it refuses. It also connects with the Identification impersonation
  level, so the server cannot act as the client.
- **Frames.** A 4-byte little-endian length and UTF-8 JSON, as in the browser's native messaging. Length 0 or over 64 MiB closes the
  connection. The receive buffer grows with the bytes that arrive, so a peer that claims 64 MiB and sends nothing costs a small buffer.
- **Many hosts.** Any number of hosts (one per browser profile) may connect, up to a cap of 64. A connection must open with an acceptable
  `hello` within 10 s (versions must include 1), else it is closed; after that it is registered. `SendAsync` pushes to one host,
  `BroadcastAsync` to all at the same time, dropping a host that does not take the message within 5 s. `RequestAsync` waits for the
  `ack` or `error` that carries the request's id.
- **Failure policy.** A malformed or unexpected message gets an `error` with a code and no input, and the connection closes; the server
  keeps serving others. A handler that throws costs that message an `INTERNAL` error, not the connection.
- **Buffers.** 64 KiB each way. A zero-size buffer makes a write wait for the reader, which would stall a broadcast on one slow host.
- **Windows only.** `Kasi.Ipc` is marked `SupportedOSPlatform("windows")`; the host and the app are Windows programs (TRD 5.1).
- **Verified by** tests of the access list read back from a live pipe, the second-instance failure, a client refusing a server owned by
  another SID or by none, the limits above, and (elevated, run on CI) a process of a temporary local user that cannot open the pipe
  while it can open a control pipe that allows everyone. Dropping the NETWORK deny, allowing Everyone, or accepting `v` 2 each fails a
  named test.
