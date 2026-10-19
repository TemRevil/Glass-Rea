# 🤖 Installing Glass-Rea: a guide for AI agents

> [!NOTE]
> This file is for AI assistants (ChatGPT, Claude, Copilot, Gemini and others) helping someone install Glass-Rea on their PC. People can follow it too; the short version for people is in the [README](README.md#-install).

Glass-Rea is a free, open-source network monitor for Windows: data use per app, live traffic, who each app talks to, one-click blocking, the devices on your Wi-Fi, and alerts. Everything stays on the PC.

---

## ✅ 1. Check the PC can run it

| Requirement | How to check |
| :--- | :--- |
| Windows 10 version 2004 (build 19041) or later, or Windows 11 | `[Environment]::OSVersion.Version` → build `19041` or higher |
| 64-bit Intel or AMD processor (x64) | `$env:PROCESSOR_ARCHITECTURE` → `AMD64` |
| An administrator account (setup asks for admin once) | The person can approve a UAC prompt |
| Internet during setup, if .NET 10 isn't installed | Setup downloads it from Microsoft (`aka.ms/dotnet`) |
| About 400 MB free on a local drive | The installed folder is about 170 MB |
| 4 GB of memory or more (8 GB recommended) | `[math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)` |

ARM PCs (such as Snapdragon laptops) are not supported. Recommended specs and measured processor and memory use are in the [README](README.md#-requirements). On a slow PC, **Settings > General > Ultra performance** makes the app much lighter while its window is open.

## 🛡️ 2. Check Smart App Control

Current releases are signed with the developer's own certificate (Tem Revil), which Windows doesn't know yet. Smart App Control, when it's **on**, blocks such programs outright, with no way to allow just this one.

```powershell
# 0 = off, 1 = on, 2 = evaluation (watching, not blocking)
(Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' -ErrorAction SilentlyContinue).VerifiedAndReputablePolicyState
```

- **0 or 2:** go on to step 3.
- **1 (on):** setup won't run. Tell the person, plainly, and let them decide. Turning Smart App Control off (Windows Security > App & browser control > Smart App Control) can't be undone without resetting Windows. **Never turn it off for them, and never suggest it as a routine step.** A Microsoft Store version, which Smart App Control allows, is being prepared; once it's listed, it's the way to install with Smart App Control on.

## 📥 3. Download from the official source only

The only official download is the latest GitHub release of `TemRevil/Glass-Rea`:

- Page: https://github.com/TemRevil/Glass-Rea/releases/latest
- File: `Glass-Rea.Setup.exe` (about 160 MB)

Don't use mirrors, download sites or copies sent by others. To download and verify in PowerShell:

```powershell
$release = Invoke-RestMethod 'https://api.github.com/repos/TemRevil/Glass-Rea/releases/latest'
$asset = $release.assets | Where-Object name -eq 'Glass-Rea.Setup.exe'
$file = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'Downloads\Glass-Rea.Setup.exe'
Invoke-WebRequest $asset.browser_download_url -OutFile $file

# 1) The file is exactly the one GitHub recorded for the release.
$expected = $asset.digest -replace '^sha256:', ''
(Get-FileHash $file -Algorithm SHA256).Hash -eq $expected.ToUpper()     # must be True

# 2) It's signed by Glass-Rea's publisher.
$sig = Get-AuthenticodeSignature $file
$sig.SignerCertificate.Subject                                          # CN=Tem Revil (or SignPath Foundation)
$sig.SignerCertificate.Thumbprint                                       # E6A4B1317EA62EDEAC18FCA3214798EFBF8D068E for Tem Revil
```

If the hash doesn't match, delete the file and stop. `Get-AuthenticodeSignature` reports `UnknownError` or `NotTrusted` for the Tem Revil certificate because Windows doesn't trust it yet; that is expected. What matters is the subject and thumbprint above.

## 🚀 4. Run setup

Setup is a normal window with buttons; it has no silent mode. Start it and let the person click through:

```powershell
Start-Process $file
```

1. **SmartScreen** may say "Windows protected your PC". Click **More info**, check the publisher says Tem Revil, then **Run anyway**.
2. **UAC** asks for admin permission: **Yes**.
3. Keep the default folder `C:\Program Files\Glass-Rea\` (or **Change** it, local drives only), choose whether Glass-Rea starts with Windows, and click **Install**.
4. Setup installs .NET 10 if it's missing, installs and starts the `GlassRea` service, and adds Glass-Rea to Start and to Installed apps.
5. On the last page, leave **Open Glass-Rea now** ticked and click **Finish**.

Running a newer setup over an older install updates it in place and keeps history, settings and firewall blocks.

## 🔍 5. Check it worked

```powershell
Get-Service GlassRea                                    # Status: Running, StartType: Automatic
Test-Path 'C:\Program Files\Glass-Rea\App\GlassRea.exe' # True (for the default folder)
```

The window opens on the **Overview** page. The first time, it imports the last 30 to 60 days of usage Windows already keeps, so history shows up within a minute. Closing the window keeps it in the tray, by the clock.

## 🧯 Troubleshooting

| What the person sees | What to do |
| :--- | :--- |
| "This app has been blocked for your protection" / Smart App Control | See step 2. Explain the trade-off; the decision is theirs. |
| "Windows protected your PC" | More info > Run anyway, after checking the publisher is Tem Revil. |
| "The Glass-Rea service isn't running" banner in the app | Run setup again (it repairs the install), or `Start-Service GlassRea` from an admin PowerShell. |
| Setup can't download .NET | Check the internet connection, or install the .NET 10 Runtime (x64) from https://dotnet.microsoft.com/download/dotnet/10.0, then run setup again. |
| Devices show without names | Normal when Windows has the network set as Public; names appear as devices rejoin. The app explains this on the Devices page. |
| Something else | Settings > About > Logs can email the logs to the developer, or open an issue: https://github.com/TemRevil/Glass-Rea/issues |

Logs: the app's are in `%LOCALAPPDATA%\Glass-Rea\Logs`, the service's in `%ProgramData%\Glass-Rea\Logs`; setup's is `%TEMP%\Glass-Rea Setup.log`.

## 🔄 Updates

Glass-Rea checks GitHub once a day (this can be turned off) and installs updates from **Settings > About**, after checking the file matches what GitHub recorded and is signed by the same publisher. Nothing else to do.

## 🗑️ Uninstall

Settings > Apps > Installed apps > **Glass-Rea** > Uninstall. The person can keep or delete their usage history (`C:\ProgramData\Glass-Rea`). Every firewall block the app made is removed either way, so no app stays blocked.

## 🔒 What it sends

Nothing about the person or their network leaves the PC. The only requests are the daily update check to GitHub (can be turned off) and, during setup, the .NET download from Microsoft. Details: [PRIVACY.md](PRIVACY.md).
