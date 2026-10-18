# Changelog

## 1.2.6

- Usage per network: the Overview, Apps and Connections pages have a network picker, so you can see what went over one network ("this month on Home") or all of them together, as before. Wi-Fi, wired and mobile networks are each counted, and use over a VPN counts on the network the VPN runs over. The picker shows once there's more than one network to choose from.
- Each network is shown by the name Windows gives it, and you can rename it in Settings > Data > Networks, as with devices.
- Earlier use is placed on its network from what Windows already keeps, where Windows says which Wi-Fi it was. Use Windows can't place, such as over a cable before this version, is shown as "Unknown network". Nothing is lost: all networks together add up to the same totals as before.
- Limits and their alerts still count every network together, as your provider does. With a network picked, the Overview's month card says so.

## 1.2.5

- Ultra performance, in Settings > General: solid glass, a plain window and no animations, for the least processor while the window is open (about 4% of one core on the Overview instead of 14%, on the PC this was measured on).
- Lighter with the glass on too: about 14% of one core on the Overview instead of 20%. The glass no longer redraws its blur when nothing behind it moved, and the dot by "Live" no longer breathes, which kept the whole window redrawing.
- Much less memory in the tray: hidden, the app hands its memory back to Windows, about 20 to 40 MB in use instead of 200.

## 1.2.4

- The bottom bar stays frosted while pages change: the old page dissolves and the new one sharpens in behind the glass, at the same time, so the bar never shows an empty or unblurred moment.
- The highlight under the selected tab slides smoothly to it and the page follows, instead of the highlight stalling halfway and jumping. The page change runs at the screen's full rate (about 10 frames a second before) and eases in instead of snapping into place.
- Pressing the bar gives a soft light instead of a hard-edged block sweeping across the tabs.

## 1.2.3

- Lighter on the PC: the app uses about a third of the processor it did while open (16% of one core instead of 47% on the Overview, on the PC this was measured on) and much less memory. The live graph is drawn once a second and Windows' compositor slides it in between; the glass bends light only where it doesn't change every second; nothing is drawn while the window is minimised.
- The usage chart grows in once it's on screen, instead of showing the bars first and then animating them.
- The bottom bar is properly frosted: the page no longer shows through it sharply.
- Firewall opens at once, even with hundreds of apps: only the rows on screen are made. Pages that take a moment to open show a thin loading line until they are ready.
- Devices: Forget takes a device off the list; if it's still on your network, the next scan brings it back. Scan now waits for the scan to finish and then shows what it found. A phone that switches to a new private address is kept as one device, with your name for it, instead of being listed twice; you get a new-device alert saying so, since anyone could copy its name. A Samsung TV is no longer taken for a Samsung phone.
- A tidier install: about 170 MB instead of 320 MB. The uninstaller no longer carries a second copy of the app, WinUI's text ships in English only (the app is in English), and only the parts of the Windows App SDK in use are installed.

## 1.2.2

- Real app icons everywhere: programs show the icon Explorer shows, Store apps the one in Start, and a program that has since updated into a new folder shows its current icon. What has no icon of its own gets a symbol for its kind, never a letter. Several icons asked for at once used to come back empty from Windows; they're now drawn one at a time.
- Kinds of apps: each row says whether it's a Store app, a service, Windows itself or a VPN tunnel, and the Apps page explains the difference. Services are shown by the name Windows gives them ("Delivery Optimization") with what they do, Store apps by their name and maker.
- One row per app: an app's versions, the rows Windows' history keeps for it and each sign-in's copy of a service are added together, on Apps, the Overview, Connections and Firewall. On Firewall, programs stay one row per program file, since a block names one file.
- Logs: the app and the service record crashes, errors, moments the window stopped responding, slow pages and slow requests, plus the crash reports Windows keeps for Glass-Rea. Settings > About > Logs emails them to the developer, saves a copy, or opens the folder. They stay on your PC until you send them.

## 1.2.1

- Fix: checking for updates said "GitHub is limiting requests from this network" on networks many people share, such as Cloudflare WARP. GitHub allows only 60 of the requests it used per hour from one address. The app now reads each release's own description file, an ordinary download GitHub doesn't limit that way, and asks the limited service only for a release without one.

## 1.2.0

