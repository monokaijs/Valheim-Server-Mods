# Server Mod Bootstrap

Install this package once on every Valheim client and dedicated server. Clients require no configuration.

Each dedicated server sets its complete HTTPS `ManifestUrl` in `BepInEx/config/ServerModBootstrap/bootstrap.cfg`. At startup and every 60 seconds, the server downloads and validates that JSON manifest. When a client connects, the server relays its active manifest through an early peer RPC. Schema v2 configs can target `server`, `client`, or `both`; server-only contents are removed before relay and use a separate revision from the client view.

If the client's managed package revision differs, the bootstrap downloads and verifies the exact Thunderstore packages, stages the update, returns the player to the menu, and asks them to restart Valheim. The preloader applies the staged files before BepInEx loads plugins on the next launch. Connecting again then uses the correct mod set.

The bootstrap owns only `BepInEx/plugins/XomNghienManaged` and exact config paths declared by the manifest. Personal and unrelated plugins are not removed.

No signing key, account, server ID, or client configuration is required. The dedicated server trusts its configured HTTPS endpoint; clients trust the manifest relayed by the Valheim server they are connecting to. Packages must use HTTPS Thunderstore download URLs.

Packages containing BepInEx `core`, `patchers`, or `monomod` files are rejected because early-loader components cannot be changed safely by a running process.
