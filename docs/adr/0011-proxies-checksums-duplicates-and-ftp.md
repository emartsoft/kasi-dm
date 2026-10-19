# 0011. Remaining engine transports and verification

- Status: accepted for M1.12a; proxy and FTP sections pending their implementation steps
- Date: 2026-10-19
- Requirements: KASI-ENG-002, KASI-ENG-011, KASI-ENG-012, KASI-ENG-013; TC-ENG-019 to TC-ENG-022

## Context

M1.12 adds optional file checksums, duplicate detection, proxies, and FTP/FTPS. The existing part writer deliberately separates sealing from committing, leaving a place to verify bytes before the destination is replaced. The add-download prompt needs a conflict decision before it creates a job, while the engine facade that will serialise check and add arrives in M1.13.

## Decision

The HTTP transfer runner uses one finalisation method for empty, single-stream, and segmented transfers. It seals and flushes the part file, streams it through the selected published MD5, SHA-1, or SHA-256 hash, and commits only on a match. MD5 and SHA-1 are comparison options for published checksums, not security primitives. A mismatch is a `VerificationFailed` result; the complete part file stays available for inspection and the old destination remains intact. The next attempt is a fresh download. Invalid algorithm or hex input fails option validation before a network request.

`DuplicateDetector` compares canonical origin plus path and query without fragments and also checks the requested target path, part file, and saved jobs. A signed query change remains a distinct URL, though the filename can still conflict. Resume is offered only for an unfinished, resumable job with the same URL and an existing part file. Auto-rename skips names claimed by jobs or disk files, preserves common compound extensions, and respects the 200-character filename cap. Overwrite is a choice passed to the existing final commit; detection itself never deletes a file. The facade must serialise the check with the eventual add, since filesystem and job-list observations can otherwise race.

## Consequences

Hashing adds one sequential read of the completed part file before rename. A checksum mismatch cannot silently replace an existing destination. The UI prompt and job-state transition for "Verification failed" remain with M2 and M1.13 respectively. Proxy and FTP transport decisions will be added to this ADR as M1.12b and M1.12c land.
