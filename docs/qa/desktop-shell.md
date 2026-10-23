# Desktop shell: manual checklist (tray and toasts)

Not automated: a native tray icon and a real toast need an interactive Windows session, and an unpackaged build has no installer shortcut until M4. The logic behind both (menu actions, speed presets, tooltip, toast routing and text) is covered by `TC-UI-003` and the notification tests in `tests/Kasi.App.Tests`.

| Case | Steps | Expected |
| --- | --- | --- |
| Tray icon | Start `Kasi.App.exe --tray` | An icon appears in the notification area and no window opens; hovering shows "Kasi" |
| Open | Choose Open Kasi, or click the icon | The main window shows |
| Close to tray | Close the main window while a download runs | The window hides, the download continues, the icon stays |
| Speed in the tooltip | Run a download and hover the icon | "Kasi - n MB/s", updating about once a second, back to "Kasi" when nothing runs |
| Pause all / Resume all | Run two downloads; choose Pause all, then Resume all | Both pause, then both continue |
| Speed limit | Choose 1 MB/s while downloading | The speed drops to about 1 MB/s; Unlimited restores it |
| Quit | Choose Quit | The icon disappears and the process ends (check Task Manager); the browser extension shows "not connected" until the next download |
| Completion toast | Let a download finish with Kasi hidden | A toast "Download complete" with the file name only; Open opens the file, Show in folder opens Explorer with the file selected |
| Clipboard capture (off) | With the Settings box off, copy a link ending in .iso | Nothing happens |
| Clipboard capture (on) | Tick "Offer to download a link when I copy one", copy a link ending in .iso in the browser's address bar | The Add download window opens with the link filled in; copying the same link again straight away does not open a second one; copying a sentence or a .html link does nothing |
| Failure toast | Download from a URL that fails | A toast "Download failed" with the file name only |
