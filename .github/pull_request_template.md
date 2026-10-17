## What and why

<!-- One or two sentences. Link the ticket and the TRD requirement(s). -->

Refs: KASI-
Requirements: KASI-

## How it was tested

<!-- Tests added or changed, with TC IDs from KASI-TPL-2026-001 where they apply. -->

- [ ] Unit tests
- [ ] Integration tests (Kasi.TestServer / Testcontainers)
- [ ] Extension tests
- [ ] Manual check (describe):

## Risk check

- [ ] Could this lose data or corrupt a file if interrupted? If yes, explain how it is protected.
- [ ] No URLs, cookies, headers or user paths reach logs, telemetry or crash reports.
- [ ] Engine projects still have no Avalonia reference.
- [ ] New dependency? Licence: <!-- e.g. MIT --> (GPL not allowed)
- [ ] Performance-sensitive path changed? Benchmark results attached.
- [ ] Schema, protocol or architecture change? Migration / schema file / ADR included.

## Screenshots

<!-- UI changes: before and after, light and dark theme. -->

## Definition of done

- [ ] Acceptance criteria pass with tagged automated tests
- [ ] CI green, coverage not reduced, `dotnet format` clean
- [ ] Keyboard-accessible with automation names (UI changes)
- [ ] User-visible text in resource files
