# Glass-Rea

A personal network monitor for Windows by Tem Revil: per-app internet use, live traffic, connections, firewall blocks, devices on the network, and alerts. The product name is **Glass-Rea**; code identifiers use **GlassRea** (no hyphen). It was called "Glasswire REA" before it went public; don't bring that name or "GlassWire" back anywhere except `Legacy.cs`, which migrates old installs.

## Layout

| Project | What it is |
|---|---|
| `src/GlassRea.Service` | Windows service, runs as SYSTEM, hosted by Microsoft's signed `dotnet.exe`. ETW capture, SRUM import, SQLite at `C:\ProgramData\Glass-Rea\data.db`, firewall rules (group "Glass-Rea"), network scan, limit and alert workers, named pipe `GlassRea`. |
| `src/GlassRea.App` | WinUI 3 app (`GlassRea.exe`), unpackaged, single instance, tray icon. Only talks to the service through the pipe. Per-user files in `%LocalAppData%\Glass-Rea\`. `Store/Package.appxmanifest` makes it, with the service, the Microsoft Store package (below). |
| `src/GlassRea.Core` | Shared: database, protocol (`Messages.cs`, `SettingKeys`, `Priority`), alert text, address and number handling. |
| `src/GlassRea.Setup` | Elevated single-file installer with the app, the service and `Uninstall Glass-Rea.exe` embedded as `payload.zip`. The uninstaller is this project built with `-p:Uninstaller=true` (no payload), so the install folder holds one copy of the app, not two; `build-setup.ps1` builds it into the stage. |
| `tests/GlassRea.Tests` | xUnit tests for Core and the service's pure logic. |

**The Microsoft Store package** (`scripts/build-store.ps1`, identity `TemRevil.Glass-Rea` from Partner Center) is the same code. `Core/Paths/Packaging.cs` tells the copies apart at run time (`IsPackaged`), and when packaged: the service is `GlassReaStore`, runs as a self-contained `Service\GlassRea.Service.exe` and writes no event log (Windows doesn't let a packaged service change its own restart-on-failure); blocks are WFP filters held in a dynamic session (`Service/Blocking/WfpBlocker.cs`, struct layouts checked in `WfpTests`), listed in `apps.blocked` and added again at every start (`FirewallManager.RestoreBlocks`), because an MSIX can't run anything when it's removed; a program whose file isn't there (an unplugged drive) can't be named to the engine, so its block waits and is retried every 30 seconds; the app starts at sign-in through its startup task (Settings > General), doesn't look for updates, and offers to remove setup's copy (`Support/OtherCopy.cs`). While setup's copy (service `GlassRea`) is installed, the Store service only stands by (`Service/Standby.cs`): it notes setup's copy's firewall blocks each minute while that runs, and once it's gone marks them blocked and starts the recording host in the same process. Setup refuses to install beside the Store copy, and never closes its window. `scripts/sandbox/test-store.ps1` tests all of this in Windows Sandbox.

`README.md` is for people; `INSTALL.md` walks an AI assistant through installing Glass-Rea for someone (checks, official download and verification, setup's screens, troubleshooting). When setup's screens, requirements, paths or signing change, update both.

The lists show one row per app: `Core/Usage/AppKinds.cs` tells apps, Store apps, services, Windows, VPN tunnels and unknown programs apart and adds an app's rows together (each version, Windows' history rows, each sign-in's copy of a service); `App/Support/AppFaces.cs` gives each its name, icon and kind label from Windows (services from the registry, packages from `PackageManager`); `App/Support/AppIcons.cs` draws icons one at a time on its own thread, because the shell answers "pending" to several at once.

Use is kept per network: `usage` and `hosts` rows carry a `network` (an id in `networks`, 0 for unknown) in their key, and `Service/Networks/NetworkWatcher.cs` sets `UsageAccumulator.Network` from `NetworkList.Current()`, the network of the physical adapter traffic goes out on (never a VPN's, so a VPN's use counts on the network under it). A Wi-Fi is keyed by its SSID (`wifi:<ssid>`, from its Network List Manager profile's `Description`), so Windows' "Home 2" profiles merge into "Home"; wired and mobile networks are keyed by profile GUID. At start the watcher rekeys profile-keyed Wi-Fi rows (`Database.Rekey`, which merges usage and hosts). The app's network switch sits in the floating bar (`Controls/Bits.cs` `NetworkSwitch`, `Support/Service.cs` `Networks`), one choice for every page. Requests take `Network` (null for all networks, the default); limits, alerts and the month card always count every network together. Older rows were moved to network 0 by a table rebuild, and `SrumImporter` places earlier use on Wi-Fi networks (`srum_networks` = `wifi-by-name`; a rerun adds only what isn't placed yet) from SRUM's `L2ProfileId`, mapped to the WLAN profile's SSID, never changing the all-networks total (`Core/Usage/HistoryNetworks.cs`).

## Build and check

```powershell
dotnet build GlassRea.slnx
dotnet test tests\GlassRea.Tests
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-setup.ps1   # publish\Glass-Rea Setup.exe
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\release.ps1        # GitHub release v<Version>, notes from CHANGELOG
```

Releases live on GitHub. The app's updater (`App/Support/Updater.cs`, `Core/Updates/Releases.cs`) installs only a setup whose SHA-256 matches GitHub's record and whose signer `Releases.AcceptsSigner` accepts: SignPath Foundation with a chain Windows trusts on a file whose ProductName is `Releases.Product` (SignPath signs many projects), or the pinned Tem Revil certificate (`Releases.SignerThumbprint`). Releases are made by `.github/workflows/release.yml`, which has SignPath sign Glass-Rea's own files and then the setup built around them; `.signpath/artifact-configurations/` mirrors the configurations kept in SignPath, and README's "Code signing policy" must stay true (a SignPath condition). `scripts/release.ps1` is the fallback from the owner's PC, signed as Tem Revil. Moving to any other signer means first shipping a release, signed by one the installed updater accepts, that accepts the new one. Any new network call the user didn't ask for goes in `PRIVACY.md` and must be possible to turn off (also a SignPath condition).

Only the Windows App SDK parts in use are referenced (WinUI, Foundation, InteractiveExperiences), not the whole SDK, and `Directory.Build.targets` keeps WinUI's text to English. `.signpath/artifact-configurations/payload.xml` lists the uninstaller too; SignPath's own copy must match before a release.

A change isn't done until the build has no warnings and the tests pass. A new test must be seen failing (revert the fix, watch it go red) before it counts.

## Logs

When asked to look at the logs, read them here (or in the .zip the user sends, from Settings > About > Logs: `app/`, `service/`, `about.txt`):

- App: `%LocalAppData%\Glass-Rea\Logs\app.log` (`Support/Log.cs`). Crashes (caught by the app, plus Windows' own crash and hang reports for Glass-Rea, copied in at start), errors, the window not responding for 0.5 s or more (`FREEZE`, with the page), pages and service calls over 1.5 s (`SLOW`), the service going away and coming back.
- Service: `C:\ProgramData\Glass-Rea\Logs\service.log` (`FileLogger.cs`, beside the event log). Everything it logs at Information and up, failed requests with their stack, requests over 1 s. Its folder is reset to SYSTEM/administrators-only at start, since anyone may add files under ProgramData.

One line each: `time zone LEVEL source: message`, an exception's lines indented under it. Files roll over at 1 MB, keeping four old ones (`Core/Logs/LogFile.cs`). Before fixing a FREEZE or SLOW, measure where the time goes; don't guess.

## Rules

- **Words on screen:** plain, sentence case, say what happened and what to do. No em dashes. Alert titles and bodies come from `Core/Alerts/AlertText.cs` only, and have tests.
- **Digits:** Latin 0-9 everywhere, stored or shown. Parse user numbers with `Fmt.TryParseNumber` (accepts Arabic-Indic input, stores Latin). No `type="number"`-style inputs for fields typed on an Arabic keyboard.
- **No third-party native code.** Smart App Control refuses unsigned native DLLs, so the tray icon (`Tray.cs`), SQLite (Windows' own `winsqlite3.dll`), the setup folder picker and the window subclass call Windows directly. Keep it that way.
- **Settings:** service-side ones go through `settings` / `settings.set` and must be added to `EditableSettings` and `ServiceState.Defaults`; per-user window choices live in `Support/Preferences.cs`; look and feel in `Support/Appearance.cs`.
- **Setup runs elevated.** Anything it opens for the user (the app, temrevil.com) goes through `explorer.exe` so it runs unelevated. WinUI's FolderPicker doesn't work elevated; use `FolderDialog`.
- **Uninstall leaves nothing blocked:** setup removes every firewall rule in the "Glass-Rea" group; the Store copy's blocks end with its service.
- **Delete what isn't used**, after searching for every reference.

## Performance

The app has to stay light on low-end PCs. Most of its processor time is Windows' compositor thread inside the app, so:

- No animation that runs forever. A glass with refraction bands (`Glass.Refracts`, on by default) re-renders its blur once per band each frame its backdrop changes; glass over something that changes every second (the rate chip, the hover readouts) sets `Refracts = false`.
- The live graph (`LiveGraph` in `Controls/Charts.cs`) is drawn once per sample, one step wider than the strip; the compositor slides it one device pixel at a time (step easing). Don't redraw it per frame.
- Animations that must start when seen (the usage chart's grow) count on the frame clock while the control is on screen, not on wall time.
- Lists that can be long use a virtualized `ListView` (no `StackPanel` items panel); a recycled row's handlers read the row from `DataContext`.
- Page changes (`Motion.SwapPage`): the floating bar's glass blurs `Stage` (the page and its fading copies), and lets its refraction go while the page moves (`Motion.BackdropMoving`), since every band redrawing every frame ran the change at about 10 fps. Building or re-adding a page holds the UI thread and the compositor with it, so nothing may be moving then: the tab pill (`Controls/TabLens.cs`, a composition shape) slides first, the page is built once it has all but arrived (`TabLens.Arrives`), and the fade starts only once frames are steady again (`Motion.Rendered`). A page's first open still holds about 80 ms as its fade starts.
- No forever animation even when stepped or tiny: any running animation keeps the compositor producing frames, and every frame redraws every glass blur (a breathing "Live" dot did this). Timers that wait, such as `SmoothWidth`'s shrink delay, use a `DispatcherQueueTimer`, not `CompositionTarget.Rendering`.
- `Glass.Track` assigns the blur's `SourceOffset`/`SourceSize` only when they change; assigning them, even to the same value, redraws the blur.
- `Appearance.Solid` (Windows' transparency effects off, or **Ultra performance** on) drops the glass's source visual entirely, so it copies and blurs nothing; Ultra also swaps the window's acrylic for a plain colour and turns `Motion.Enabled` off. New glass or motion code must honour both.
- Hidden in the tray, the app trims its working set (`App.TrimMemorySoon`, `SetProcessWorkingSetSize(-1, -1)`): WinUI keeps its native memory for the life of the process, so closing the window frees almost nothing on its own.
- The window does no work while minimised. Measure (CPU over 20 s after 20 s settling, or `dotnet-trace`) before and after; don't guess.

## Checking the UI

The app is native WinUI, so browser tools don't apply. Run the built `GlassRea.exe` and drive it with UI Automation (by element name, toggles by `TogglePattern`), taking window screenshots; check light and dark. For motion, capture the screen region frame by frame around a real mouse click (an automation Invoke also gives the button a focus ring) and diff the frames: a run of unchanged frames mid-animation is a stall. Notifications come from `Shell_NotifyIcon`; its balloon icon must be the full icon size or Windows silently refuses the notification.
