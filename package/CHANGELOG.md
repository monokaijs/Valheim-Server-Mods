# 1.1.0

- Polls the signed manifest every 60 seconds while running as a dedicated server.
- Installs new, updated, and removed managed mods without SSH access.
- Saves and quits after plugin changes so a process supervisor can restart Valheim with the new assemblies.
- Applies config-only updates live and allows an optional restart for mods that do not watch their config files.
- Uses manifest ETags to avoid downloading unchanged payloads.

# 1.0.0

- Initial signed live manifest synchronization.
- Transitive Thunderstore dependency installation.
- Centrally managed configuration files applied before plugin initialization.
- Isolated managed plugin directory and last-known-good failure behavior.
