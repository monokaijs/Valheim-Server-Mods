# Universal Valheim Server Mod Bootstrap

This repository builds a single BepInEx/Thunderstore package installed on both Valheim clients and dedicated servers. A server is configured with an arbitrary JSON manifest URL; clients receive that server's active manifest during connection and never configure a server ID or URL.

## Manifest service

The endpoint returns this plain JSON schema:

```json
{
  "schemaVersion": 1,
  "manifestId": "unique-server-or-cluster-id",
  "revision": "64-character-sha256",
  "generatedAt": "2026-08-16T00:00:00.000Z",
  "packages": [],
  "configs": []
}
```

For backward compatibility, `serverId` may be used instead of `manifestId`. Package entries contain `coordinate`, `namespace`, `packageName`, `versionNumber`, `downloadUrl`, optional `fileSize`, and `dependencies`. Config entries contain a path relative to `BepInEx/config`, `sha256`, and `contentBase64`.

Only HTTPS Thunderstore package URLs are accepted. Packages containing BepInEx `core`, `patchers`, or `monomod` files are rejected.

## Server configuration

Install the same package on each server, then feed that instance its complete endpoint once:

```ini
ManifestUrl = https://example.com/manifests/server-a.json
RequestTimeoutSeconds = 45
```

This is the only server-specific setting. Clients leave `ManifestUrl` empty and do not edit any configuration.

The server checks the endpoint on startup and every 60 seconds. Package changes are downloaded and staged, the world is saved, and the server quits so Docker (`restart: unless-stopped`/`always`), systemd (`Restart=always`), or a hosting panel can restart it with the new assemblies. Config-only changes are applied live unless `RestartForConfigChanges` is enabled.

## Client connection flow

1. The bootstrap registers an early peer RPC on both sides.
2. The server relays its last validated manifest when the client connects.
3. The client validates the manifest and compares its identity and revision.
4. A changed package set downloads into the cache and is staged without touching loaded DLL files.
5. The client returns to the menu and sees a restart prompt.
6. On the next launch, the preloader applies the pending package set before normal plugins load.

Switching between servers works the same way. If their revisions already match, connection continues without a prompt.

## Build and test

```sh
dotnet run --project tests/XomNghien.Bootstrap.Tests/XomNghien.Bootstrap.Tests.csproj -c Release
./scripts/package.sh
```

The Thunderstore-ready ZIP and SHA-256 file are written to `artifacts/`. Build outputs, binaries copied into `package/`, and artifacts are excluded from Git.
