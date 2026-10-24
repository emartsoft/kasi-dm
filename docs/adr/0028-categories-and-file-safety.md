# ADR 0028: Categories, rules and file safety

- **Status:** Proposed (M2.11a; 11b to 11e extend it)
- **Requirements:** KASI-CAT-001, KASI-CAT-002 (11a); KASI-CAT-003, KASI-CAT-004 (11d, 11e) | **Test cases:** TC-CAT-001, TC-CAT-002
- **Plan:** [`docs/plans/m2-11.md`](../plans/m2-11.md)

## Context

A download takes its category from the dialog or a remembered site, otherwise it is General: nothing looks at the link, so a `.mp4` lands in General and not in Video (TRD 10.4, KASI-CAT-001). There is no way to say "everything from `*.example.com` goes here" (KASI-CAT-002).

## Decision (11a)

1. **One table, `category_rule`** (migration `0006`): `kind` is `extension`, `domain` or `url`; `position` orders rules of a kind; `is_builtin` rules ship with Kasi (the TRD 10.4 table, plus `bz2`, `tar.gz`, `deb`, `rpm`, `apk`, `dmg`, `pkg`, `jpeg`) and can be switched off (`is_enabled`) but not deleted; deleting a category deletes its rules. General (the fallback) and Torrents (routed by job kind) have none.
2. **A pure resolver in `Kasi.Core`**, `CategoryResolver.Resolve`, with no I/O and no clock, in the order of TRD 10.4: rules by domain or address (position order), then extension rules (the user's before the built-in ones, a double extension such as `tar.gz` before its last part), then the MIME type only when the file name has no extension, then General. It returns the category and the rule that decided, which the settings page (11c) shows.
3. **Patterns are globs** (`*`, `?`, ignoring case) matched by a two-cursor loop, never a regular expression, so no pattern can make a match explode. A domain rule is matched against the host (`*.github.com` does not match `github.com`), an address rule against the address **without query and fragment** (a query can carry a token). `GlobPattern.Validate` refuses an empty or over-long pattern, one that matches everything, one with spaces or control characters, and an extension that is not letters, digits and inner dots.
4. **What a person chooses is not overridden** (applied in 11b): the dialog's choice, then the remembered site, then the rules.

## Consequences

- 11a changes no behaviour a user can see: nothing calls the resolver yet. 11b makes every way of creating a job use it.
- The migration number is the next free one at the time of writing; if the M3 work adds `0006` first, one of the two is renumbered at merge.
