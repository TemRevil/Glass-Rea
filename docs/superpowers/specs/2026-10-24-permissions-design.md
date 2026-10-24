# Permissions: ask before the service does anything (1.2.8)

## Why

The Microsoft Store refused submission 1 (2026-10-22, policy 10.6.3): `packagedServices` and `localSystemServices`
were denied "based on the information provided". The service has to run as SYSTEM (kernel network events, Windows
Filtering Platform, Windows' SRUM history), so those capabilities stay. What changes is that nothing the service can
do happens until the person has said yes, feature by feature, with a plain line on why each needs system rights. This
is wanted for its own sake, not only for the Store: the person sees and controls what the app does with SYSTEM, in
both copies (setup's and the Store's), instead of Windows granting it silently at install.

## Goals

- First open of the app shows a permission screen with four switches, each with one line on what it does and why it
  needs system rights:
  1. **Usage per app** - per-app traffic counting (ETW), and everything built on it: the live graph, networks, data
     limits, "new app went online" alerts.
  2. **Blocking apps** - "Block internet" (firewall rules for setup's copy, WFP filters for the Store copy).
  3. **Past usage from Windows** - the one-time import of Windows' own history (SRUM).
  4. **Device discovery** - scans, passive listening (UDP 67/5353) and the "Glass-Rea device discovery" firewall rule.
- The service runs from install, but a feature that isn't allowed does nothing: no ETW session, no WFP engine, no
  SRUM read, no scan, no listening socket, no firewall rule. The pipe always answers, so the app can ask.
- Settings > General gets a **Permissions** card with the same four switches.
- Turning a switch off acts at once:
  - Usage per app: capture stops; what was recorded stays.
  - Blocking apps: every block is lifted and forgotten (`apps.blocked` cleared, rules/filters removed). Turning it back
    on does not bring old blocks back.
  - Past usage from Windows: an import in progress stops; what was imported stays.
  - Device discovery: scans and listening stop, the discovery firewall rule is removed; the device list stays.
- **Delete history** button in the Permissions card: removes recorded usage, connections (hosts), devices (and
  forgotten devices, device hints), and alerts. Keeps settings, networks' names and blocks. Asks once to confirm.
  Doesn't start a new SRUM import afterwards.
- Pages whose feature is off show a short "Off" state with a button that turns it on, not an empty page.
- Upgrades from 1.2.7 or earlier (owner's choice): the features stay on (no gap in history, no blocks lifted), and the
  permission screen opens once so the person can review and switch things off.
- 1.2.8 released for setup's copy, and a Store package 1.2.8.0 resubmitted with the justifications rewritten to say the
  service does nothing until the user allows each feature.

## Non-goals

- No change to how the service runs (still SYSTEM, still installed by setup / the package).
- No UAC prompt from the app, no `allowElevation`.
- No per-feature split finer than the four switches (limits and alerts follow Usage per app).

## Anti-goals

- A switch that says off while the service still does that thing. Off must be provably off (log line, tests).
- Losing an existing user's history or blocks on update.
- Pre-ticked switches on a fresh install: all four start off; the screen offers "Allow all" and per-switch choices.

## Decided by the owner

- Both copies get the screen (it lives in the app, so it's the same code).
- One switch per feature, four features.
- Withdraw = stop, lift blocks, keep history; separate Delete history button.
- Existing installs keep running and see the screen once.

## My calls (builder may refine)

- Consent is service-side state in `settings` (shared by both copies, since both use `C:\ProgramData\Glass-Rea\data.db`):
  `allow_usage`, `allow_blocking`, `allow_history`, `allow_devices` ("true"/"false"), plus `permissions_reviewed`
  ("true" once the person has closed the screen). New pipe ops: `permissions` (read), `permissions.set`,
  `history.delete`. Same pipe ACL as today (any signed-in user), which is what blocking already allows.
- Existing install = the database already has `first_etw_hour` or any `usage` row when the 1.2.8 service first starts
  and no `allow_*` key exists: set all four to "true", `permissions_reviewed` = "false".
- Workers stay registered as hosted services but wait on a gate in `ServiceState` (a per-feature flag plus a signal),
  so turning a feature on or off doesn't restart the service.
- The screen is shown by the app at start while `permissions_reviewed` is "false": a page over the main window, not a
  separate window. Setup's last page keeps "Open Glass-Rea now".
- Wording lives in one place (like `AlertText.cs`), plain, sentence case, no em dashes.
- Closing the screen without pressing Done changes nothing and it shows again next open; Done (or Allow all) sets
  `permissions_reviewed`.
- New-device and device left/back alerts follow Device discovery; new-app and limit alerts follow Usage per app.
- The Store copy's standby (setup's copy still installed) is unchanged; once it takes over, it reads the same
  `allow_*` keys.
- `ServiceStatus` gains the four flags so pages know to show their "Off" state without another request.

## Docs to update

README (what the app asks on first run), INSTALL.md (setup's screens are followed by the permission screen), PRIVACY.md
(nothing runs until allowed; Delete history), SECURITY.md (who can change permissions: any signed-in user, as with
blocks), AGENTS.md (gate rule: new privileged work must sit behind a permission), CHANGELOG 1.2.8.

## Checks

- Tests for: the existing-install detection, each gate (feature off = worker idle), Blocking off clears `apps.blocked`,
  Delete history leaves settings and blocks. Each seen failing first.
- On the PC: fresh state (copy of the database with settings removed) shows the screen, all off, nothing in
  `service.log` about capture/WFP/SRUM/scan; allow each one and see it start; off again and see it stop and the
  discovery rule go. Dark and light by UI Automation.
- Store package tested in Windows Sandbox (`scripts/sandbox/test-store.ps1`) before resubmitting.
