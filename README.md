# Launchpad for Windows

A macOS-style app launcher. Press a shortcut and the screen blurs behind a smoothly scrolling grid of rounded,
glossy icons; start typing to filter the apps and groups already shown in Launchpad.

## Run it

Double-click **`build.bat`**: it stops a running copy, cleans, builds to `dist\` and starts Launchpad
(`build.bat test` runs the tests first, `build.bat nolaunch` builds without starting it). Or by hand:

```powershell
./publish.ps1          # builds .\dist\Launchpad.exe (needs the .NET 8 Desktop Runtime; add -SelfContained to bundle it)
.\dist\Launchpad.exe   # first run opens the launcher and initializes the installed-app catalog
```

| Action | How |
|---|---|
| Open / close | **Ctrl+Alt+L** (change it in Settings; if the combo is taken Launchpad picks a free one and tells you) |
| Open with the Windows key (on by default) | Turn it off in Settings → "Windows key opens Launchpad". A lone tap opens/closes Launchpad instead of the Start menu; Win+E, Win+D, Win+Shift+S… are untouched. (Not seen while an elevated window has focus.) |
| Add an item | In File Explorer, right-click a file, shortcut, or folder and choose **Add to Launchpad**. |
| Open / close from another tool | `Launchpad.exe --toggle` (plain `Launchpad.exe` always opens it) |
| Rearrange icons | press an icon and drag it - it lifts immediately, the others slide aside, the grid auto-scrolls at the edges, release to place it. The order is remembered (Settings → Arrange apps: A–Z / Most used / Custom). Dragging empty space scrolls |
| Make a group | drag an icon over the *middle* of another one: the target turns into the group right away so you can see the result. **Let go and it stays; drag away and it cancels.** Drop onto an existing group to add to it. Click a group to open it as a frosted grey square window in the centre (only the glass itself is blurred; the rest of the grid stays sharp, just dimmed): rename it at the top, reorder or launch its apps, and **drag any app out of the window** to take it out and drop it on the grid (click outside or press Esc to close). Right-click a group tile for Rename / Ungroup |
| Scroll the grid | wheel / touchpad (smooth, eased; a touchpad gets a gentler factor than a wheel notch) · ↑ ↓ one row · PgUp/PgDn a screen · Home/End · drag with the mouse (it coasts when you flick) |
| Search | just type to filter Launchpad items by name · **Esc** clears, then closes · **Enter** opens the first match |
| Settings | gear (bottom right) or **Ctrl+,** · tray icon: right-click |

Launch it with `--background` (what "Start with Windows" uses) to stay hidden until the shortcut is pressed.

## How it works

* **App catalogue** – checks the installed application list once at startup. Cached icons and app details paint the
  launcher immediately; apps the user adds are saved and applied directly to the catalogue.
* **Search** – filters the apps and groups already shown in Launchpad by name. Space-separated words must all match;
  results keep the current grid ordering and keyboard arrows move focus through the grid.
* **Blur** – the monitor is captured, shrunk, blurred and graded on the CPU, then crossfaded in. Because the overlay is
  excluded from screen capture it can appear first and grab the desktop afterwards, and it does not depend on Windows'
  "Transparency effects" setting (which is often off).
* **Real apps only** – the Start menu lists far more than people open: admin consoles, developer command prompts, documentation
  links, updaters, diagnostics. A classifier (`AppClassifier`, with tests built from real entries) keeps those out of the grid;
  Settings → "Show system tools and helpers" brings them back, and right-click → Hide removes any single app.
* **Icons** – every app's own icon is re-rendered consistently. Opaque, full-bleed icons fill a rounded glossy tile (shadow,
  rim light, gloss). Icons with transparent areas keep them: the artwork is scaled up and drawn on its own with a soft shadow, no
  tile behind it. A flat-coloured plate with just a tiny logo, or a pale white/grey backing behind a drawing, is removed. Where an opaque plate has transparent corners (a rounded body, a dome), the tile there takes the plate's own colour instead of white. Results are cached on disk.

Data lives in `%LOCALAPPDATA%\Launchpad` (`settings.json`, `icons\`, `apps.json`). Installed apps are checked once at startup; custom items are persisted in settings and added directly to the catalog. Launchpad does not crawl folders or drives.

## Updating

Open **Settings → Check for updates**, or choose **Check for updates…** in the tray menu. When a
build is available, **Update & restart** verifies the GitHub release download, closes Launchpad,
replaces its files in the current folder, and restarts quietly. Your apps, groups, settings and
startup preference are preserved; startup and Explorer integration are refreshed automatically.
The receiving machine needs no Git or GitHub login. The release must be public and the app folder
must be writable.

The updater detects new version tags and replacement ZIPs under the same version. Build-generated
`update-manifest.json` inventories owned files so obsolete binaries can be removed without touching
unlisted personal files. Temporary downloads and backups are removed after success or successful
rollback. A failed startup restores the previous build; interrupted transactions are recovered on
the next launch, retaining backups if recovery fails. Builds predating this feature need one manual
installation of the current release ZIP before future in-app updates are available.

Run `powershell -NoProfile -ExecutionPolicy Bypass -File tools/TestUpdate.ps1` for isolated installer
tests covering success, rollback, locked files and interrupted transactions.

## Developing

```powershell
dotnet test                                                   # 55 tests: index, query language, search, scanner, watcher, icons
dotnet run -c Release --project tools/Bench -- 'C:\'          # scan + search benchmark on a real drive
dotnet run -c Release --project tools/Bench -- --concurrent   # search latency while a scan runs
Launchpad.exe --render-test out.png --query=report --size=1920x1080   # render the UI off-screen to a PNG
$env:LAUNCHPAD_TRACE = 1                                       # timing log: %LOCALAPPDATA%\Launchpad\debug.log
```

Layout: `src/Launchpad.Core` (index, scanner, watcher, query parser, search, icon compositor – no UI dependencies),
`src/Launchpad.App` (WPF shell: window, tray, hot key, app catalog), `tests`, `tools`.

## Limits worth knowing

* The scanner uses plain directory enumeration + change notifications, so it needs no admin rights. Reading the NTFS
  master file table directly would make the very first scan take seconds instead of ~15-45 s, but needs elevation.
* A few apps only provide a ~16 px icon to Windows; those stay small however they are styled.
* Only the monitor under the mouse is covered/blurred.
