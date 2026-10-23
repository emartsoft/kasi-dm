# Notes for store reviewers

Paste the text below into each store's "notes to reviewer" box. The early submission is **unlisted** (Chrome Web Store and Edge Add-ons: "unlisted" visibility; AMO: "unlisted"), because the companion desktop app is still in development (plan `docs/plans/m2-7.md`, decision 1). Submit after M2.8b so the app can answer; the notes say what is and is not testable.

---

Kasi Download Manager is the browser companion of the Kasi desktop download manager for Windows (MartSoft Solutions). The desktop app is in development and not yet publicly available, so this listing is unlisted.

Without the desktop app the extension is expected to say "Kasi is not connected" in its popup, and the browser downloads files normally. That is the designed fallback, not a fault.

What you can check without the app
- Click the toolbar icon: the popup shows "Kasi is not connected." and a switch "Send downloads to Kasi".
- Open the extension's options page: it explains private windows and offers "Open Kasi settings" (which also reports not connected).
- Right-click a link, an image, a page and a text selection: the three items "Download with Kasi", "Download all links with Kasi" and "Download selected links with Kasi" appear. With no app running they do nothing and the toolbar badge shows "!".
- Download any `.zip` or `.iso`: because Kasi is not reachable the browser keeps and finishes the download unchanged.

What needs the app
- With the Kasi app and its native messaging host installed, a matching download is paused, handed to Kasi, and the browser's copy is removed. This was verified end to end in Chromium and Firefox in our automated tests.

Permissions are explained one by one in the "Justification" boxes. No remote code is used; there is no analytics and no server of ours is contacted. The Firefox source package builds the add-on with the steps in `amo-build.md`.

Privacy policy: the address entered in the listing (the published `docs/legal/privacy-policy.md`).

---

## Where the privacy policy is hosted

The policy needs a public address before any submission. Until a martsoft.co.ke page exists, use the repository's page for `docs/legal/privacy-policy.md` once the repository is public, or any public copy of the same text. The owner enters that address in all three listings (an owner step, plan section 5).
