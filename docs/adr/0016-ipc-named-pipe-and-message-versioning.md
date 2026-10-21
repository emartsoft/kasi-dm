# 0016. IPC: strict message contracts, the schema, and (2.1b) the named pipe

- Status: Accepted for the contracts (M2.1a); the pipe section is added in M2.1b
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
- The pipe's access control, framing and host registry are decided and recorded in M2.1b.
