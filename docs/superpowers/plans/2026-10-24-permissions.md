# Permissions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Glass-Rea 1.2.8, where the SYSTEM service does nothing for a feature until the person allows it, from a first-run screen and a Settings card.

**Architecture:** Four permission flags live in the service's `settings` table. `ServiceState` exposes a gate per feature (is it allowed, wait until it is, a token that cancels when it's withdrawn); every privileged worker loops on that gate. The app reads the flags from `status`, shows a permission screen while they haven't been reviewed, a Permissions card in Settings, and an "Off" state on pages whose feature is off.

**Tech Stack:** C#/.NET 10, WinUI 3, xUnit, Windows' SQLite (`Database`).

**Spec:** `docs/superpowers/specs/2026-10-24-permissions-design.md`

## Global Constraints

- Setting keys exactly: `allow_usage`, `allow_blocking`, `allow_history`, `allow_devices` ("true"/"false"), `permissions_reviewed` ("true"/"false").
- Pipe ops exactly: `permissions`, `permissions.set`, `history.delete`. Pipe ACL unchanged.
- Fresh install: all four "false", `permissions_reviewed` "false". Existing install (database has `first_etw_hour` set or any `usage` row, and no `allow_*` key): all four "true", `permissions_reviewed` "false".
- Words on screen: plain, sentence case, no em dashes; all permission wording in `Core/Alerts/PermissionText.cs`, with tests.
- Latin digits only. Build with no warnings; every new test seen failing first (AGENTS.md).
- Off means off: no ETW session, no WFP engine open, no SRUM copy, no scan, no socket on 67/5353, no discovery rule while the feature is off. Each start and stop is logged once ("Usage per app is on/off", etc.).
- Version 1.2.8 in `Directory.Build.props`.

## Review Focus

1. Past usage on, Usage per app off: the SRUM importer today waits for `first_etw_hour` forever. Expected: it imports up to the current hour. Test in Task 4.
2. Usage turned on for the first time long after install: every app looks new and the new-app alert floods. Expected: no new-app alerts in the first hour of capture. Test in Task 4.
3. Blocking turned off while a Store-copy block is waiting for its drive (`_waiting`): expected it's dropped too, and the retry timer never re-adds it. Test in Task 5.
4. "Block internet" pressed while Blocking is off (old page open): expected a plain refusal, nothing blocked. Test in Task 5.
5. Delete history while capture is running: expected the next flush writes only new bytes, no crash, settings and blocks kept. Test in Task 3.

---

### Task 1: Permission model and wording (Core)

**Files:**
- Create: `src/GlassRea.Core/Protocol/Permissions.cs`
- Create: `src/GlassRea.Core/Alerts/PermissionText.cs`
- Modify: `src/GlassRea.Core/Protocol/Messages.cs` (SettingKeys, ServiceStatus)
- Test: `tests/GlassRea.Tests/PermissionTests.cs`

