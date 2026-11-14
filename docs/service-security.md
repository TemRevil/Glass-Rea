# Glass-Rea's service: what it may do, and who may ask it

Glass-Rea is two programs. The app (`GlassRea.exe`) is what people see. It runs as the signed-in person, at medium integrity. The service records network use and applies blocks. In the Microsoft Store package it is the packaged service `GlassReaStore` (`Service\GlassRea.Service.exe`), started by Windows as LocalSystem. The copy installed by Glass-Rea's own setup is the service `GlassRea`. Both run the same code. This page describes the Store copy and points out where setup's copy differs.

The source is public: https://github.com/TemRevil/Glass-Rea. File names below are paths in that repository.

## Nothing runs until the person allows it

A fresh install starts with every privileged feature off. The first time the app opens, it lists the four things the service would use its rights for, each with a line on why, and nothing starts until the person turns it on. The same switches are in Settings > General > Permissions. Turning one off stops it at once (`src/GlassRea.Service/Permissions.cs`, `WhileOn`).

| Feature | What the service does with its rights |
|---|---|
| Usage per app | A real-time ETW session (`GlassRea-Capture`) on Microsoft-Windows-Kernel-Network, Kernel-Process and DNS-Client, to count bytes per process |
| Blocking apps | Block filters in the Windows Filtering Platform for the programs the person picks |
| Past usage from Windows | Reads a shadow copy of the System Resource Usage Monitor database (`C:\Windows\System32\sru\SRUDB.dat`) with Windows' own `esentutl.exe`, once, to show use from before the install |
| Device discovery | Listens for DHCP (UDP 67) and Bonjour (UDP 5353) on private networks, so the Devices page can name devices, and adds the firewall rule that lets it hear them |

## How the app talks to the service

- **Mechanism:** one local named pipe, `\\.\pipe\GlassRea`. The app sends one JSON request per line, and the service sends one JSON response per line (`src/GlassRea.Core/Protocol/Messages.cs`, `PipeClient.cs`, `src/GlassRea.Service/PipeServer.cs`). There is no other IPC: no TCP or HTTP listener, no COM server, no RPC endpoint, no shared memory.
- **Pipe access list** (`PipeServer.Security`):
  - NETWORK: denied. Anyone connecting from another PC signs in over the network and carries this group.
  - INTERACTIVE (people signed in at this PC): read and write. This does not include creating pipe instances.
  - SYSTEM and Administrators: full control.
  - Nothing else. Services, batch jobs and network logons get no access.
- **Only the service can serve the pipe.** Its first instance is created with `FILE_FLAG_FIRST_PIPE_INSTANCE`, so it won't start serving if some other program already holds the name. While it runs, nobody but SYSTEM and administrators may add instances. The app also checks the other side: after connecting, it asks Windows which process serves the pipe (`GetNamedPipeServerProcessId`), and compares that with the process id the Service Control Manager reports for `GlassReaStore` or `GlassRea` (`QueryServiceStatusEx`). If they don't match, the app hangs up without sending anything.

## Who may send it requests

The pipe's access list only decides who can open it. For each connection, the service then checks which program is calling (`src/GlassRea.Service/PipeCallers.cs`). It asks Windows for the client's process id (`GetNamedPipeClientProcessId`) and opens that process for limited query only. It then accepts the caller if one of these is true:

1. The caller is the Store copy's app. Its package family must be `TemRevil.Glass-Rea_m8znxtvwyfwk8` (`GetPackageFamilyName`), and its program file must be exactly `GlassRea.exe` in that package's install folder (`GetStagedPackagePathByFullName`, which finds the package whichever account installed it; the service runs as SYSTEM). A different program started inside the package doesn't qualify, and neither does a `GlassRea.exe` from another package.
2. The caller is setup's copy of the app, and its program file is exactly `App\GlassRea.exe` in that copy's install folder. Setup restricts that folder so only SYSTEM and administrators can write to it. The Store copy's service never accepts this case.
3. The caller runs elevated, with the Administrators group enabled in its token. Such a caller could already act as SYSTEM, so the pipe gives it nothing new.

Any other caller gets one response, "Glass-Rea's service only answers Glass-Rea's own app.". The pipe then closes, and the program's path is written to the service log. No request from that caller is read.

This is a check of which program is calling, not of which person. Every person signed in at the PC uses the same service through the same app, the way they share one copy of the usage history. Everything the service does on request is something the app offers on screen.

## Every request the service accepts

Each request names an operation (`op`). Anything not listed here is refused with "Unknown request".

