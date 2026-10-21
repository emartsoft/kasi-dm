# Kasi browser protocol

The messages between the Kasi browser extension (through `Kasi.NativeHost`) and the Kasi application, defined by TRD section 9.4.

| File | What it is |
| --- | --- |
| `kasi-messages.schema.json` | The single definition: JSON Schema draft-07, one `oneOf` over the eleven message types, discriminated by `type`. |
| `src/Kasi.Ipc/` | The C# contracts (`IpcMessage` and its records), `IpcSerializer` and the checks the schema also states. |
| `tests/fixtures/ipc/valid`, `invalid` | Sample messages read by both test suites. |

## Rules

- **Every message** has `v` (the protocol version, `1`), `id` (the correlation id, 1 to 64 characters) and `type`. A reply carries the id of the message it answers.
- **Strict.** An unknown `type`, an unknown field (at any depth), a missing required field, a value of the wrong type, a duplicate property, an enum value that is not spelled exactly as listed, or a `v` other than 1 rejects the whole message. `v` other than 1 is `UNSUPPORTED_VERSION`; everything else is `INVALID_MESSAGE`. A rejection carries a code, never the offending text.
- **Capped.** Strings, lists and numbers have maximums (for example a URL is at most 8,192 characters, a message carries at most 200 cookies, a batch at most 100,000 links). The caps are `maxLength`, `maxItems`, `minimum` and `maximum` in the schema and `IpcLimits` in C#; a test fails when they differ.
- **URLs.** `http` or `https` everywhere, plus `ftp` and `ftps` for the URL of a download (`addDownload`, `addBatch` links, `linkRefreshed`). The schema checks the scheme and that a host follows; the C# parser (`System.Uri`) is the authority on whether the rest is a valid URL.
- **Secrets.** Cookie values and URLs are never logged, and they are not copied into an exception or an `error` reply.
- **Adding or changing a message** means changing the schema, the records, the sample files (a valid one and an invalid one per type) and TRD 9.4 in one PR. The tests fail if the list of types, the caps or the samples disagree with the records; the extension's Ajv tests (M2.3) check the same samples against the schema. A change that old peers cannot read raises `v` and needs an ADR.

## Messages

| Type | Direction | Purpose |
| --- | --- | --- |
| `hello` | extension → Kasi | Version handshake; Kasi replies with `rules` |
| `rules` | Kasi → extension | Interception rules, bypass key, enabled flag |
| `addDownload` | extension → Kasi | One intercepted or context-menu download |
| `addBatch` | extension → Kasi | Links chosen in the picker |
| `mediaDetected` | extension → Kasi | Manifest or direct media found in a tab |
| `captureMedia` | extension → Kasi | The user chose a stream |
| `refreshLink` | Kasi → extension | Open the referrer page and watch for a matching download |
| `linkRefreshed` | extension → Kasi | Fresh URL and cookies for a paused job |
| `openSettings` | extension → Kasi | Show Kasi's settings window; `section` is `general` or `browser` (default) |
| `ack` | both | Success for a correlation id |
| `error` | both | Failure for a correlation id (`code` and an optional short `detail`) |