**Interfaces:**
- Produces: `enum Feature { Usage, Blocking, History, Devices }`; `static class PermissionKeys { string For(Feature f); const string Reviewed = "permissions_reviewed"; Feature[] All }` mapping to `allow_usage`, `allow_blocking`, `allow_history`, `allow_devices`; `sealed record PermissionSet(bool Usage, bool Blocking, bool History, bool Devices, bool Reviewed) { bool Allowed(Feature f) }`; `ServiceStatus` gains `PermissionSet? Permissions = null` as its last parameter; `PermissionText.Title(Feature)`, `PermissionText.Why(Feature)`, `PermissionText.OffTitle(Feature)`, `PermissionText.OffBody(Feature)`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void KeysAreStable() {
    Assert.Equal("allow_usage", PermissionKeys.For(Feature.Usage));
    Assert.Equal("allow_blocking", PermissionKeys.For(Feature.Blocking));
    Assert.Equal("allow_history", PermissionKeys.For(Feature.History));
    Assert.Equal("allow_devices", PermissionKeys.For(Feature.Devices));
}
[Fact] public void AllowedReadsEachFlag() {
    var p = new PermissionSet(true, false, true, false, false);
    Assert.True(p.Allowed(Feature.Usage)); Assert.False(p.Allowed(Feature.Blocking));
    Assert.True(p.Allowed(Feature.History)); Assert.False(p.Allowed(Feature.Devices));
}
[Theory, MemberData(nameof(Features))] public void WordingIsPlain(Feature f) {
    foreach (var s in new[] { PermissionText.Title(f), PermissionText.Why(f), PermissionText.OffTitle(f), PermissionText.OffBody(f) }) {
        Assert.False(string.IsNullOrWhiteSpace(s));
        Assert.DoesNotContain('\u2014', s);
        Assert.DoesNotContain('\u2013', s);
        Assert.Matches("^[A-Z]", s); // sentence case
    }
}
```

- [ ] **Step 2: Run** `dotnet test tests\GlassRea.Tests --filter PermissionTests` - expected: build fails (types missing).

- [ ] **Step 3: Implement** the types above. Wording (exact copy):
  - Usage: "Usage per app" / "Counts each app's internet use from Windows' network events. Only Windows itself can read those, so the service runs as SYSTEM."
  - Blocking: "Blocking apps" / "Lets Block internet stop an app in Windows' firewall. Only an administrator or SYSTEM can add firewall filters."
  - History: "Past usage from Windows" / "Reads once what Windows recorded before Glass-Rea was installed. Windows keeps that file where only SYSTEM can open it."
  - Devices: "Device discovery" / "Finds the phones, TVs and computers on your network and listens for their names. This adds a firewall rule for your private networks only."
  - OffTitle: "<Title> is off"; OffBody: Usage "Nothing is being counted. Turn it on to see what each app uses.", Blocking "Apps can't be blocked. Turn it on to use Block internet.", History "Windows' past usage isn't read.", Devices "Your network isn't scanned. Turn it on to see its devices."

- [ ] **Step 4: Run** the filter again - expected: PASS. Revert one key string, see `KeysAreStable` fail, restore.

- [ ] **Step 5: Commit** `git commit -m "Permission model and wording"`

### Task 2: Gate in the service, with first-start defaults

**Files:**
- Create: `src/GlassRea.Service/Permissions.cs`
- Modify: `src/GlassRea.Service/ServiceState.cs` (holds a `Permissions`, `Snapshot()` fills `ServiceStatus.Permissions`)
- Modify: `src/GlassRea.Service/Program.cs` (register `Permissions` singleton; call `Initialize()` before `RestoreBlocks`)
- Test: `tests/GlassRea.Tests/PermissionTests.cs`

**Interfaces:**
- Consumes: Task 1 types.
- Produces: `sealed class Permissions(Database db)` with `void Initialize()` (writes defaults once, per Global Constraints), `PermissionSet Current`, `bool Allowed(Feature f)`, `void Set(Feature f, bool on)`, `void MarkReviewed()`, `event Action<Feature, bool>? Changed`, `Task WaitAllowed(Feature f, CancellationToken ct)`, `CancellationToken WhileAllowed(Feature f)` (a token cancelled when `f` is set off; a fresh one after each withdrawal).

- [ ] **Step 1: Failing tests**
  - `FreshDatabaseStartsAllOff`: `Database.InMemory()`, `Initialize()` -> all four false, Reviewed false.
  - `ExistingInstallKeepsEverythingOn`: set `first_etw_hour` = "1", `Initialize()` -> all four true, Reviewed false.
  - `ExistingUsageRowAlsoCountsAsInstalled`: one `AddUsage` row, no `first_etw_hour` -> all true.
  - `InitializeRunsOnce`: fresh, `Initialize()`, `Set(Usage, true)`, `Initialize()` again -> Usage still true, others false.
  - `WaitAllowedReturnsWhenTurnedOn`: task from `WaitAllowed(Devices)` not completed; `Set(Devices, true)` -> completes within 1 s.
  - `WhileAllowedCancelsWhenTurnedOff`: on, token = `WhileAllowed(Usage)`; `Set(Usage, false)` -> `token.IsCancellationRequested`; `Set(Usage, true)` -> `WhileAllowed(Usage)` is a new, uncancelled token.
  - `ChangedFiresOnlyOnRealChange`: setting the same value twice raises `Changed` once.

- [ ] **Step 2: Run** filter - FAIL (missing type).
- [ ] **Step 3: Implement.** One `Lock`; per feature a `TaskCompletionSource` for waiters and a `CancellationTokenSource` for `WhileAllowed`. `Set` writes the key, logs "<Title> is on/off" via an injected `ILogger<Permissions>` (optional parameter for tests), then signals.
- [ ] **Step 4: Run** - PASS; break `Initialize`'s existing-install check, see two tests fail, restore.
- [ ] **Step 5: Commit** `"Service: a gate per permission"`

### Task 3: Delete history and lift blocks in the database

**Files:**
- Modify: `src/GlassRea.Core/Storage/Database.cs`
- Test: `tests/GlassRea.Tests/DatabaseTests.cs`

**Interfaces:**
- Produces: `void DeleteHistory()` - one transaction: `DELETE FROM` usage, hosts, devices, forgotten_devices, device_hints, alerts, dns; keeps apps, networks, settings, `apps.blocked`. `void ClearBlocked()` - sets every app's blocked flag to 0.

- [ ] **Step 1: Failing tests**
  - `DeleteHistoryKeepsSettingsBlocksAndNetworks`: add usage, host, device, alert, a setting, a blocked app, a network; `DeleteHistory()`; `Report(...)` has no apps with bytes, `Devices()` empty, `Alerts(0)` empty, `GetSetting` unchanged, `BlockedApps()` unchanged, `Networks(0)` unchanged.
  - `UsageAfterDeleteCountsOnlyNewBytes`: add 1000, delete, add 50 for the same hour -> report NetIn 50. (Review Focus 5)
  - `ClearBlockedUnblocksAll`: two blocked -> `ClearBlocked()` -> `BlockedApps()` empty.
- [ ] **Step 2: Run** - FAIL. **Step 3: Implement.** **Step 4: Run** - PASS, see one fail by dropping a table from the delete. **Step 5: Commit** `"Database: delete history, clear blocks"`

### Task 4: Usage and history workers follow their permission

**Files:**
- Modify: `src/GlassRea.Service/Capture/EtwCapture.cs`, `Capture/FlushService.cs`, `Networks/NetworkWatcher.cs`, `LimitWatcher.cs`, `History/SrumImporter.cs`
- Modify: `src/GlassRea.Core/Protocol/Messages.cs` (`SettingKeys.CaptureFirstHour = "capture_first_hour"`)
- Test: `tests/GlassRea.Tests/PermissionTests.cs`

**Interfaces:**
- Consumes: `Permissions.WaitAllowed`, `Permissions.WhileAllowed` (Task 2).
- Produces: `static long SrumImporter.Cutoff(long firstLiveHour, long nowHour)` (firstLiveHour when > 0, else nowHour); `static bool FlushService.Settled(long captureFirstHour, long nowHour)` (true only when captureFirstHour > 0 and nowHour > captureFirstHour).

- [ ] **Step 1: Failing tests**
  - `SrumCutoffUsesNowWhenNothingWasCaptured`: `Cutoff(0, 500) == 500`; `Cutoff(400, 500) == 400`. (Review Focus 1)
  - `NewAppAlertsWaitForTheFirstCaptureHour`: `Settled(0, 9) == false`, `Settled(9, 9) == false`, `Settled(9, 10) == true`. (Review Focus 2)
- [ ] **Step 2: Run** - FAIL.
- [ ] **Step 3: Implement.**
  - EtwCapture: outer loop `await permissions.WaitAllowed(Usage, ct)`; the session's stop is registered on a linked token of `ct` and `WhileAllowed(Usage)`; on withdrawal `Capturing = false`, `CaptureError = null`, loop back to waiting. Sets `capture_first_hour` when null (existing installs: `Permissions.Initialize` copies `first_etw_hour` into it).
  - FlushService: `OnNewApp` uses `Settled(capture_first_hour, now/3600)`.
  - NetworkWatcher and LimitWatcher: wait for Usage before each round; idle while off.
  - SrumImporter: waits for History (not for `first_etw_hour`); uses `Cutoff`; when it imports with no live hour yet, writes `first_etw_hour` = cutoff so live capture later doesn't double count. Cancels cleanly on `WhileAllowed(History)` (state back to "pending").
- [ ] **Step 4: Run** all tests - PASS; revert `Cutoff`'s fallback, see it fail, restore.
- [ ] **Step 5: Commit** `"Usage and history wait for their permission"`

### Task 5: Blocking and devices follow their permission

**Files:**
- Modify: `src/GlassRea.Service/FirewallManager.cs`, `DeviceScanner.cs`, `DeviceListener.cs`, `Program.cs`
- Test: `tests/GlassRea.Tests/PermissionTests.cs`

**Interfaces:**
- Consumes: Task 2 gate, Task 3 `ClearBlocked`.
- Produces: `FirewallManager.LiftAll()` (removes every rule in group "Glass-Rea" except the discovery rule, or every WFP filter; clears `_waiting`, stops `_retry`, `ClearBlocked()`); `FirewallManager.RemoveDiscovery()`; `SetBlocked` throws `InvalidOperationException(PermissionText.OffBody(Blocking))` when Blocking is off. `static bool FirewallManager.IsBlockRule(string name)` (true for `RuleName(...)` names, false for `DiscoveryRule`).

- [ ] **Step 1: Failing tests**
  - `OnlyBlockRulesAreLifted`: `IsBlockRule(RuleName("c:\\a.exe", true))` true, `IsBlockRule(DiscoveryRule)` false.
  - `BlockRefusedWhileBlockingIsOff`: FirewallManager over an in-memory database with Blocking off; `SetBlocked(id, true)` throws with the OffBody text; `BlockedApps()` empty. (Review Focus 4)
  - `LiftAllForgetsWaitingBlocks`: packaged-mode path behind a test seam (`internal int WaitingCount`); after `LiftAll()` it's 0 and the timer is disposed. (Review Focus 3)
- [ ] **Step 2: Run** - FAIL. **Step 3: Implement.** `Program.cs` subscribes `Permissions.Changed`: Blocking off -> `LiftAll()`; Devices off -> `RemoveDiscovery()`. `RestoreBlocks` only when Blocking is allowed. DeviceScanner and DeviceListener: wait for Devices; run on a token linked to `WhileAllowed(Devices)`; listener calls `AllowDiscovery()` each time it starts. New-device and presence alerts already live in these workers, so they follow.
- [ ] **Step 4: Run** - PASS, each new test seen failing once. **Step 5: Commit** `"Blocking and devices follow their permission"`

### Task 6: Pipe requests

**Files:**
- Modify: `src/GlassRea.Service/PipeServer.cs`

**Interfaces:**
- Produces: `permissions` -> `PermissionSet`; `permissions.set` with `Settings` keys from `PermissionKeys` (values "true"/"false") plus optional `permissions_reviewed` "true" -> null, unknown key refused "Unknown permission: <key>"; `history.delete` -> null. `live`, `usage`, `hosts` keep answering from stored data whatever the flags.

- [ ] **Step 1:** Add the cases, delegating to `Permissions` and `Database.DeleteHistory` (logic is tested in Tasks 2-3).
- [ ] **Step 2: Run** `dotnet build GlassRea.slnx` - no warnings; `dotnet test` - all pass.
- [ ] **Step 3: Commit** `"Pipe: permissions and delete history"`

### Task 7: Permission screen, Settings card, Off states (app)

**Files:**
- Create: `src/GlassRea.App/Pages/PermissionsPanel.xaml(.cs)` (a user control, used by both the screen and the card)
- Modify: `src/GlassRea.App/MainWindow.xaml(.cs)` (overlay host; status loop shows it while `!Permissions.Reviewed`)
- Modify: `src/GlassRea.App/Pages/SettingsPage.xaml(.cs)` (General: "Permissions" card with the panel and "Delete history")
- Modify: `OverviewPage`, `AppsPage`, `ConnectionsPage` (Usage), `FirewallPage` (Blocking), `DevicesPage` (Devices): an InfoBar with `OffTitle`/`OffBody` and a "Turn on" button
- Modify: `SettingsPage` Startup caption "Recording never stops..." -> "Recording runs whether this window is open or not, for what you've allowed in Permissions."

**Interfaces:**
- Consumes: `ServiceStatus.Permissions`, pipe ops from Task 6, `PermissionText`.
- Produces: `PermissionsPanel` with `ToggleSwitch` per feature named by `PermissionText.Title` (UI Automation finds them by name); screen buttons "Allow all" and "Done"; Delete history asks with a `ContentDialog` ("Delete history?" / "Usage, connections, devices and alerts recorded so far are removed. Settings and blocks stay." / "Delete" / "Cancel").

- [ ] **Step 1:** Build the panel and the overlay; closing the window with the overlay open sets nothing (spec).
- [ ] **Step 2:** The service's data folder is fixed (`C:\ProgramData\Glass-Rea`), so a fresh state is only checked in Windows Sandbox (Task 9). Here: back up `data.db`, list the owner's current blocks (`apps.blocked`) first, and never switch Blocking off on this PC while any exist. Check by UI Automation in dark and light: with `permissions_reviewed` set to "false" the overlay shows; Allow all turns all on; Done hides it and it stays hidden on reopen; Settings card mirrors the flags; each page shows its Off bar when its feature is off and "Turn on" turns it on; Delete history dialog. Screenshots saved to the session scratchpad.
- [ ] **Step 3: Commit** `"App: permission screen, Settings card, Off states"`

### Task 8: Docs, version, Store texts

**Files:**
- Modify: `README.md`, `INSTALL.md`, `PRIVACY.md`, `SECURITY.md`, `AGENTS.md` (Rules: "New privileged work sits behind a permission (`Service/Permissions.cs`)"), `CHANGELOG.md` (1.2.8), `Directory.Build.props` (1.2.8)

- [ ] **Step 1:** Write them per the spec's "Docs to update".
- [ ] **Step 2:** `dotnet build` clean, `dotnet test` all pass.
- [ ] **Step 3: Commit** `"1.2.8"`. Every commit is Tem Revil's; the Claude co-author trailer goes only on a commit of 150+ changed lines.
- [ ] **Step 4:** Rewrite the two Store justifications (each under 500 characters, counted) to add "The service does nothing until the user turns each feature on in the app's permission screen." Give them to the owner.

### Task 9: On the owner's PC, then release (owner's go-ahead)

- [ ] **Step 1:** Install 1.2.8 over 1.2.7 here: features stay on, history and blocks kept, screen opens once (existing-install path). `service.log` shows no "is off" lines.
- [ ] **Step 2:** Switch each feature off and on in Settings; for each, `service.log` has the on/off line and the off state holds (no `GlassRea-Capture` session in `logman query -ets` with Usage off; `Get-NetFirewallRule -DisplayName "Glass-Rea device discovery"` gone with Devices off; a block lifted with Blocking off). Restore the owner's original choices afterwards.
- [ ] **Step 3:** Store package in Windows Sandbox (`scripts/sandbox/test-store.ps1`): fresh install shows all off, nothing recorded until allowed.
- [ ] **Step 4:** Ask the owner before: pushing, `scripts/release.ps1` (GitHub release), and the Store resubmission.
