# Contributing to Kasi

This guide is the working agreement for everyone committing to Kasi. If something here conflicts with the TRD (`KASI-TRD-2026-001`), the TRD wins for *what* to build; this guide wins for *how* we work.

## 1. Branching

We use **trunk-based development** with short-lived branches.

| Branch | Purpose | Rules |
| --- | --- | --- |
| `main` | Always releasable. Every merge produces a signed beta build. | Protected. No direct pushes. Merge via PR only, squash merge. |
| `feature/<ticket>-<slug>` | New work, e.g. `feature/KASI-142-slow-lane-split` | Branch from `main`. Live ≤ 3 working days; split the work if it needs longer. |
| `fix/<ticket>-<slug>` | Defect fixes, e.g. `fix/KASI-207-etag-resume` | Same as feature. |
| `chore/<slug>` | Tooling, dependencies, docs | Same as feature. |
| `release/<major>.<minor>` | Created only when a shipped version needs a hotfix | Cut from the release tag; fixes are made on `main` first and cherry-picked. |

- Rebase on `main` before requesting review; do not merge `main` into your branch.
- Incomplete features merge behind a feature flag (`KasiFeatures` in `Kasi.Core`), off by default, so `main` stays releasable.
- Releases are tags on `main`: `v1.0.0`, `v1.0.1-beta.3`. Versions come from tags via MinVer; never edit version numbers by hand.

## 2. Commit messages

