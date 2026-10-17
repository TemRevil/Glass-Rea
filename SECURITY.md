# Security

## Report a vulnerability

Please use GitHub's private vulnerability reporting on this repository. Don't post exploitable details, captured traffic or your usage database in a public issue.

## Threat model

Glass-Rea is a monitor for one PC and the people who use it. The service runs as SYSTEM so it can read the kernel's network events and change Windows Firewall rules. Everything it records stays on the PC, in `C:\ProgramData\Glass-Rea\data.db`; nothing is sent anywhere. Setup's only network use is downloading the .NET 10 runtime from Microsoft when it's missing.

The app talks to the service over a local named pipe that any signed-in user of the PC can open. That is deliberate: any user can see usage, devices and alerts, block or unblock apps, change limits and set the service's processor priority (Normal, Above normal or High, never Realtime). Don't install it on a machine shared with people who shouldn't see or change those. The pipe isn't reachable from the network.

The install folder holds the service's files and is locked to Administrators and SYSTEM, wherever you install it, so a standard user can't replace what SYSTEM runs. The usage database keeps ProgramData's default permissions, so every user of the PC can read it, as they can through the pipe. Firewall rules the app made are removed on uninstall, so nothing stays blocked.

Setup is signed with a self-signed "Tem Revil" certificate that is trusted only on the PC that made it (`scripts\make-cert.ps1`). On any other PC, Windows shows an unknown publisher; check the source and build it yourself if that matters to you.

There is no independent security audit. Host names come from DNS answers seen on the PC and device names from the local network, so both can be spoofed by anyone on that network.

## Local network discovery

To name the devices on your network, the service, every few minutes and only on the network adapter that reaches your router:

- pings each address in the subnet (capped at 1022 addresses);
- asks each device that answered for its name over Bonjour (UDP 5353), NetBIOS (UDP 137) and UPnP (UDP 1900), and fetches a UPnP description only from the device that offered it, over plain HTTP, with no redirects and at most 512 KB;
- tries to connect to a few TCP ports (62078, 7000, 9100, 631, 515, 445, 139, 8008, 8009, 8060, 1400, 554, 5555) and closes the connection at once, sending nothing.

It also listens, without sending, for the DHCP requests (UDP 67) and Bonjour announcements (UDP 5353) devices broadcast. For that it adds one inbound Windows Firewall rule, "Glass-Rea device discovery", limited to the Glass-Rea service, UDP, those two ports and the local subnet; uninstall removes it. Everything received is parsed as untrusted input (bounds-checked, size-capped, XML without DTDs), only ever shown as text, and kept per device on this PC (at most 64 facts per device, forgotten after 30 days). On a network you don't trust, the alerts and names it shows are only as honest as the devices on it.
