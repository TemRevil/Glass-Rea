# Security

## Report a vulnerability

Please use GitHub's private vulnerability reporting on this repository. Don't post exploitable details, captured traffic or your usage database in a public issue.

## Threat model

Glass-Rea is a monitor for one PC and the people who use it. The service runs as SYSTEM so it can read the kernel's network events and change Windows Firewall rules. Everything it records stays on the PC, in `C:\ProgramData\Glass-Rea\data.db`; nothing is sent anywhere. Setup's only network use is downloading the .NET 10 runtime from Microsoft when it's missing.

The app talks to the service over a local named pipe that any signed-in user of the PC can open. That is deliberate: any user can see usage, devices and alerts, block or unblock apps, change limits and set the service's processor priority (Normal, Above normal or High, never Realtime). Don't install it on a machine shared with people who shouldn't see or change those. The pipe isn't reachable from the network.

The install folder holds the service's files and is locked to Administrators and SYSTEM, wherever you install it, so a standard user can't replace what SYSTEM runs. The usage database keeps ProgramData's default permissions, so every user of the PC can read it, as they can through the pipe. Firewall rules the app made are removed on uninstall, so nothing stays blocked.

Setup is signed with a self-signed "Tem Revil" certificate that is trusted only on the PC that made it (`scripts\make-cert.ps1`). On any other PC, Windows shows an unknown publisher; check the source and build it yourself if that matters to you.

There is no independent security audit. Host names come from DNS answers seen on the PC and device names from the local network, so both can be spoofed by anyone on that network.
