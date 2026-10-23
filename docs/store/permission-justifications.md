# Permission justifications

One entry per permission in `extension/manifests/manifest.base.json`, plus the host permission and the content script, each naming the code that uses it. `extension/tests/unit/store-docs.test.ts` fails when a permission is added to or removed from the manifest without a matching `###` heading here. Paste the text of an entry into the store's "justification" box for that permission.

### `downloads`

Kasi takes over the downloads you choose. When a download starts and matches Kasi's rules, the extension pauses it, passes the address to the Kasi app, and once Kasi has accepted it cancels the browser's own copy and removes the file and the entry from the browser's list, so there is one copy, not two. If Kasi does not answer, it resumes the download and the browser finishes it. Code: `src/shared/interception.ts`.

### `cookies`

A download from a site you are logged in to needs the same login. After a download matches, or when you use a right-click item, the extension reads the cookies the browser would send for that address and gives them to the Kasi app on your computer so it can fetch the file with your session. Cookies are read only for addresses you are downloading, are never sent anywhere except to the local Kasi app, and are never written to a log. Code: `src/shared/cookies.ts`.

### `webRequest`

Used without blocking and without reading or changing headers or bodies. It remembers, for a short time, which page and frame requests were not plain GET requests, so that downloads that come from submitting a form (POST) are left to the browser, which is the only thing that can repeat them correctly. Only a one-way hash of the address and the method are kept, for 30 seconds. Code: `src/shared/request-tracker.ts`.

### `nativeMessaging`

The only way to talk to the Kasi desktop app. The extension connects to the host registered as `com.martsoft.kasi`, which passes messages to Kasi over a local named pipe that only your Windows account can open. Nothing goes over the network. Code: `src/shared/native-port.ts`.

### `storage`

Keeps small settings on your computer: the rules Kasi last sent (file types, excluded sites, the bypass key), the on/off switch from the popup, and short-lived hashes (a held-key click, a form-POST mark) in session storage that are dropped when the browser closes. Code: `src/shared/rules-cache.ts`, `src/shared/settings.ts`, `src/shared/request-tracker.ts`, `src/shared/bypass.ts`.

### `contextMenus`

Adds the right-click items "Download with Kasi" (links and media), "Download all links with Kasi" (pages) and "Download selected links with Kasi" (a selection). Code: `src/shared/menus.ts`.

### `scripting`

When you choose "Download all links" or "Download selected links", the extension reads the links from the page you are on (every frame of that tab, once, at your request) and sends them to Kasi so you can pick which to download. It injects a small function that returns the page's link addresses and your selection; it does not change the page. Code: `src/shared/link-collector.ts`, `src/background/index.ts`.

### Host permission `<all_urls>`

A download can come from any site, so reading the cookies for it and noticing the kind of request that started it need access to every site. The extension uses this only for the three purposes above (cookies of a download you are taking, request kind, and your own "download all links" request); it does not read page content except for those links, and it contacts no server.

### Content script (all pages, all frames, at document start)

One small script that listens for clicks and records only whether Alt, Ctrl or Shift was held on a trusted click and on which link (as a hash), so that Alt-click on a link lets the browser download that file itself. It reads no page text, makes no network request and sends the record only to this extension's own background script. Code: `src/content/index.ts`, `src/content/modifier.ts`.

### Remote code

None. All code is in the package; nothing is downloaded or evaluated at run time. The Firefox package declares `data_collection_permissions: required ["none"]` because it transmits nothing to the developer.
