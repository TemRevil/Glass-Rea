# Product

<!-- impeccable:product-schema 1 -->

## Platform

windows desktop (native, WinUI 3 front end; not one of web / ios / android)

## Users

People on Windows 10 or 11 who want to know which apps actually spend their internet, per day, week and month, including behind a VPN, without paying for a commercial monitor.

## Product Purpose

A network monitor for one PC: per-app internet use by day, week and month; a live traffic graph; who each app talks to; one-click blocking of an app's internet; devices on the network; alerts for new apps, new devices and data limits. Success: the user can answer "where did my data go today / this month" at a glance, and stop an app from using the internet in one click.

## Positioning

It splits traffic per app even under a VPN (the tunnel is listed but not double-counted) and imports the 30 to 60 days of history Windows already keeps, so it is useful from the first minute. Local only: no account, no cloud.

## Operating Context

Lives in the tray and starts with Windows; opened a few times a day for a quick look, occasionally for a longer dig (which app, which hosts, block it). A background service (SYSTEM) records everything; the window is only a viewer and controller over a local pipe.

## Capabilities and Constraints

- Pages: overview / usage, live traffic, apps and connections, firewall, devices, alerts, settings.
- Must run with Smart App Control on: the service loads no unsigned third-party binaries; app-side dependencies are Microsoft-signed or built in this repo.
- Invariant culture, Latin digits only (no Arabic-Indic digits), no em-dashes in app text.
- Must follow Windows light/dark.
- Must stay light on low-end PCs; Ultra performance makes the window as cheap as it can be.

## Brand Commitments

- Name: "Glass-Rea". Credit "Made by Tem Revil" in the app and installer; publisher "Tem Revil" in file properties. Releases are signed with the Tem Revil certificate; signing by SignPath Foundation is being set up.
- Design: Fluent 2 (fluent2.microsoft.design) with blurred glass materials, dark and light mode, fade-and-blur transitions and real motion, a regridded layout with no generic sidebar, settings in sections rather than one long page, and a vector logo.
- Tone: calm, architectural, restrained, premium; no startup or flashy looks; motion calm and slower than usual.

## Evidence on Hand

Real data from the running service. No testimonials or marketing claims exist or are needed.

## Product Principles

1. Answer "where did my data go" before anything else.
2. Never double-count: tunnels shown, not summed.
3. One click to act (block, unblock, open details), with a clear way back.
4. Quiet by default; alerts only for things worth interrupting for.
