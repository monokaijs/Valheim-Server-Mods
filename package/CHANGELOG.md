# 2.0.0

- Replaced the hardcoded website and server ID with a generic HTTPS `ManifestUrl` configured only on dedicated servers.
- Added an early per-peer RPC handshake that relays the server's validated manifest to bootstrap clients.
- Uses plain JSON manifests with no signing keys or secrets.
- Clients require no configuration and stage changed mod sets safely for the next launch.
- Added an in-game restart prompt and disconnects clients before they join with an incompatible loaded mod set.
- Supports switching between independently managed servers by remembering the last relayed manifest identity.

# 1.1.0

- Polls the manifest every 60 seconds while running as a dedicated server.
- Installs new, updated, and removed managed mods without SSH access.
- Saves and quits after plugin changes so a process supervisor can restart Valheim with the new assemblies.
- Applies config-only updates live and allows an optional restart for mods that do not watch their config files.
- Uses manifest ETags to avoid downloading unchanged payloads.

# 1.0.0

- Initial live manifest synchronization.
- Transitive Thunderstore dependency installation.
- Centrally managed configuration files applied before plugin initialization.
- Isolated managed plugin directory and last-known-good failure behavior.
