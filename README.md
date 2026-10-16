# Unread Reader for Windows

A native **WinUI 3 / C#** RSS reader inspired by [Unread's desktop app](https://www.goldenhillsoftware.com/unread/screenshots/mac/). Read RSS and Atom feeds locally, import OPML, and save individual article links. Feedly is an optional syncing service. This is an independent implementation and is not affiliated with Golden Hill Software or Feedly.

## Run

On Windows 10 version 1809 or later (Windows 11 recommended), open PowerShell in this folder:

```powershell
.\scripts\Build.ps1
```

The script uses your .NET SDK, or downloads .NET 10 into the gitignored `.tools` folder if it is missing. No Visual Studio installation is required. First-time restore/build needs internet access. Open `Unread.sln` in a current Visual Studio with WinUI tools if you prefer an IDE.

```powershell
.\scripts\Build.ps1 -Task Build
.\scripts\Build.ps1 -Task Test
.\scripts\Build.ps1 -Task Publish
```

Publishing produces a self-contained x64 application in `artifacts/Unread-win-x64`. Run `Unread.App.exe` from that folder; keep its companion files together. Use `-Architecture ARM64` to build for ARM64 (not locally runtime-tested). The project targets .NET 10 and Windows App SDK 2.5.1.

## Windows packages

Every push to any branch or tag runs [Windows packages](https://github.com/0xruth1ezz/unread-rss/actions/workflows/windows-package.yml). Open a successful run and download its **Unread-Windows-x64** artifact: it contains a per-user `.exe` installer, a portable ZIP, and SHA-256 checksums. Packages are retained for 14 days. The installer adds a Start menu shortcut; uninstalling preserves your feeds and settings. These development packages are unsigned.

The workflow runs tests, publishes the self-contained application, builds the installer with Inno Setup on the Windows runner, and checks silent install/uninstall. NuGet downloads are cached by project configuration, with partial-cache reuse when dependencies change. Build intermediates and packages are rebuilt on every push.

To create the same packages locally, install [Inno Setup](https://jrsoftware.org/isinfo.php) 6.3 or newer, then run:

```powershell
.\scripts\Package.ps1 -Version 1.0.0
# If the compiler is installed elsewhere:
.\scripts\Package.ps1 -Version 1.0.0 -IsccPath 'C:\Tools\Inno Setup\ISCC.exe'
```

Outputs are written to `artifacts/packages`. This repository contains source, required runtime assets and licenses; generated packages, SDKs, caches and local design/review records are excluded.

## Local feeds, OPML and article links

The app opens your **Local library**, with no account required. Use **+ (Add to library)** in the top toolbar:

- **Add feed or website…** accepts an RSS/Atom URL, or discovers feeds advertised by a website. Choose a feed if the site publishes several. An optional folder supports paths such as `Reading / Technology`.
- **Save article link…** saves a web page to **Saved Articles**. Pages with readable HTML also get a local text copy; some sites only expose a preview. Manually saved pages also appear under **Saved Links**. Adding an existing link bookmarks it without making another copy.
- **Import OPML…** opens an `.opml` or `.xml` subscription list. Nested folders are retained; duplicate feed URLs are merged. Invalid URLs are skipped. Subscriptions are saved before fetching, so an unavailable feed remains available for a later retry.
- **Export OPML…** writes subscriptions and folder paths to an OPML 2.0 file. Saved article links and article text are not part of OPML.

**Refresh** checks all your local feeds. Read status and bookmarks survive refreshes and app restarts. Right-click a feed to remove it; saved articles are kept. Local feeds, saved links and read state are stored in `%LOCALAPPDATA%\UnreadReader\Local`, separately from Feedly. OPML imports are limited to 10 MB; individual feed/page downloads to 8 MB. RSS 2.0, RSS 1.0/RDF and Atom 1.0 are supported.

## Feedly connection

Click **Local library** at the bottom of the sidebar, choose **Feedly account…**, and paste a valid Feedly access token. A token is verified against `/v3/profile` before it is saved. Tokens are encrypted using Windows DPAPI for the current Windows user and are never written to the cache or source tree. The same menu switches back to your local library or opens an isolated demo. Adding/importing local content switches to the local library.

Feedly controls API access. See the [official authorization documentation](https://developers.feedly.com/reference/authorization). Current self-service tokens are limited to eligible accounts; a normal Feedly login is not itself an API token. This version implements **token authentication**, not a browser OAuth flow. An approved Feedly OAuth registration can be integrated separately if needed. No credentials are bundled or required to explore the demo.

Subscriptions, folders, unread counts, articles, read/unread changes and Saved for Later sync with Feedly. **Sync** refreshes the current stream; **Load more** follows Feedly's continuation cursor. Each request loads 50 articles. Search covers locally loaded article titles, sources and text, and is explicitly labeled that way.

Read/save changes are persisted before transmission and queued when offline. Sync replays pending changes before refreshing server state. Disconnect clears the local account library and credentials; it asks before discarding unsynced actions. The demo is held in memory and never modifies Feedly.

## Reading

- Three panes with collapsible folders and a focused reading view.
- Native selectable article text, headings, images, emphasis, quotations and web links. Article content never executes scripts.
- Paper, Dusk and Campfire themes; adjustable text size and optional mark-as-read on open.
- Compact windows show the feed and article lists, then open the reader with a Back button.
- Click an article's title to open its original source in your default browser, or copy its link from the toolbar.

| Key | Action |
| --- | --- |
| Ctrl+N | Add a feed or website |
| Ctrl+O | Import OPML |
| Ctrl+R | Refresh feeds / sync Feedly |
| Ctrl+F | Search loaded articles |
| J / K | Next / previous article |
| M | Toggle read status |
| S | Toggle Saved for Later |
| F11 | Toggle focused reading |
| Escape | Leave focused/compact reading |

Single-letter shortcuts are suppressed while typing. Settings, credentials and article caches live in `%LOCALAPPDATA%\UnreadReader`. The cache retains the newest 2,000 fetched articles plus bookmarks; manually saved links are retained independently of this limit. Feedly also preserves articles with pending changes. Cached article text remains available offline; remote images are not predownloaded. Saved webpage extraction uses the page's accessible HTML; it does not run JavaScript or bypass sign-in/paywalls. Embedded media and periodic background sync are not implemented.

## Structure and verification

- `src/Unread.App`: native XAML interface, article rendering, account UI, DPAPI credentials.
- `src/Unread.Core`: RSS/Atom parsing, website feed discovery, article extraction, OPML import/export, Feedly client, atomic cache storage and persistent mutation queue.
- `tests/Unread.Core.Tests`: local subscription/OPML round trips, URL validation, RSS/Atom content, discovery, article extraction, read/bookmark preservation, persistence, plus Feedly request and offline-queue checks.

Tests use an in-memory HTTP handler; no real Feedly account is contacted. A live account sync has not been verified without user credentials. Use `Unread.App.exe --demo` to force demo mode without changing an existing account connection, and `--compact` to open a 900-pixel-wide window. Developers can use `--data-dir <absolute-folder>` for an isolated library during UI verification.

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for fonts and demo photo sources.