Use [Conventional Commits](https://www.conventionalcommits.org/). Release notes are generated from them.

```
<type>(<scope>): <summary in imperative, ≤ 72 chars>

<optional body: what and why, wrapped at 100>

Refs: KASI-142
```

| Type | Use for |
| --- | --- |
| `feat` | User-visible feature |
| `fix` | Defect fix |
| `perf` | Performance improvement with no behaviour change |
| `refactor` | Code change with no behaviour change |
| `test` | Tests only |
| `docs` | Documentation only |
| `build` / `ci` | Build scripts, packaging, pipelines |
| `chore` | Anything else (dependency bumps, tidy-ups) |

Scopes match project areas: `engine`, `media`, `torrent`, `scheduling`, `data`, `ipc`, `app`, `host`, `extension`, `build`. Example: `fix(engine): keep lease in flight when segment is split`.

The squash-merge title must follow the same format, because it is what lands on `main`.

## 3. Code style

Style is enforced by `.editorconfig`, analyzers and `dotnet format`; the build fails on violations (`TreatWarningsAsErrors` is on). Run `dotnet format` before pushing.

### 3.1 C#

- Nullable reference types are enabled everywhere. Do not suppress nullable warnings with `!` unless you add a comment explaining why the value cannot be null.
- File-scoped namespaces; one public type per file; file name matches the type.
- `async` all the way. Never block on tasks (`.Result`, `.Wait()`, `GetAwaiter().GetResult()`). Async methods end in `Async` and accept a `CancellationToken` as the last parameter.
- Library projects (`Kasi.Engine`, `Kasi.Media`, `Kasi.Data`, …) use `ConfigureAwait(false)`. `Kasi.App` does not.
- Hot paths (network reads, file writes, checkpointing) must not allocate per chunk: use `ArrayPool<byte>`, `Memory<T>`/`Span<T>` and pooled objects. Benchmark changes to them with BenchmarkDotNet and include results in the PR.
- No static mutable state. Dependencies come through constructor injection, registered in `Kasi.App`'s composition root.
- Use `TimeProvider` instead of `DateTime.Now` or `Stopwatch` directly, so schedules and timeouts are testable.
- Exceptions are for exceptional cases. Expected download outcomes (404, range not supported, file changed) are results, not exceptions.
- Logging uses Serilog message templates (`Log.Information("Split segment {Index} at {Offset}", i, off)`), never string interpolation. **Never log URLs with query strings, cookies, headers, auth tokens or full user paths.** Use the `Redact` helpers in `Kasi.Core.Logging`.

### 3.2 Avalonia UI

- MVVM with CommunityToolkit.Mvvm source generators (`[ObservableProperty]`, `[RelayCommand]`).
- Views contain no logic beyond view-only behaviour; no code-behind that touches services.
- View models never call engine methods synchronously on the UI thread. They subscribe to engine events and update through the 250 ms batched dispatcher (`UiBatchDispatcher`).
- Compiled bindings on (`x:DataType` on every view).
- Every interactive control has an `AutomationProperties.Name`. Keyboard access is required for every action (KASI-UI-010).
- Colours, spacing and typography come from the shared resource dictionaries in `Kasi.App/Styles`; no hard-coded colours in views.

### 3.3 TypeScript (extension)

- Strict mode on; no `any` without a comment.
- ESLint and Prettier configs in `extension/` are authoritative; `npm run lint` must pass.
- All messages to the native host are built and validated with the generated types from `docs/protocol/kasi-messages.schema.json`. Never hand-write message objects.

## 4. Tests

- Every behaviour change ships with tests in the same PR.
- **Every defect fix starts with a failing test** that reproduces it, wherever the defect can be automated.
- Tests that implement a test-plan case carry its ID: `[Trait("TC", "TC-ENG-008")]`. Integration tests carry `[Trait("Category", "Integration")]`.
- Tests never reach the public internet. Use `Kasi.TestServer`, its chaos profiles, or Testcontainers.
- Download tests verify the SHA-256 of the result. A completed state alone is not evidence.
- Fault-injection tests use seeded randomness; print the seed on failure.
- A flaky test is quarantined (`[Trait("Quarantine", "true")]`) within one working day and fixed or removed within one sprint, with an issue tracking it.

## 5. Pull requests

### 5.1 Before you open one

- [ ] Branch rebased on `main`
- [ ] `dotnet build` and `dotnet test` pass locally (and `npm test` for extension changes)
- [ ] `dotnet format --verify-no-changes` passes
- [ ] PR is focused: one change, ideally under 400 lines of non-generated diff

### 5.2 Review rules

| Change touches | Required approvals |
| --- | --- |
| Most code | 1 |
| `Kasi.Engine` segmentation, resume or checkpointing; `Kasi.Data` migrations | 2, including the lead developer |
| Security-sensitive code: `Kasi.Ipc`, `Kasi.NativeHost`, extension messaging, DRM detection, secret storage, update verification | 2, including the lead developer; tag the PR `security` |
| Build, signing or release pipeline | 1 from the lead developer or CTO |

- Reviewers respond within one working day.
- The author resolves every comment, either with a change or a reply. Reviewers resolve their own threads.
- CI must be green: build, tests, coverage gate (engine ≥ 80%), format check, extension lint and tests.
- Squash merge only. The author merges once approved.

### 5.3 What reviewers check

1. Does it do what the ticket and TRD requirement say?
2. Are the tests meaningful, and do they cover failure paths?
3. Could this lose data or corrupt a file if interrupted at any line?
4. Does anything sensitive (URLs, cookies, paths) reach logs, telemetry or crash reports?
5. Does the engine still have no UI dependency?
6. Is it readable by the next person without the author present?

## 6. Definition of done

A ticket is done only when **all** of these are true:

- [ ] Code merged to `main` through an approved PR with green CI
- [ ] Acceptance criteria of the linked TRD requirement(s) pass, with automated tests tagged with requirement or TC IDs
- [ ] No new analyzer warnings; coverage not reduced
- [ ] Logs and errors are clear to a support engineer and contain no sensitive data
- [ ] UI changes are keyboard-accessible, have automation names, and work in light and dark themes
- [ ] User-visible text is in the resource files (ready for Kiswahili localisation)
- [ ] Performance-sensitive changes include benchmark results with no regression beyond 5%
- [ ] Docs updated where behaviour changed: ADR for architectural decisions, protocol schema for message changes, migration for schema changes
- [ ] Verified by QA in the nightly build (for user-facing changes)

## 7. Architecture decisions

Significant decisions (a new dependency, a change to the engine design, storage format, IPC protocol or security model) are recorded as an ADR in `docs/adr/NNNN-title.md` using the template in `docs/adr/0000-template.md`, and linked from the PR. Adding any NuGet or npm dependency requires a one-line licence check in the PR description; GPL-licensed dependencies are not allowed.

## 8. Security

- Report vulnerabilities privately to the lead developer or CTO, not in public issues.
- Never commit secrets. Signing keys and update-feed credentials live only in CI secret storage.
- The DRM refusal in `Kasi.Media` must not be made configurable, bypassable or disabled in any build.

## 9. Getting help

Questions about scope go to the TRD and then the CTO. Questions about code go to the PR or the team channel. If a rule here gets in the way of good work, propose a change to this file through a PR.
