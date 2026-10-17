# Changelog

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