| Operation | What it takes | What it does |
|---|---|---|
| `status` | nothing | Version, whether recording is on, import progress |
| `usage`, `hosts`, `live` | a time range, an app id, a network id | Reads recorded use from the service's own database |
| `devices`, `networks`, `alerts` | an alert id to start after | Reads the device list, network list and alerts |
| `block` | an app id, on or off | Blocks or unblocks an app the service has already recorded (see Blocking) |
| `device.rename`, `device.forget` | a MAC address already in the list, a name of up to 64 characters | Renames or forgets a device in the database |
| `network.rename` | a network id already in the list, a name | Renames a network in the database |
| `scan` | nothing | Starts a device scan, if Device discovery is allowed |
| `permissions`, `permissions.set` | the four feature names, each `true` or `false` | Reads or changes the four switches above |
| `history.delete` | nothing | Deletes recorded usage, connections, devices and alerts |
| `settings`, `settings.set` | values for a fixed list of keys | Reads or changes: daily and monthly limits (whole bytes), which alerts to show (on or off), the processor priority of the service itself (`normal`, `above_normal` or `high`), when the month and week start, and the file names counted as VPN tunnels (compared with a program's file name, never run or opened) |

**What can't be passed in.** No request takes a file path, a program, a script, a command line, a registry key, a WFP filter, a firewall rule, a port, an address or a service name. `block` takes an app id. The service looks that id up in its own database, where it recorded the program's path itself, from the ETW events of a process that already ran. Every other value is checked against a fixed form (a number, a known key, `true` or `false`, one of a few words, a short name with no line breaks) before it is stored. The service never starts a program because of a request. The only program it ever starts is `C:\Windows\System32\esentutl.exe`, with fixed arguments, to copy the SRUM database, and that happens only when Past usage from Windows is allowed. JSON is read into fixed record types (`System.Text.Json`, no polymorphic types).

## Blocking: WFP filters, not firewall rules

The Store copy blocks with **dynamic WFP filters only** (`src/GlassRea.Service/Blocking/WfpBlocker.cs`). It opens the filter engine with `FWPM_SESSION_FLAG_DYNAMIC`, adds its own sublayer, and for each blocked app adds block filters at `ALE_AUTH_CONNECT_V4/V6` and `ALE_AUTH_RECV_ACCEPT_V4/V6`. The filters match the app's id (`FwpmGetAppIdFromFileName0`). A service inside svchost is matched by its service SID too, and a Store app by its package SID. Windows removes everything a dynamic session added as soon as the session closes. That happens when the service stops, and also when the process ends in any other way.

The list of blocked apps is kept in the service's database (`apps.blocked`). At every start the service adds the filters again (`FirewallManager.RestoreBlocks`), if Blocking apps is still allowed.

The Store copy creates **one Windows Firewall rule**, and it is an allow rule, not a block: "Glass-Rea device discovery". It allows inbound UDP 67 and 5353, from the local subnet only, on private and domain networks, to `Service\GlassRea.Service.exe` only. It exists only while Device discovery is allowed and the service is running. See below for when it is removed.

Setup's copy (not the Store package) blocks with Windows Firewall rules in the group "Glass-Rea" instead, and its uninstaller removes every rule in that group.

## Cleanup

| When | What happens |
|---|---|
| An app is unblocked | Its filters are deleted by id at once, and it's marked unblocked in the database |
| Blocking apps is turned off | The WFP session is closed, which removes every filter, and the list of blocks is cleared |
| Device discovery is turned off | Both sockets close and the discovery firewall rule is removed |
| The service stops (stop, update, uninstall, shutdown) | The WFP session closes, so every block filter goes. The discovery rule is removed as the service stops, and added again at its next start |
| The service crashes or is killed | Windows closes the dynamic session with the process, so every block filter still goes. The discovery rule can be left behind. It is rewritten at the next start, or removed then if Device discovery is off. Left behind, it only lets the local subnet reach UDP 67 and 5353 on a program that isn't running |
| The app crashes or closes | Nothing changes. The app holds no filters, rules or handles the service depends on, and the service never needs the app to clean up |
| An update | Windows stops the old service (filters gone, discovery rule removed) and starts the new one, which adds back the blocks still listed |
| Uninstall | Windows stops the service first, so no block filter and no discovery rule stays. The recorded history stays in `C:\ProgramData\Glass-Rea` (this is in `PRIVACY.md`) |

## Why LocalSystem

A packaged service's `StartAccount` can be `localSystem`, `localService` or `networkService`. We ran each operation the service performs as each of the three, on Windows 11 (build 26200). Each one ran from a scheduled task under that account.

| Operation (feature) | LocalService | NetworkService | LocalSystem |
|---|---|---|---|
| Add a WFP sublayer in a dynamic session (Blocking apps) | Access denied (`FwpmSubLayerAdd0` returned 5) | Access denied (5) | Works |
| Start the real-time ETW session on Kernel-Network (Usage per app) | Works | Access denied | Works |
| Read the path of running programs, to name the app behind each packet (Usage per app) | 2 of 289 processes | 13 of 289 | 268 of 290 (the rest are protected processes) |
| Open `C:\Windows\System32\sru\SRUDB.dat` (Past usage from Windows) | Denied. The folder grants only Administrators, SYSTEM and `NT SERVICE\DPS` | Denied | Works |
| Copy it with `esentutl /y /vss` (Past usage from Windows) | Fails, `JET_errOSSnapshotNotAllowed` | Fails, same error | Works |
| Add or remove a Windows Firewall rule (Device discovery) | Access denied (`E_ACCESSDENIED`) | Access denied | Works |

So LocalService could start the capture session, but it couldn't say which app each packet belongs to, and it couldn't block, import past usage or open the discovery ports. NetworkService can't even start the capture. Glass-Rea needs all of these to work.

Running at a lower identity would mean giving up the features above. The service drops nothing it doesn't need. It has no network listener apart from the two discovery ports. It starts no programs apart from the `esentutl.exe` copy. Each feature waits for its own switch.

## How to check this

- The pipe's access list and the caller rules are covered by tests in `tests/GlassRea.Tests/PipeTests.cs`. Among them: setup's app is let in only from its install folder, the Store app only by its package and program, an app that connects to a pipe not served by the service hangs up, a refused program gets its answer after it has sent its request (the pipe has no buffer, so answering first left both sides waiting), a request the service never answers fails after 30 seconds, and the Store family name matches the package manifest's identity.
- The WFP structure layouts are checked against the Windows SDK in `tests/GlassRea.Tests/WfpTests.cs`.
- `scripts/sandbox/test-store.ps1` installs the package in Windows Sandbox. It checks that blocks stop traffic, come back after a restart, are gone while the service is stopped, and are undone by unblocking.

Support: temrevil@gmail.com, https://temrevil.com/projects/glass-rea
