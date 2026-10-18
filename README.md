<div align="center">

<img src="design/logo/twin-flow-256.png" alt="Glass-Rea logo" width="112" />

# Glass-Rea

**See what's using your internet, and stop it.**<br/>
A free, private network monitor for Windows, made of glass.

[![Latest release](https://img.shields.io/github/v/release/TemRevil/Glass-Rea?style=flat-square&label=release&color=C46532)](https://github.com/TemRevil/Glass-Rea/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/TemRevil/Glass-Rea/total?style=flat-square&color=C46532)](https://github.com/TemRevil/Glass-Rea/releases)
[![CI](https://img.shields.io/github/actions/workflow/status/TemRevil/Glass-Rea/ci.yml?branch=main&style=flat-square&label=CI)](https://github.com/TemRevil/Glass-Rea/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue?style=flat-square)](LICENSE)
<br/>
![Windows 10 | 11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?style=flat-square&logo=windows&logoColor=white)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet&logoColor=white)
![WinUI 3](https://img.shields.io/badge/WinUI-3-0078D4?style=flat-square)
![C#](https://img.shields.io/badge/C%23-239120?style=flat-square&logo=csharp&logoColor=white)

[📥 **Download**](https://github.com/TemRevil/Glass-Rea/releases/latest) · [🤖 Install guide for AI agents](INSTALL.md) · [📝 Changelog](CHANGELOG.md) · [🔒 Privacy](PRIVACY.md)

</div>

---

## 📥 Install

1. **Download** `Glass-Rea.Setup.exe` from the [latest release](https://github.com/TemRevil/Glass-Rea/releases/latest).
2. **Run it** and approve the admin prompt. If Windows says *"Windows protected your PC"*, click **More info**, check the publisher is **Tem Revil**, then **Run anyway**.
3. Click **Install**, then **Finish**. Glass-Rea opens and stays in the tray by the clock.

> [!TIP]
> **Using an AI assistant?** Ask it to *"install Glass-Rea using https://github.com/TemRevil/Glass-Rea/blob/main/INSTALL.md"*. That guide walks it through checking your PC, downloading from the official release only, verifying the file, and the setup steps.

> [!IMPORTANT]
> Releases are signed with the developer's own certificate for now, which Windows doesn't know yet. If **Smart App Control** is on, Windows blocks setup and there's no way to allow just this one app. Turning Smart App Control off can't be undone without resetting Windows, so think it over. Signing by SignPath Foundation, which Windows trusts, is being set up.

<details>
<summary><b>What setup does</b></summary>
<br/>

- Installs to `C:\Program Files\Glass-Rea\` unless you pick another folder on a local drive.
- Installs .NET 10 from Microsoft if it's missing.
- Registers and starts the `GlassRea` service (starts with Windows, restarts if it fails).
- Adds Glass-Rea to Start and to Installed apps, and can start it in the tray when you sign in.
- Running a newer setup updates in place; your history, settings and blocks are kept.
- Installing over the old "Glasswire REA" moves its history, settings and firewall blocks across and removes the old copy.

**Needs:** see [Requirements](#-requirements) below.

</details>

---

## 💻 Requirements

| | Minimum | Recommended |
| :--- | :--- | :--- |
| 🪟 **Windows** | Windows 10 version 2004 (build 19041) | Windows 11 |
| 🧠 **Processor** | Any 64-bit Intel or AMD dual-core | Quad-core, Intel 6th generation / AMD Ryzen or newer |
| 💾 **Memory** | 4 GB | 8 GB or more |
| 🖥️ **Screen** | 1280 × 800 at 100% scaling (the window is at least 980 × 680) | 1920 × 1080 at 100 or 125% |
| 📦 **Disk** | About 400 MB free during setup, 170 MB once installed | The same; history adds only a few MB, since it keeps hourly totals |
| 🌐 **Other** | An administrator account to install; internet during setup if .NET 10 isn't there | Transparency effects on in Windows, for the glass |

ARM PCs (such as Snapdragon laptops) aren't supported.

<details>
<summary><b>📈 What it uses, measured</b></summary>
<br/>

On an Intel Core i7-6700 (4 cores, 8 threads, from 2015) with 16 GB, Windows 11, Overview page open, averaged over repeated runs:

| State | Processor (share of one core) | Memory |
| :--- | :--- | :--- |
| Window open, glass on | about 14% (under 2% of the whole processor) | 150 to 270 MB, more after visiting every page |
| Window open, **Ultra performance** on | about 4% | the same |
| In the tray | under 1% | about 20 to 40 MB in use; Windows can reclaim the rest |
| Recording service (always running) | under 0.5% | about 45 MB |

The glass is the expensive part: it copies and blurs what's behind it every frame something moves. **Settings > General > Ultra performance** turns it solid, makes the window plain and turns animations off, for PCs where every bit of processor counts. With Windows' transparency effects off, the glass is solid anyway.

</details>

---

## ✨ What it does

| | Page | What you see |
| :---: | :--- | :--- |
| 📊 | **Overview** | The top apps for a day, week (starting Saturday) or month, on one network or all of them, what's moving right now, this month's total and the latest alerts. Older history is imported once from what Windows already keeps (about 30 to 60 days). |
| 📱 | **Apps** | Every app that used the internet in the period, on one network or all of them, with downloaded, uploaded and total. |
| 🌐 | **Connections** | Per app: the remote hosts it talked to, with their DNS names, bytes in and out, and when last seen, on one network or all of them. |
| 🛡️ | **Firewall** | Block or unblock any app's internet access in one click. They're Windows Firewall rules, so blocks hold even when the app is closed. |
| 📡 | **Devices** | Devices on your network, named and recognised: the name each gives itself (or one you give it), its maker, what it is (phone, TV, printer...) and its system (iOS, Android, Windows...), with IP, MAC, whether the MAC is a randomized private one, and first and last seen. |
| 🔔 | **Alerts** | An app going online for the first time, a new device joining your network, a device leaving or coming back (optional), and 80% and 100% of a daily or monthly limit. Each also pops up as a Windows notification. |
| ⚙️ | **Settings** | **General** (startup, tray, Ultra performance, priority), **Notifications**, **Data** (limits, when the month and week start, VPN tunnels, your networks and their names), **Appearance** (theme, accent, and the glass itself: frosting, opacity, chromatic aberration, light, shininess, refraction, thickness, edge softness, corner radius, tint) and **About** (version, service state, updates, logs). |

A live strip of the last two minutes runs along the top of every page.

<details>
<summary><b>🔐 How VPNs are counted</b></summary>
<br/>

VPN tunnels (Cloudflare WARP, WireGuard, OpenVPN, Proton, Nord) are listed but left out of totals and the live graph, because the traffic they carry is already counted under the apps that sent it, including apps that reach the VPN through its local proxy or DNS.

Limits are different: they count what crossed your internet connection, as your provider does, so with a VPN on they include its overhead (the Overview shows how much).

</details>

<details>
<summary><b>📶 How networks are told apart</b></summary>
<br/>

Each hour's use is kept with the network it went over. A Wi-Fi goes by its own name, hidden or not, so it stays one network even when Windows remembers it twice ("Home" and "Home 2", as it does when the same Wi-Fi comes from a new router or another access point). Wired and mobile networks go by the name Windows gives them ("Network 2"). You can rename any of them. A VPN isn't a network of its own: its use counts on the Wi-Fi or cable it runs over.

The network switch in the floating bar, next to the light and dark button, picks the network every page counts: all of them, or one. It shows once there's more than one network, and keeps your choice.

History imported from Windows is placed on its Wi-Fi network where Windows recorded which one. Use it can't place, such as over a cable before Glass-Rea counted per network, is shown as "Unknown network". Limits always count every network together.

</details>

<details>
<summary><b>📡 How devices are recognised</b></summary>
<br/>

From what they say about themselves on the local network: the name they send when they join (DHCP), their Bonjour, NetBIOS and UPnP answers, a few ports only one kind of device keeps open (62078 on iPhones and iPads, 9100 on printers), and the maker registered for their network card. A device that says nothing, such as a phone with a private address and no name, is called by what it is.

</details>

---

## 🔒 Privacy

> [!NOTE]
> Glass-Rea sends nothing about you or your network anywhere. It asks GitHub once a day whether there's a new version, which you can turn off. See [PRIVACY.md](PRIVACY.md).

---

## 🔄 Updates

**Settings > About** finds newer releases and installs them for you. Before running one it checks the download is exactly the file GitHub recorded and that it's signed by Glass-Rea's publisher.

## 🗑️ Uninstall

**Settings > Apps > Installed apps > Glass-Rea > Uninstall.** Choose whether to keep your usage history (kept by default in `C:\ProgramData\Glass-Rea`). Every firewall block the app made is removed either way, so no app stays blocked.

---

## 🧩 How it works

```text
 ┌──────────────────────────┐   named pipe    ┌───────────────────────────────┐
 │  GlassRea (the app)      │ ◄─────────────► │  GlassRea.Service  (SYSTEM)   │
 │  WinUI 3 window + tray   │                 │  ETW network, process and DNS │
 └──────────────────────────┘                 │  events → hourly totals       │
                                              └──────────────┬────────────────┘
                                                             ▼
                                         C:\ProgramData\Glass-Rea\data.db (SQLite)
```

| Part | Role |
| :--- | :--- |
| ⚙️ **GlassRea.Service** | A Windows service (SYSTEM). Reads the kernel's network, process and DNS events (ETW), attributes every packet to an app (svchost split by the services it hosts, Store apps by package) and stores hourly totals in SQLite. Imports Windows' own usage history (SRUM) for the time before it was installed, manages the firewall rules, and scans the local network every 5 minutes. |
| 🪟 **GlassRea** | The app: a WinUI 3 window plus tray icon that asks the service for data over a local named pipe. Closing the window keeps it in the tray. |
| 📦 **GlassRea.Core** | What both share: the database, the protocol, address classification, number parsing. |
| 💿 **GlassRea.Setup** | The installer and uninstaller, one signed exe. |

### 🧰 Tech stack

| Domain | Technology |
| :--- | :--- |
| **Language** | C# on [.NET 10](https://dotnet.microsoft.com/) |
| **App** | [WinUI 3](https://learn.microsoft.com/windows/apps/winui/winui3/) (Windows App SDK), [Win2D](https://github.com/microsoft/Win2D) for the charts and the glass |
| **Capture** | Event Tracing for Windows (kernel network, process and DNS providers) |
| **Storage** | SQLite through Windows' own `winsqlite3.dll` |
| **Blocking** | Windows Firewall rules |
| **Tests** | xUnit, run on every push by GitHub Actions |

---

<details>
<summary><b>🛠️ Develop</b></summary>
<br/>

```powershell
dotnet build GlassRea.slnx
dotnet test tests\GlassRea.Tests
```

Build the installer with `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-setup.ps1`; it writes `publish\Glass-Rea Setup.exe`. It's signed as Tem Revil once `scripts\make-cert.ps1` has made the certificate on this PC (trusted on this PC only), and unsigned otherwise.

To publish a release, raise `<Version>` in `Directory.Build.props`, give it a `## <version>` section in `CHANGELOG.md`, and push both to main: the **Release** workflow starts by itself (or run it from the Actions tab). It builds and tests on GitHub, has SignPath sign Glass-Rea's files and then the setup (each approved on signpath.io), creates the GitHub release with that section as its notes, and checks GitHub stored the exact file. `scripts\release.ps1` does the same from this PC, signed with the Tem Revil certificate instead.

Every push and pull request runs CI on GitHub: the build with warnings as errors, the tests, and an unsigned setup kept for a week under the run's Artifacts. Dependabot proposes updates to the Actions and NuGet packages weekly.

`scripts\make-icon.ps1` regenerates `src\GlassRea.App\Assets\app.ico` from the Twin flow mark (`design\logo\twin-flow.svg`).

See [CONTRIBUTING.md](CONTRIBUTING.md) and [AGENTS.md](AGENTS.md) for how the code is organised.

</details>

## ✍️ Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

- Committers and reviewers: [Tem Revil](https://github.com/TemRevil)
- Approvers: [Tem Revil](https://github.com/TemRevil)

Every signed file is built from this repository by GitHub Actions ([`.github/workflows/release.yml`](.github/workflows/release.yml)) on GitHub's own machines, and each signing request is approved by hand. Only Glass-Rea's own files are signed; everything else in the setup is Microsoft's and signed by Microsoft. Until SignPath signing is live, releases are signed with Tem Revil's own certificate.

## 📄 License

[MIT](LICENSE). Third-party notices: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Report a security problem: [SECURITY.md](SECURITY.md).

---

<div align="center">

Built by [Tem Revil](https://github.com/TemRevil), with Claude as coding assistant.<br/>
**[temrevil.com](https://temrevil.com)** · **[Report a problem](https://github.com/TemRevil/Glass-Rea/issues)**

</div>
