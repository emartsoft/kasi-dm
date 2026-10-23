# Browser interception: manual checklist (TC-BRW-001 to 004)

Automated end to end in Playwright's Chromium (stand-in for Chrome and Edge) and Firefox release (`.github/workflows/extension-e2e.yml`, ADR 0019). Run these by hand in each release candidate, in **Chrome, Edge and Firefox** (TPL section 4), with a real Kasi build once M2.8 lands. Not automated: branded Chrome and Edge (they ignore `--load-extension`), private windows, the Firefox toolbar badge, a revoked Firefox host permission.

| Case | Steps | Expected |
| --- | --- | --- |
| TC-BRW-001 | Click a link to a file whose type is in Kasi's list | Kasi takes it (job or add dialog); the browser has no copy and no part file |
| TC-BRW-002 (not running) | Quit Kasi, click such a link | Kasi starts within 5 s and takes the download |
| TC-BRW-002 (missing) | Rename `Kasi.exe` next to the host, click such a link | The browser finishes its own download; the popup says Kasi is not connected; Chrome and Edge show a red `!` badge |
| TC-BRW-003 | Alt-click (then the configured key if changed) such a link | The browser keeps the download; Kasi hears nothing |
| TC-BRW-004 | Download a `blob:` file, a `data:` file, a file from a POST form, a file from an excluded site | The browser keeps each one |
| TC-BRW-004 (private) | Allow the extension in private windows, then download there; then switch that off and repeat | Taken only when allowed |
| TC-BRW-005 (menus) | Right-click a link, an image, a page and a text selection with links on a page of about 40 links | The three menu items appear in the right places; "Download with Kasi" on a link, "Download all links" and "Download selected links" each reach Kasi (a download, or one batch with the 40 or the selected links); the browser downloads nothing itself |
| Refresh link | Start a download from a page whose link expires (a signed URL), pause it, wait for the link to expire, resume; when Kasi shows the refresh window click "Open the page and refresh" | The browser opens the page in a background tab, which closes by itself; the window says the link was refreshed and the download continues from where it stopped |
| Firefox permission | Remove "Access your data for all websites" from the add-on, click such a link | The popup shows "needs permission" and the browser downloads normally |
