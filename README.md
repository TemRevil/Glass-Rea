# Glass-Rea

A personal network monitor for Windows: how much each app used the internet, by day, week or month; live traffic; who each app talks to; blocking apps; the devices on your Wi-Fi; and alerts.

## What it does

| Page | What you see |
|---|---|
| Overview | The top apps for a day, week (starting Saturday) or month, what's moving right now, this month's total and the latest alerts. Older history is imported once from what Windows already keeps (about 30 to 60 days). |
| Apps | Every app that used the internet in the period, with downloaded, uploaded and total. |
| Connections | Per app: the remote hosts it talked to, with their DNS names, bytes in and out, and when last seen. |
| Firewall | Block or unblock any app's internet access in one click (Windows Firewall rules, so blocks hold even when the app is closed). |
| Devices | Devices on your network, named and recognised: the name each gives itself (or one you give it), its maker, what it is (phone, TV, printer and so on) and its system (iOS, Android, Windows...), with IP, MAC, whether the MAC is a randomized private one, and first and last seen. |
| Alerts | An app going online for the first time, a new device joining your network, a device leaving or coming back (optional), 80% and 100% of a daily or monthly limit. Each also shows as a Windows notification with a short title and a plain line on what it means. |
| Settings | In sections. **General:** start with Windows (in the tray or with the window), minimize to the tray, close to the tray, and how much processor priority to ask Windows for (Normal, Above normal, High). **Notifications:** which alerts, whether they pop up, and a test button. **Data:** daily and monthly limits, the day the month and the week start again, VPN tunnel processes. **Appearance:** theme, accent, and the glass itself (frosting, opacity, chromatic aberration, light, shininess, refraction, thickness, edge softness, corner radius and tint). **About:** version, the recording service's state, and updates. |

A live strip of the last two minutes runs along the top of every page.

VPN tunnels (Cloudflare WARP, WireGuard, OpenVPN, Proton, Nord) are listed but left out of totals and the live graph, because the traffic they carry is already counted under the apps that sent it, including apps that reach the VPN through its local proxy or DNS. Limits are different: they count what crossed your internet connection, as your provider does, so with a VPN on they include its overhead (the Overview shows how much).

Devices are recognised from what they say about themselves on the local network: the name they send when they join (DHCP) and their Bonjour, NetBIOS and UPnP answers, plus a few ports only one kind of device keeps open (62078 on iPhones and iPads, 9100 on printers) and the maker registered for their network card. A device that says nothing, such as a phone with a private address and no name, is called by what it is.

## How it works

- **GlassRea.Service** runs as a Windows service (SYSTEM). It reads the kernel's network, process and DNS events (ETW), attributes every packet to an app (svchost is split by the services it hosts; Store apps by package), and stores hourly totals in SQLite at `C:\ProgramData\Glass-Rea\data.db`. It also imports Windows' own usage history (SRUM) for the time before it was installed, manages the firewall rules, and scans the local network every 5 minutes.
- **GlassRea** (the app) is a WinUI 3 window plus tray icon that asks the service for data over a local named pipe. Closing the window keeps it in the tray (Settings > General can make closing quit instead).
- **GlassRea.Core** holds what both share: the database, the protocol, address classification, number parsing.

## Install

Download `Glass-Rea.Setup.exe` from the [latest release](https://github.com/TemRevil/Glass-Rea/releases/latest) and run it (it asks for admin once). It installs to `C:\Program Files\Glass-Rea\` unless you pick another folder on a local drive, installs .NET 10 from Microsoft if it's missing, registers and starts the `GlassRea` service (automatic start, restarts on failure, hosted by Microsoft's signed `dotnet.exe` so Smart App Control allows it), adds the app to Start and to Installed apps, and can start it in the tray when you sign in. When it finishes it can open Glass-Rea and temrevil.com. Running a newer setup updates in place; your history and settings are kept. Settings > About finds newer releases and installs them for you: it checks the download is the file GitHub recorded and that it's signed by Glass-Rea's publisher before running it. Installing over the old "Glasswire REA" moves its history, settings and firewall blocks across and removes the old copy.

Releases are signed by SignPath Foundation (see [Code signing policy](#code-signing-policy)), a certificate Windows trusts, so Smart App Control lets setup and the app run. Releases up to 1.1.1 were signed with Tem Revil's own certificate, which only the PC that made it trusts: SmartScreen warns about those (More info, then Run anyway) and Smart App Control blocks them.

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

- Committers and reviewers: [Tem Revil](https://github.com/TemRevil)
- Approvers: [Tem Revil](https://github.com/TemRevil)

Every signed file is built from this repository by GitHub Actions ([`.github/workflows/release.yml`](.github/workflows/release.yml)) on GitHub's own machines, and each signing request is approved by hand. Only Glass-Rea's own files are signed; everything else in the setup is Microsoft's and signed by Microsoft.

## Privacy

Glass-Rea sends nothing about you or your network anywhere. It asks GitHub once a day whether there's a new version, which you can turn off. See [PRIVACY.md](PRIVACY.md).

## Uninstall

Settings > Apps > Installed apps > Glass-Rea > Uninstall. Choose whether to keep your usage history (kept by default in `C:\ProgramData\Glass-Rea`). Every firewall block the app made is removed either way, so no app stays blocked.

## Develop

```powershell
dotnet build GlassRea.slnx
dotnet test tests\GlassRea.Tests
```

Build the installer with `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-setup.ps1`; it writes `publish\Glass-Rea Setup.exe`. It's signed as Tem Revil once `scripts\make-cert.ps1` has made the certificate on this PC (trusted on this PC only), and unsigned otherwise.

To publish a release, raise `<Version>` in `Directory.Build.props`, give it a `## <version>` section in `CHANGELOG.md`, and push both to main: the **Release** workflow starts by itself (or run it from the Actions tab). It builds and tests on GitHub, has SignPath sign Glass-Rea's files and then the setup (each approved on signpath.io), creates the GitHub release with that section as its notes, and checks GitHub stored the exact file. `scripts\release.ps1` does the same from this PC, signed with the Tem Revil certificate instead.

Every push and pull request runs CI on GitHub: the build with warnings as errors, the tests, and an unsigned setup kept for a week under the run's Artifacts. Dependabot proposes updates to the Actions and NuGet packages weekly.

`scripts\make-icon.ps1` regenerates `src\GlassRea.App\Assets\app.ico` from the Twin flow mark (`design\logo\twin-flow.svg`).