- Glass settings: Settings > Appearance has a Glass card with the same controls as RealGlass (https://real-glass.vercel.app): chromatic aberration, frosting, glass opacity, light strength, edge smoothness, refraction index, corner radius, specular shininess, thickness, tint colour and tint strength, and a button to put them back. Chromatic aberration splits the edge into colours the way a prism does, from each colour's own refractive index. Blur is now called Frosting, and Glass is now Glass opacity (the same setting, read the other way round).
- Lists update in place: when a figure changes, only that figure changes on screen. Before, the whole list on Apps, Connections, Firewall, Devices, Alerts and the Overview's top apps was rebuilt, which also made the app stutter for a moment.
- The light that follows the pointer over the glass no longer trails behind it: Windows' compositor moves it from the pointer's own position, so it keeps up however busy the app is.
- Signing: this version also accepts updates signed by SignPath Foundation (free code signing for open source), a certificate Windows trusts, so that later releases, built on GitHub and signed that way, can run with Smart App Control on and still update in place. This release itself is still signed by Tem Revil. An update is now installed only if GitHub recorded a checksum for it.
- Privacy policy: [PRIVACY.md](https://github.com/TemRevil/Glass-Rea/blob/main/PRIVACY.md) says exactly what the app and setup send over the internet (only the update check, which can be turned off, and the .NET download), and setup links to it.

## 1.1.1

- Fix: Windows history records some VPNs under their service name (Cloudflare WARP as "CloudflareWARP"), so their traffic was counted as an ordinary app on top of the apps it carried. The service's program is now looked up, and history already imported is corrected once. On the PC this was found on, October dropped from 300 GB to 158 GB.
- Fix: the Connections page gave a VPN's share as a part of the apps' total, which doesn't include it, so it could read over 100%. A VPN's share is now of your connection.

## 1.1.0

- Updates: Settings > About checks GitHub for a newer release (and, unless turned off, does so once a day with a notification when one is out), shows what's new, and downloads and installs it. A download is installed only if it is exactly the file GitHub recorded for the release and is signed by Tem Revil.
- Devices are named and recognised: the name a device gives itself (from DHCP, Bonjour, NetBIOS or UPnP), its maker (the IEEE registry, built in), and what it is (phone, tablet, computer, TV, speaker, printer, console, camera, watch, smart home, storage, network gear) with its system (iOS, iPadOS, macOS, Android, Windows, Linux), each with its own icon. Devices with no name are called by what they are ("iPhone", "Samsung phone"). The Connections page names devices on your network the same way.
- The service listens for devices joining the network and announcing themselves, and adds one firewall rule (UDP 67 and 5353, local network only, for this service alone) so it can hear them.
- Apps using a VPN's local proxy or DNS (Cloudflare WARP in proxy mode, or its DNS) are counted as themselves.
- Limits and their alerts count what crossed your internet connection, the way your provider counts it: with a VPN on, that includes the VPN's own overhead. With a VPN in use, the Overview says how much your connection carried and how much of it was the VPN's overhead. Per-app figures are unchanged.
- Name a device yourself on the Devices page; your name stays through every scan, and clearing it goes back to the name the device gives itself.
- Optional alerts when a device leaves your network (missing for 15 minutes) or comes back, in Settings > Notifications. Off by default. Only devices of the network this PC is on can go missing, so taking a laptop elsewhere raises nothing.
- When Windows treats your network as public, the Devices page says so and opens network settings, since devices can't be heard announcing their names there.
- Choose the day the monthly count starts again (1st to 31st) and the first day of the week (Saturday, Sunday or Monday), in Settings > Data. Limits and alerts follow it.
- Liquid glass: edges bend the backdrop like a lens and catch light, from a real refraction and reflection model (see `src/GlassRea.Core/Look/LiquidGlass.cs`).
- Glass readouts when hovering the live graph and the usage charts.
- The download and upload chip resizes smoothly; the hover light follows the pointer at its own size and fades out where it is.
- Middle-click anywhere to scroll by moving the pointer, as in a browser.
- Settings sliders no longer lag.
- Hardening: a UPnP description is fetched only from a path that looks like one (no query, no user name); a Bonjour announcement only speaks for the device that sent it; at most 5,000 heard facts are kept in all; the discovery firewall rule applies on private and work networks only.

## 1.0.0

First public source release, under the name Glass-Rea (it was called Glasswire REA while private; installing over that copy moves its history, settings and firewall blocks across).

- Windows service: ETW capture per app, Windows' own usage history imported once, hourly totals in SQLite, firewall blocking, local network scan, limit and new app/device alerts.
- App: overview, apps, connections, firewall, devices, alerts and settings, with a live two-minute strip; light and dark themes, accent palettes and adjustable glass.
- Settings in sections: start with Windows (hidden or with the window), minimize and close to the tray, processor priority, notification choices with a test button, limits, VPN tunnels, appearance.
- Notifications with a short title and a plain line on what happened and what you can do.
- Setup: one signed exe, choice of install folder on a local drive, opens the app and temrevil.com when done, clean uninstall that removes every firewall block.
