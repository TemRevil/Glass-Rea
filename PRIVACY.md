# Privacy

Glass-Rea has no accounts, no telemetry, no analytics and no ads. What it records stays on your PC.

## What it records, and where

How much each app sends and receives and on which network (by the name Windows gives it), the addresses and names of the computers they talk to, the devices on your network (their names, addresses, network card makers and what they say about themselves), and the alerts it raised. All of it is kept in `C:\ProgramData\Glass-Rea\data.db` on your PC and is never sent anywhere. The app's own settings (its look and window) are kept in your Windows profile. Uninstalling asks whether to keep or delete your history.

**Logs.** To help fix problems, the app and the service keep plain-text logs of crashes, errors, moments the window stopped responding and anything that was slow: `%LocalAppData%\Glass-Rea\Logs` (the app) and `C:\ProgramData\Glass-Rea\Logs` (the service). They name the apps and pages involved and the error messages, and the app copies in the crash reports Windows itself keeps for Glass-Rea. They stay on your PC, keep only the last few megabytes, and are never sent anywhere: Settings > About > Logs can pack them into a .zip that you can save or attach to an email yourself.

## When it uses the internet on its own

**Checking for updates.** The app asks GitHub (`github.com`, and `api.github.com` when a release has no description file) for Glass-Rea's latest release once a day, and when you click Check in Settings > About. When a new version is out and you choose to install it, the setup is downloaded from GitHub. These requests carry only what any web request does: your IP address, and the app's name and version ("Glass-Rea/1.2.0"). Turn off **Check for updates automatically** in Settings > About and the app contacts GitHub only when you click Check. GitHub's privacy statement: https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement

That is the only thing the app or the service sends over the internet by itself.

## On your own network

To name the devices on your network, the service talks to them directly: it pings them, asks them for their names, and listens for the announcements they broadcast. None of this leaves your network. [SECURITY.md](SECURITY.md#local-network-discovery) lists exactly what it sends and listens for.

## Setup

If .NET 10 isn't on your PC, setup downloads it from Microsoft (`aka.ms`, then Microsoft's download servers). Microsoft's privacy statement: https://privacy.microsoft.com/privacystatement. When setup finishes it can open temrevil.com, Tem Revil's website, if you leave that box ticked; the link goes through temrevil.com's own short link, which counts that the visit came from setup.

## Questions

Open an issue at https://github.com/TemRevil/Glass-Rea/issues.
