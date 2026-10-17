# Glass-Rea

A personal network monitor for Windows by Tem Revil: per-app internet use, live traffic, connections, firewall blocks, devices on the network, and alerts. The product name is **Glass-Rea**; code identifiers use **GlassRea** (no hyphen). It was called "Glasswire REA" before it went public; don't bring that name or "GlassWire" back anywhere except `Legacy.cs`, which migrates old installs.

## Layout

| Project | What it is |
|---|---|
| `src/GlassRea.Service` | Windows service, runs as SYSTEM, hosted by Microsoft's signed `dotnet.exe`. ETW capture, SRUM import, SQLite at `C:\ProgramData\Glass-Rea\data.db`, firewall rules (group "Glass-Rea"), network scan, limit and alert workers, named pipe `GlassRea`. |
| `src/GlassRea.App` | WinUI 3 app (`GlassRea.exe`), unpackaged, single instance, tray icon. Only talks to the service through the pipe. Per-user files in `%LocalAppData%\Glass-Rea\`. |
| `src/GlassRea.Core` | Shared: database, protocol (`Messages.cs`, `SettingKeys`, `Priority`), alert text, address and number handling. |
| `src/GlassRea.Setup` | Elevated single-file installer and uninstaller with the app and service embedded as `payload.zip`. |
| `tests/GlassRea.Tests` | xUnit tests for Core and the service's pure logic. |

## Build and check

```powershell
dotnet build GlassRea.slnx
dotnet test tests\GlassRea.Tests
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-setup.ps1   # publish\Glass-Rea Setup.exe
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\release.ps1        # GitHub release v<Version>, notes from CHANGELOG
```

Releases live on GitHub. The app's updater (`App/Support/Updater.cs`, `Core/Updates/Releases.cs`) installs only a setup whose SHA-256 matches GitHub's record and whose signer `Releases.AcceptsSigner` accepts: SignPath Foundation with a chain Windows trusts, or the pinned Tem Revil certificate (`Releases.SignerThumbprint`). Releases are made by `.github/workflows/release.yml`, which has SignPath sign Glass-Rea's own files and then the setup built around them; `.signpath/artifact-configurations/` mirrors the configurations kept in SignPath, and README's "Code signing policy" must stay true (a SignPath condition). `scripts/release.ps1` is the fallback from the owner's PC, signed as Tem Revil. Moving to any other signer means first shipping a release, signed by one the installed updater accepts, that accepts the new one. Any new network call the user didn't ask for goes in `PRIVACY.md` and must be possible to turn off (also a SignPath condition).

A change isn't done until the build has no warnings and the tests pass. A new test must be seen failing (revert the fix, watch it go red) before it counts.

## Rules

- **Words on screen:** plain, sentence case, say what happened and what to do. No em dashes. Alert titles and bodies come from `Core/Alerts/AlertText.cs` only, and have tests.
- **Digits:** Latin 0-9 everywhere, stored or shown. Parse user numbers with `Fmt.TryParseNumber` (accepts Arabic-Indic input, stores Latin). No `type="number"`-style inputs for fields typed on an Arabic keyboard.
- **No third-party native code.** Smart App Control refuses unsigned native DLLs, so the tray icon (`Tray.cs`), SQLite (Windows' own `winsqlite3.dll`), the setup folder picker and the window subclass call Windows directly. Keep it that way.
- **Settings:** service-side ones go through `settings` / `settings.set` and must be added to `EditableSettings` and `ServiceState.Defaults`; per-user window choices live in `Support/Preferences.cs`; look and feel in `Support/Appearance.cs`.
- **Setup runs elevated.** Anything it opens for the user (the app, temrevil.com) goes through `explorer.exe` so it runs unelevated. WinUI's FolderPicker doesn't work elevated; use `FolderDialog`.
- **Uninstall leaves nothing blocked:** every firewall rule in the "Glass-Rea" group is removed.
- **Delete what isn't used**, after searching for every reference.

## Checking the UI

The app is native WinUI, so browser tools don't apply. Run the built `GlassRea.exe` and drive it with UI Automation (by element name, toggles by `TogglePattern`), taking window screenshots; check light and dark. Notifications come from `Shell_NotifyIcon`; its balloon icon must be the full icon size or Windows silently refuses the notification.
