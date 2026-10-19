# 0011. Remaining engine transports and verification

- Status: accepted for M1.12a and M1.12b; FTP section pending
- Date: 2026-10-19
- Requirements: KASI-ENG-002, KASI-ENG-011, KASI-ENG-012, KASI-ENG-013; TC-ENG-019 to TC-ENG-022

## Context

M1.12 adds optional file checksums, duplicate detection, proxies, and FTP/FTPS. The existing part writer deliberately separates sealing from committing, leaving a place to verify bytes before the destination is replaced. The add-download prompt needs a conflict decision before it creates a job, while the engine facade that will serialise check and add arrives in M1.13.

## Decision

The HTTP transfer runner uses one finalisation method for empty, single-stream, and segmented transfers. It seals and flushes the part file, streams it through the selected published MD5, SHA-1, or SHA-256 hash, and commits only on a match. MD5 and SHA-1 are comparison options for published checksums, not security primitives. A mismatch is a `VerificationFailed` result; the complete part file stays available for inspection and the old destination remains intact. The next attempt is a fresh download. Invalid algorithm or hex input fails option validation before a network request.

`DuplicateDetector` compares canonical origin plus path and query without fragments and also checks the requested target path, part file, and saved jobs. A signed query change remains a distinct URL, though the filename can still conflict. Resume is offered only for an unfinished, resumable job with the same URL and an existing part file. Auto-rename skips names claimed by jobs or disk files, preserves common compound extensions, and respects the 200-character filename cap. Overwrite is a choice passed to the existing final commit; detection itself never deletes a file. The facade must serialise the check with the eventual add, since filesystem and job-list observations can otherwise race.

`ProxySettings` chooses the system proxy, a direct connection, or an explicit HTTP, HTTPS, or SOCKS5 proxy for one transfer. The custom endpoint contains no user information; proxy credentials are stored in the DPAPI-protected `JobSecret.ProxyCredentials` and passed only to the handler's proxy authentication channel. Origin credentials remain origin-scoped. Exact host and leading-dot domain bypass entries connect directly. The HTTPS proxy spike passed with a TLS connection to the proxy, while an HTTPS origin passed through an HTTP `CONNECT` tunnel. The in-process proxy fixture checks both paths plus SOCKS5, virtual-host routing and authentication. A `407` from a `CONNECT` handshake is permanent authentication failure, not a transient retry. Certificate chain and host validation remain enabled for both proxy and origin TLS.

## Consequences

Hashing adds one sequential read of the completed part file before rename. A checksum mismatch cannot silently replace an existing destination. The UI prompt and job-state transition for "Verification failed" remain with M2 and M1.13 respectively. Proxy routing is fixed per transfer and shared by all HTTP lanes; changing it requires a new transfer. FTP transport decisions will be added as M1.12c lands.
