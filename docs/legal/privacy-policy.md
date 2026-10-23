# Kasi Download Manager: privacy policy

Last updated: 2026-10-23. Applies to the Kasi Download Manager browser extension (Chrome, Microsoft Edge, Firefox) and its use with the Kasi desktop application, both by MartSoft Solutions.

## In short

The extension sends the downloads you choose to the Kasi application on your own computer. It does not send anything to MartSoft Solutions or to any other server, and it has no analytics, no advertising and no remote code.

## What the extension handles, and why

When a download matches the rules you set in Kasi, or when you use one of the extension's right-click items, the extension reads:

- the web address of the file, the page it came from, the file name and type the browser reports, and the browser's user-agent text;
- the cookies the browser would send for that address, so that Kasi can fetch the file with your login.

It passes these only to the Kasi application on the same computer, through the browser's native messaging channel and a local connection that only your Windows account can open. The extension itself does not keep them after the hand-over.

It stores in the browser, on your computer: the rules Kasi last sent (file types, excluded sites, the bypass key), the on/off switch in the popup, and short-lived markers (a hash of a link you clicked with a key held, a mark for form-POST downloads) that are dropped when the browser closes or after seconds.

It does not read your browsing history, page text, form entries or passwords, and it does not track you.

## What Kasi (the desktop application) does with it

Kasi keeps the address, file name and, for a download that needs your login, the cookies, for as long as the download exists in its list. Cookies are stored encrypted with Windows data protection (DPAPI) for your Windows account and are deleted when the download finishes or is removed. Kasi's own logs and crash reports never contain web addresses with query strings, cookies, headers or full folder paths.

## Sharing

None. MartSoft Solutions receives no data from the extension. Data is not sold, transferred to third parties, or used for anything other than downloading the files you choose.

## Your choices

Switch the extension off in its toolbar popup, hold Alt (or your chosen key) while clicking a link to let the browser download it, exclude sites in Kasi's settings, or remove the extension; removing it deletes its stored settings. Delete a download in Kasi to delete its saved cookies.

## Children

The extension is not directed at children and collects no personal information.

## Changes

If this policy changes, the new text is published at the same address with a new date.

## Contact

MartSoft Solutions, Kenya. Contact address: <owner to fill in before the first submission>.
