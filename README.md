# Xom Nghien Server Mods Bootstrap

This repository builds the one-time Thunderstore package that synchronizes the live Valheim server mod list and managed configs from `xom-nghien-web`.

## Trust setup

Generate a signing key once:

```sh
node scripts/generate-signing-key.mjs
```

The private PEM is written to the gitignored `.secrets` directory. Put its base64 value in the web deployment secret `XN_BOOTSTRAP_SIGNING_PRIVATE_KEY_BASE64`. The matching public XML is packaged with the bootstrap and must be committed/published. Do not regenerate the key unless you are intentionally rotating trust and publishing a new bootstrap version.

Apply `packages/db/migrations/025_add_server_managed_configs.sql` in `xom-nghien-web`, then add complete config files from the server's admin management page. Paths are relative to `BepInEx/config`.

Managed config contents are public to players by design. Never store server passwords, API keys, private signing material, or other secrets in them.

## Build and test

```sh
dotnet run --project tests/XomNghien.Bootstrap.Tests/XomNghien.Bootstrap.Tests.csproj -c Release
./scripts/package.sh
```

The Thunderstore-ready ZIP and SHA-256 file are written to `artifacts/`.

## Runtime behavior

- The manifest is fetched from `/api/launcher/v1/servers/{serverId}/bootstrap` and verified with RSA-SHA256.
- Exact required packages and their transitive dependencies are installed beneath `BepInEx/plugins/XomNghienManaged`.
- Package-provided default configs are installed only when absent. Server-managed configs always win.
- Removed server-managed config paths are removed only when the prior bootstrap state proves ownership.
- A failed fetch, signature, download, or extraction leaves the last-known-good installation in place.
- Packages that install `core`, `patchers`, or `monomod` files are rejected; use the existing launcher for those rare packages.
- Dedicated servers poll the signed manifest every 60 seconds while running.
- Config-only revisions are applied live. Mods must implement their own config file watcher to observe those values without a restart.
- Package revisions are installed immediately, followed by a world-save request and a controlled process quit after 60 seconds.
- The Valheim process must be managed by Docker (`restart: unless-stopped`/`always`), systemd (`Restart=always`), or an equivalent host panel for automatic restart. This is a one-time host setting, not per-update SSH work.

Runtime behavior can be changed in `BepInEx/config/com.xomnghien.servermods.runtime-updater.cfg`. Client processes never poll or auto-restart; r2modman clients synchronize during their normal startup preloader phase.
