# 0002. Job lifecycle and the stored state names

- Status: accepted
- Date: 2026-10-17
- Requirements: TRD 6.9, TRD 11.2, KASI-RES-004, KASI-RES-005, KASI-MED-004

## Context

TRD 6.9 defines the job lifecycle in a diagram of nine states, and the `job.state` column stores the current one as text.
The diagram leaves some cases open: where Pause can start, what a permanent error does mid-download, how a paused job
resumes, and how "Not enough space", "offline", torrent seeding and a changed remote file are represented. The state names
are also a storage format, so they cannot change casually.

## Decision

**States.** `Queued, Probing, Downloading, Retrying, Paused, Muxing, Verifying, Completed, Failed`, stored as exactly those
strings (`JobStateText`). Numbers and other casings are rejected on read. Job kinds are stored as `http`, `ftp`, `media`,
`torrent`, quota resets as `daily`, `weekly`, `never`.

**Transitions** (`JobStateMachine`; the same table is written out independently in the tests):

| From | To |
| --- | --- |
| Queued | Probing, Paused |
| Probing | Downloading, Failed, Paused |
| Downloading | Verifying (media jobs: Muxing instead), Retrying, Paused, Failed |
| Retrying | Downloading, Failed, Paused |
| Paused | Downloading, Queued |
| Muxing (media only) | Verifying, Failed |
| Verifying | Completed, Failed |
| Failed | Queued |
| Completed | none |

- Pause is allowed from every active state, not only Downloading as drawn. Resume goes to Downloading for a job paused
  after it started and to Queued for one paused before; the engine knows which, the state machine allows both.
- A permanent error (404, 410, and similar) ends Probing or Downloading directly with no retries (TC-RES-009); Retrying is
  only for transient errors, and its way out to Failed is the retry limit (KASI-RES-004).
- Only media jobs ever occupy Muxing; for other kinds it has no way in and no way out.
- Retrying a Failed job returns it to Queued with its saved segments and byte count. Whether those bytes are reused is decided
  when it resumes, by the validator check (KASI-RES-003).
- Failing needs a message; leaving Failed or Retrying clears it.

**Not states.** "Not enough space" and "offline" are reasons attached to Paused. Torrent seeding is a sub-status of Completed
(TRD 8.3). A remote file that changed (TRD 6.5) is an engine action that resets the segments while the job stays in
Downloading, with a warning, not a transition.

## Consequences

- Renaming a state or adding one needs a migration for existing rows and a new ADR; the tests pin the names and the table.
- Adding the `Paused` reasons later means a column or a status code, not a new state.
- The UI, scheduler and torrent adapter can all rely on `JobStateMachine` instead of re-deriving the diagram.
