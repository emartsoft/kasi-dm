# Data usage answers

The answers to give in each store's privacy questionnaire. They follow `docs/legal/privacy-policy.md` and `permission-justifications.md`; change all three together.

## What the extension handles

| Data | Handled? | Where it goes | Kept? |
| --- | --- | --- | --- |
| Web addresses of downloads you choose (and the page they came from) | Yes | Only to the Kasi app on your computer | Not by the extension; Kasi keeps them for the download |
| Cookies for those addresses | Yes | Only to the Kasi app on your computer | Not by the extension; Kasi keeps them encrypted with the job and deletes them when the job is gone |
| File name, type and size reported by the browser | Yes | Only to the Kasi app on your computer | Not by the extension |
| Browsing history, page content, form data, keystrokes, location, personal identifiers | No | - | - |
| Settings (rules from Kasi, the on/off switch) | Yes | Stored in the browser's extension storage on your computer | Until you remove the extension |

## Chrome Web Store: "Privacy practices" tab

- Single purpose: see `listing/en-US.md`.
- Data types collected: **Web history**: no. **Website content**: no. **Authentication information**: no (cookies are passed to the local app the user installed, not collected by the developer). **Personally identifiable information**, **health**, **financial**, **location**, **user activity**: no.
- Certifications: the data is not sold or transferred to third parties; not used for purposes unrelated to the single purpose; not used for creditworthiness or lending.
- Remote code: **No**.

If the dashboard's wording forces a choice for cookies and addresses, say they are processed on the device and sent only to the user's own installed companion application, and are not transmitted to the developer.

## Microsoft Edge Add-ons

The same answers as Chrome. Edge asks for a privacy policy URL as well.

## Firefox (AMO)

- The manifest declares `data_collection_permissions: { required: ["none"] }`: the extension collects and transmits nothing to the developer or any third party. Tick "no data collection" where the form asks.
- Source code: upload the source package built by `npm run package` with `amo-build.md` (the extension is built with Vite from TypeScript).
