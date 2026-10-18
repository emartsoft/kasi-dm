# 0009. Authentication scope, protected job secrets and link refresh

- Status: implemented in M1.10a/b/c, pending merge
- Date: 2026-10-18
- Requirements: KASI-ENG-009, KASI-ENG-010, KASI-NFR-013; TC-ENG-016 to 018

## Context

The browser extension will give the engine cookies, headers and credentials for protected downloads. A probe may follow
ten redirects before the workers begin fetching ranges. Forwarding a captured `Authorization` or `Cookie` header to every
hop would disclose the source site's secret to an unrelated host. A stopped job also needs its secret after an app restart,
without leaving plaintext in SQLite. A refreshed signed URL must preserve already verified bytes only if it names the
same file.

## Decision

**Storage (M1.10a).** `JobSecret` contains cookies, headers and optional Basic/Digest credentials. The three secret-bearing
models redact `ToString()`. `JobSecretRepository` serialises camelCase JSON and protects it with Windows DPAPI under the
current user's account before writing `job_secret.payload`. A missing job cannot acquire a secret; deleting a job cascades
to its secret. Completed transfers delete the saved secret.

**Request scope (M1.10b).** One `RequestScope` decides which headers and cookies may go on each probe hop and each lease.
`Authorization`, `Cookie` and `Proxy-Authorization` from caller headers, and every header captured in the job secret
whatever its name, are sent only to the initial request's origin (scheme, host and port). Captured headers win on a name
clash. Other caller headers follow redirects. A failure to delete the secret after a finished download is ignored: the file
is already committed, and the engine facade (M1.13) removes a leftover secret when it completes the job.
Each cookie is independently checked against the destination URI: a leading-dot domain includes the named host and its
subdomains on a label boundary; a host-only domain and an IP literal require an exact host; the path must match at a
segment boundary; `Secure` requires HTTPS. The port does not affect cookie matching. An eligible cookie may therefore be
sent to a redirected origin only when the cookie itself authorises that host.

**Transport (M1.10b).** The pool attaches `NetworkCredential` only to handlers for the initial origin; every other origin's
lane has no credentials. The prober chooses a lane for each redirect hop. `SocketsHttpHandler` answers Basic and Digest
challenges, while automatic redirects and automatic cookies stay off. A `401` or `403` remains `AuthRequired`, so the
retry policy does not repeat it. The local test server supports an MD5 Digest challenge solely to verify that standard
scheme; MD5 does not protect stored secrets.
On resume, the saved job URL supplies that initial origin (or the caller explicitly provides `InitialUrl`). Without either,
the runner suppresses credentials and sensitive raw headers. A saved final URL on another origin cannot inherit the
source origin's credentials.

**Link refresh (M1.10c).** The engine probes a proposed new URL with a one-byte range and compares total size and the
stored ETag (or Last-Modified) before changing the saved URL or writing bytes. A successful validation updates only
`url` and `final_url`, then resumes the existing segment cursors with `If-Range`. The saved checkpoint identity must
still match the caller's paused job. A secret's sensitive headers and credentials are bound to their original origin
before a URL change is persisted; a newly supplied secret can bind to the refreshed origin. Browser cookies retain
their individual domain and path scope.

## Consequences

- A redirect cannot carry the source origin's credentials or sensitive raw headers to another origin. Cookie domains
  still work across their authorised subdomains.
- Basic and Digest can require an extra challenge response on each worker lane; test server counters make this visible.
- DPAPI secrets can be read only under the Windows user account that protected them. A corrupt or undecryptable payload
  is reported as invalid data, without including secret values in the error.
- The engine facade in M1.13 remains responsible for job-state transitions and asking the browser for fresh credentials.
