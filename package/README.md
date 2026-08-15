# Xom Nghien Bootstrap

Install this package once with r2modman or Thunderstore Mod Manager. Before normal BepInEx plugins initialize, it downloads the signed live manifest for the selected Xom Nghien server and synchronizes its required mods and centrally managed configuration files.

On a dedicated server it continues polling every 60 seconds. Config changes are written live. When the package set changes, it installs the new files, requests a world save, waits 60 seconds, and quits so the server's process supervisor can restart it. Configure Docker with `restart: unless-stopped` or systemd with `Restart=always`; without a supervisor, automatic quit is not an automatic restart.

Runtime settings are generated at `BepInEx/config/com.xomnghien.servermods.runtime-updater.cfg`. Set `AutoRestartForModChanges = false` to stage changes without quitting, or `RestartForConfigChanges = true` for mods that only read configuration during startup.

The bootstrap owns only `BepInEx/plugins/XomNghienManaged` and the exact config paths published by the server. It does not remove personal or unrelated plugins.

Managed configs are client-visible and must never contain server passwords or other secrets.

If you need another Xom Nghien server, edit `BepInEx/config/XomNghienBootstrap/bootstrap.cfg` and change `ServerId`.

Packages containing BepInEx `core`, `patchers`, or `monomod` files must still be installed through the Xom Nghien launcher because they cannot safely become active after preloading has begun.
