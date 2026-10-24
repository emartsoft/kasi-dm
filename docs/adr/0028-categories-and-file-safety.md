# ADR 0028: Categories, rules and file safety

- **Status:** Proposed (M2.11a to 11c; 11d and 11e extend it)
- **Requirements:** KASI-CAT-001, KASI-CAT-002 (11a); KASI-CAT-003, KASI-CAT-004 (11d, 11e) | **Test cases:** TC-CAT-001, TC-CAT-002
- **Plan:** [`docs/plans/m2-11.md`](../plans/m2-11.md)

## Context

A download takes its category from the dialog or a remembered site, otherwise it is General: nothing looks at the link, so a `.mp4` lands in General and not in Video (TRD 10.4, KASI-CAT-001). There is no way to say "everything from `*.example.com` goes here" (KASI-CAT-002).

## Decision (11a)

1. **One table, `category_rule`** (migration `0006`): `kind` is `extension`, `domain` or `url`; `position` orders rules of a kind; `is_builtin` rules ship with Kasi (the TRD 10.4 table, plus `bz2`, `tar.gz`, `deb`, `rpm`, `apk`, `dmg`, `pkg`, `jpeg`) and can be switched off (`is_enabled`) but not deleted; deleting a category deletes its rules. General (the fallback) and Torrents (routed by job kind) have none.
2. **A pure resolver in `Kasi.Core`**, `CategoryResolver.Resolve`, with no I/O and no clock, in the order of TRD 10.4: rules by domain or address (position order), then extension rules (the user's before the built-in ones, a double extension such as `tar.gz` before its last part), then the MIME type only when the file name has no extension, then General. It returns the category and the rule that decided, which the settings page (11c) shows.
3. **Patterns are globs** (`*`, `?`, ignoring case) matched by a two-cursor loop, never a regular expression, so no pattern can make a match explode. A domain rule is matched against the host (`*.github.com` does not match `github.com`), an address rule against the address **without query and fragment** (a query can carry a token). `GlobPattern.Validate` refuses an empty or over-long pattern, one that matches everything, one with spaces or control characters, and an extension that is not letters, digits and inner dots.
4. **What a person chooses is not overridden** (applied in 11b): the dialog's choice, then the remembered site, then the rules.

## Decision (11b)

5. **`NewDownload.CategoryId` of null means "by file type".** `JobService.AddAsync` then runs the resolver with the link, the file name and the MIME type the browser reported (`NewDownload.Mime`), and stores the chosen category and its folder. A named category, and an explicit folder, are never changed. The browser hand-off with the dialog switched off passes the remembered site's category, or null.
6. **The Add dialog** shows the category the rules give the link when it opens, and again as the link or the file name is typed, until the person picks a category by hand or the site was remembered. **The picker** gets a first entry, "By file type", which is its default: each link gets its own category. Naming a category there applies it to all.
7. **Not in 11b:** the one re-resolve after the probe (decision 6 of the plan). It moves where the engine creates the part file, so it is 11b2.

## Decision (11b2)

8. **A job knows whether only the fallback decided its category.** `job.category_auto` (migration `0008`, `Job.CategoryAuto`) is set by `JobService.AddAsync` when no category and no folder were named and the resolver found nothing in the link, the name or the browser's MIME type (`CategoryMatch.IsFallback`). A person's choice, a rule's choice and the browser's MIME type all leave it off.
9. **The engine asks once, after the probe.** For a job with `CategoryAuto` and no saved bytes, `HttpTransferOptions.RouteAfterProbe` calls an `IPostProbeRouter` (Core; the engine does not know categories) with the file name and the `Content-Type` the probe reported, before the part file is created. The app's `CategoryRouter` runs the resolver with that MIME type; if it finds a category other than the fallback it saves the job's category and folder (`UpdateRoutingAsync`, which also clears the mark, so a job is routed once and a resume looks in the right folder) and returns the folder. A router that returns null or throws changes nothing and never fails the download (the job log says "not re-checked").
10. **Not done:** a job whose probe reports a file name (Content-Disposition) is not re-named by it, as before: the dialog's name wins. Only the MIME type is new here.

## Decision (11c)

11. **A Categories tab in Settings that saves each change at once**, so it has no Save button of its own (the window's Save and Cancel apply to the General and Browser tabs). `CategoriesViewModel` lists the categories and, for the chosen one, its rules (kind, pattern, built in or yours, on or off with a check box), with add, save, delete and move up or down. A built-in category keeps its name, takes only a folder under Downloads, and cannot be deleted; its rules can be switched off, not deleted. A user category may take a relative folder or a full path (a drive or a share). Deleting a category deletes its rules and leaves its downloads where they are, with no category.
12. **What may be typed is checked before it is saved** (`CategoryInput`): a name of 1 to 64 characters that no other category has (ignoring case); a folder without `..`, reserved Windows names, characters Windows does not allow or stream syntax; a pattern by `GlobPattern.Validate`. Every message is a resource string.
13. **"Try a link"** runs the resolver on a typed link and says which category and folder it would get and which rule decided, or that nothing matched and General is used.
14. **Moving a rule** swaps its position with the next rule of the same kind shown for that category; rules of other categories keep their own positions.

## Decision (11d)

15. **The Mark-of-the-Web is written on every completed file, with no switch.** `MarkOfTheWeb.TryWrite` puts `[ZoneTransfer]`, `ZoneId=3` and `HostUrl=<address without user name, password, query and fragment>` into the `Zone.Identifier` stream of the sealed part file, after the checksum check and before the rename, in the one finalisation step all three transfer paths (empty file, single stream, segmented) share. The stream travels with the rename. A volume that cannot hold streams (FAT32, exFAT, some shares) is not an error: the job log gets one fixed line, "Mark-of-the-Web not supported on this volume", and the download completes, because it must not fail for a mark the volume cannot hold. A resumed download reaches finalisation once, so it is marked once.

## Consequences

- 11a changes no behaviour a user can see: nothing calls the resolver yet. 11b makes every way of creating a job use it.
- The migration number is the next free one at the time of writing; if the M3 work adds `0006` first, one of the two is renumbered at merge.
