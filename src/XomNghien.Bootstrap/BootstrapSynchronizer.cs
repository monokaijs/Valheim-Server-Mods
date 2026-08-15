using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace XomNghien.Bootstrap;

/// <summary>Synchronizes the managed package and configuration manifest.</summary>
public static class BootstrapSynchronizer
{
    private const long MaximumArchiveBytes = 500L * 1024 * 1024;
    private const int MaximumManifestBytes = 8 * 1024 * 1024;
    private static readonly object SynchronizeLock = new();

    /// <summary>Checks and applies the latest manifest.</summary>
    public static SynchronizationResult Run()
    {
        lock (SynchronizeLock)
        {
            var context = CreateContext();
            ApplyPendingLocked(context);
            return RunLocked(context);
        }
    }

    private static SynchronizationResult RunLocked(BootstrapContext context)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var previous = LoadState(context.StatePath);
        if (!context.Settings.HasManifestUrl)
        {
            BootstrapLog.Info("No ManifestUrl is configured; waiting for a server-relayed manifest");
            return SynchronizationResult.Unchanged(previous.Revision);
        }

        BootstrapLog.Info("Checking configured manifest for managed mod and config updates");
        var conditionalRevision = LocalStateIsHealthy(context.BepInExRoot, previous) ? previous.Revision : "";
        var manifestBytes = DownloadManifest(context.Settings, conditionalRevision);
        if (manifestBytes == null)
        {
            BootstrapLog.Info($"Revision {ShortRevision(previous.Revision)} is already current");
            return SynchronizationResult.Unchanged(previous.Revision);
        }
        return ApplyManifestLocked(context, previous, manifestBytes);
    }

    /// <summary>Downloads and stages a manifest relayed by the connected game server.</summary>
    public static SynchronizationResult StageRelayedManifest(string manifestJson)
    {
        if (manifestJson == null) throw new ArgumentNullException(nameof(manifestJson));
        var manifestBytes = Encoding.UTF8.GetBytes(manifestJson);
        if (manifestBytes.Length > MaximumManifestBytes) throw new InvalidDataException("Relayed manifest exceeds the 8 MiB limit");
        lock (SynchronizeLock)
        {
            var context = CreateContext();
            return StageManifestLocked(context, LoadState(context.StatePath), manifestBytes);
        }
    }

    /// <summary>Checks the configured server manifest and stages package changes for restart.</summary>
    public static SynchronizationResult StageConfiguredUpdate()
    {
        lock (SynchronizeLock)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var context = CreateContext();
            var previous = LoadState(context.StatePath);
            if (!context.Settings.HasManifestUrl) return SynchronizationResult.Unchanged(previous.Revision);
            var conditionalRevision = LocalStateIsHealthy(context.BepInExRoot, previous) ? previous.Revision : "";
            var manifestBytes = DownloadManifest(context.Settings, conditionalRevision);
            return manifestBytes == null
                ? SynchronizationResult.Unchanged(previous.Revision)
                : StageManifestLocked(context, previous, manifestBytes);
        }
    }

    /// <summary>Returns the last validated manifest for relay to connecting clients.</summary>
    public static string? ReadRelayManifest()
    {
        var context = CreateContext();
        if (!context.Settings.HasManifestUrl || !File.Exists(context.LastManifestPath)) return null;
        var info = new FileInfo(context.LastManifestPath);
        if (info.Length > MaximumManifestBytes) throw new InvalidDataException("Manifest exceeds the 8 MiB relay limit");
        return File.ReadAllText(context.LastManifestPath, Encoding.UTF8);
    }

    private static SynchronizationResult ApplyManifestLocked(
        BootstrapContext context,
        BootstrapState previous,
        byte[] manifestBytes)
    {
        var manifest = Json.Read<BootstrapManifest>(manifestBytes);
        var manifestId = ManifestIdentity(manifest);
        ValidateManifest(manifest, manifestId, previous);

        if (string.Equals(previous.Revision, manifest.Revision, StringComparison.Ordinal)
            && LocalStateIsHealthy(context.BepInExRoot, previous))
        {
            previous.ManifestId = manifestId;
            previous.GeneratedAt = manifest.GeneratedAt;
            Json.WriteFile(context.StatePath, previous);
            WriteLastManifest(context.LastManifestPath, manifestBytes);
            BootstrapLog.Info($"Revision {ShortRevision(manifest.Revision)} is already installed");
            return SynchronizationResult.Unchanged(manifest.Revision);
        }

        var workRoot = Path.Combine(context.StateRoot, "staging-" + Guid.NewGuid().ToString("N"));
        var stagedPlugins = Path.Combine(workRoot, "plugins");
        var stagedDefaults = Path.Combine(workRoot, "defaults");
        Directory.CreateDirectory(stagedPlugins);
        Directory.CreateDirectory(stagedDefaults);
        try
        {
            var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var package in manifest.Packages)
            {
                var archive = GetPackageArchive(context.Settings, context.StateRoot, package);
                PackageInstaller.Extract(archive, package, stagedPlugins, stagedDefaults, owners);
            }
            Apply(context.BepInExRoot, stagedPlugins, stagedDefaults, manifest, manifestId, previous, context.StatePath);
            WriteLastManifest(context.LastManifestPath, manifestBytes);
            BootstrapLog.Info($"Installed revision {ShortRevision(manifest.Revision)} with {manifest.Packages.Count} packages and {manifest.Configs.Count} managed configs");
            var packagesChanged = PackageSetsDiffer(previous.Packages, manifest.Packages.Select(package => package.Coordinate));
            return SynchronizationResult.Applied(
                manifest.Revision,
                packagesChanged,
                ConfigSetsDiffer(previous.ManagedConfigs, manifest.Configs) || !packagesChanged);
        }
        finally
        {
            TryDeleteDirectory(workRoot);
        }
    }

    private static SynchronizationResult StageManifestLocked(
        BootstrapContext context,
        BootstrapState previous,
        byte[] manifestBytes)
    {
        var manifest = Json.Read<BootstrapManifest>(manifestBytes);
        var manifestId = ManifestIdentity(manifest);
        ValidateManifest(manifest, manifestId, previous);

        if (string.Equals(previous.Revision, manifest.Revision, StringComparison.Ordinal)
            && string.Equals(previous.ManifestId, manifestId, StringComparison.Ordinal)
            && LocalStateIsHealthy(context.BepInExRoot, previous))
            return SynchronizationResult.Unchanged(manifest.Revision);

        foreach (var package in manifest.Packages)
            GetPackageArchive(context.Settings, context.StateRoot, package);

        var packagesChanged = PackageSetsDiffer(previous.Packages, manifest.Packages.Select(package => package.Coordinate));
        if (!packagesChanged)
            return ApplyManifestLocked(context, previous, manifestBytes);

        var temporary = context.PendingManifestPath + ".new";
        File.WriteAllBytes(temporary, manifestBytes);
        AtomicFile.Replace(temporary, context.PendingManifestPath);
        BootstrapLog.Info($"Staged revision {ShortRevision(manifest.Revision)} for the next process start");
        return SynchronizationResult.Applied(manifest.Revision, true, true);
    }

    private static void ApplyPendingLocked(BootstrapContext context)
    {
        if (!File.Exists(context.PendingManifestPath)) return;
        BootstrapLog.Info("Applying the pending managed mod revision before plugin loading");
        var manifestBytes = File.ReadAllBytes(context.PendingManifestPath);
        ApplyManifestLocked(context, LoadState(context.StatePath), manifestBytes);
        File.Delete(context.PendingManifestPath);
    }

    private static void WriteLastManifest(string path, byte[] manifestBytes)
    {
        var temporary = path + ".new";
        File.WriteAllBytes(temporary, manifestBytes);
        AtomicFile.Replace(temporary, path);
    }

    private static BootstrapContext CreateContext()
    {
        var patcherPath = Assembly.GetExecutingAssembly().Location;
        var patcherDirectory = Path.GetDirectoryName(patcherPath) ?? throw new InvalidOperationException("Bootstrap assembly has no directory");
        var bepinexRoot = Directory.GetParent(patcherDirectory)?.FullName ?? throw new InvalidOperationException("Cannot find BepInEx root");
        var configRoot = Path.Combine(bepinexRoot, "config", "ServerModBootstrap");
        var stateRoot = Path.Combine(bepinexRoot, "xom-bootstrap");
        BootstrapLog.Initialize(stateRoot);
        return new BootstrapContext(
            bepinexRoot,
            stateRoot,
            Path.Combine(stateRoot, "state.json"),
            Path.Combine(stateRoot, "last-manifest.json"),
            Path.Combine(stateRoot, "pending-manifest.json"),
            BootstrapSettings.Load(Path.Combine(configRoot, "bootstrap.cfg")));
    }

    private static BootstrapState LoadState(string statePath)
    {
        if (!File.Exists(statePath)) return new BootstrapState();
        try { return Json.ReadFile<BootstrapState>(statePath); }
        catch (Exception error)
        {
            BootstrapLog.Error("Ignoring corrupt local bootstrap state", error);
            return new BootstrapState();
        }
    }

    private static byte[]? DownloadManifest(BootstrapSettings settings, string previousRevision)
    {
        using var client = CreateClient(settings.RequestTimeoutSeconds);
        using var request = new HttpRequestMessage(HttpMethod.Get, settings.ManifestUrl);
        if (!string.IsNullOrWhiteSpace(previousRevision))
            request.Headers.TryAddWithoutValidation("If-None-Match", "\"" + previousRevision + "\"");
        using var response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        if (response.StatusCode == HttpStatusCode.NotModified) return null;
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumManifestBytes)
            throw new InvalidDataException("Manifest exceeds the 8 MiB limit");
        using var source = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        using var target = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (target.Length + read > MaximumManifestBytes)
                throw new InvalidDataException("Manifest exceeds the 8 MiB limit");
            target.Write(buffer, 0, read);
        }
        return target.ToArray();
    }

    private static string GetPackageArchive(BootstrapSettings settings, string stateRoot, ManifestPackage package)
    {
        ValidatePackage(package);
        var cacheRoot = Path.Combine(stateRoot, "cache");
        Directory.CreateDirectory(cacheRoot);
        var cacheName = Hex(SHA256.Create().ComputeHash(System.Text.Encoding.UTF8.GetBytes(package.Coordinate))) + ".zip";
        var destination = Path.Combine(cacheRoot, cacheName);
        if (File.Exists(destination))
        {
            try
            {
                PackageInstaller.ValidateArchive(destination, package);
                return destination;
            }
            catch
            {
                File.Delete(destination);
            }
        }

        BootstrapLog.Info("Downloading " + package.Coordinate);
        using var client = CreateClient(settings.RequestTimeoutSeconds);
        using var response = client.GetAsync(package.DownloadUrl, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumArchiveBytes)
            throw new InvalidDataException(package.Coordinate + " exceeds the 500 MiB archive limit");
        var temporary = destination + ".download";
        using (var source = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
        using (var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += read;
                if (written > MaximumArchiveBytes) throw new InvalidDataException(package.Coordinate + " exceeds the 500 MiB archive limit");
                target.Write(buffer, 0, read);
            }
        }
        try
        {
            PackageInstaller.ValidateArchive(temporary, package);
            AtomicFile.Replace(temporary, destination);
            return destination;
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    private static HttpClient CreateClient(int timeoutSeconds)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ServerModBootstrap/2.0");
        return client;
    }

    private static void Apply(
        string bepinexRoot,
        string stagedPlugins,
        string stagedDefaults,
        BootstrapManifest manifest,
        string manifestId,
        BootstrapState previous,
        string statePath)
    {
        var pluginsRoot = Path.Combine(bepinexRoot, "plugins");
        var managedPlugins = Path.Combine(pluginsRoot, "XomNghienManaged");
        var backupPlugins = Path.Combine(pluginsRoot, "XomNghienManaged.backup");
        Directory.CreateDirectory(pluginsRoot);
        TryDeleteDirectory(backupPlugins);
        if (Directory.Exists(managedPlugins)) Directory.Move(managedPlugins, backupPlugins);

        var configBackups = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Directory.Move(stagedPlugins, managedPlugins);
            ApplyPackageDefaults(bepinexRoot, stagedDefaults);
            ApplyManagedConfigs(bepinexRoot, manifest.Configs, previous.ManagedConfigs, configBackups);
            Json.WriteFile(statePath, new BootstrapState
            {
                ManifestId = manifestId,
                Revision = manifest.Revision,
                GeneratedAt = manifest.GeneratedAt,
                Packages = manifest.Packages.Select(package => package.Coordinate).ToList(),
                ManagedConfigs = manifest.Configs.Select(config => config.Path).ToList(),
                ManagedConfigHashes = manifest.Configs.ToDictionary(config => config.Path, config => config.Sha256, StringComparer.OrdinalIgnoreCase),
            });
            TryDeleteDirectory(backupPlugins);
        }
        catch
        {
            TryDeleteDirectory(managedPlugins);
            if (Directory.Exists(backupPlugins)) Directory.Move(backupPlugins, managedPlugins);
            RestoreConfigs(bepinexRoot, configBackups);
            throw;
        }
    }

    private static void ApplyPackageDefaults(string bepinexRoot, string stagedDefaults)
    {
        if (!Directory.Exists(stagedDefaults)) return;
        foreach (var source in Directory.GetFiles(stagedDefaults, "*", SearchOption.AllDirectories))
        {
            var relative = RelativePath(stagedDefaults, source);
            var destination = SafeConfigTarget(bepinexRoot, relative);
            if (File.Exists(destination)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }
    }

    private static void ApplyManagedConfigs(
        string bepinexRoot,
        IEnumerable<ManifestConfig> configs,
        IEnumerable<string> previousPaths,
        IDictionary<string, byte[]?> backups)
    {
        var nextPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var config in configs)
        {
            var target = SafeConfigTarget(bepinexRoot, config.Path);
            nextPaths.Add(config.Path);
            BackupOnce(target, backups);
            var contents = Convert.FromBase64String(config.ContentBase64);
            if (!FixedTimeEquals(Hex(SHA256.Create().ComputeHash(contents)), config.Sha256))
                throw new InvalidDataException("Managed config hash mismatch for " + config.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = target + ".xn-new";
            File.WriteAllBytes(temporary, contents);
            AtomicFile.Replace(temporary, target);
        }

        foreach (var path in previousPaths.Where(path => !nextPaths.Contains(path)))
        {
            var target = SafeConfigTarget(bepinexRoot, path);
            BackupOnce(target, backups);
            if (File.Exists(target)) File.Delete(target);
        }
    }

    private static void BackupOnce(string target, IDictionary<string, byte[]?> backups)
    {
        if (!backups.ContainsKey(target)) backups[target] = File.Exists(target) ? File.ReadAllBytes(target) : null;
    }

    private static void RestoreConfigs(string bepinexRoot, IDictionary<string, byte[]?> backups)
    {
        foreach (var pair in backups)
        {
            if (!pair.Key.StartsWith(Path.Combine(bepinexRoot, "config") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (pair.Value == null)
            {
                if (File.Exists(pair.Key)) File.Delete(pair.Key);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(pair.Key)!);
                File.WriteAllBytes(pair.Key, pair.Value);
            }
        }
    }

    private static bool ConfigsAreCurrent(string bepinexRoot, IEnumerable<ManifestConfig> configs)
    {
        foreach (var config in configs)
        {
            var target = SafeConfigTarget(bepinexRoot, config.Path);
            if (!File.Exists(target)) return false;
            using var stream = File.OpenRead(target);
            if (!FixedTimeEquals(Hex(SHA256.Create().ComputeHash(stream)), config.Sha256)) return false;
        }
        return true;
    }

    private static bool LocalStateIsHealthy(string bepinexRoot, BootstrapState state)
    {
        if (string.IsNullOrWhiteSpace(state.Revision)
            || !Directory.Exists(Path.Combine(bepinexRoot, "plugins", "XomNghienManaged"))
            || state.ManagedConfigHashes == null
            || state.ManagedConfigHashes.Count != state.ManagedConfigs.Count)
            return false;

        foreach (var pair in state.ManagedConfigHashes)
        {
            var target = SafeConfigTarget(bepinexRoot, pair.Key);
            if (!File.Exists(target)) return false;
            using var stream = File.OpenRead(target);
            if (!FixedTimeEquals(Hex(SHA256.Create().ComputeHash(stream)), pair.Value)) return false;
        }
        return true;
    }

    internal static string SafeConfigTarget(string bepinexRoot, string relative)
    {
        var normalized = relative.Replace('\\', '/');
        if (normalized.Length == 0 || normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.Split('/').Any(part => part.Length == 0 || part == "." || part == ".."
                || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal)
                || part.IndexOfAny(new[] { '<', '>', ':', '"', '|', '?', '*' }) >= 0
                || part.Any(character => character < 32)))
            throw new InvalidDataException("Unsafe managed config path: " + relative);
        var configRoot = Path.GetFullPath(Path.Combine(bepinexRoot, "config"));
        var target = Path.GetFullPath(Path.Combine(configRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(configRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsafe managed config path: " + relative);
        return target;
    }

    private static void ValidateManifest(BootstrapManifest manifest, string manifestId, BootstrapState previous)
    {
        if (manifest.SchemaVersion != 1) throw new InvalidDataException("Unsupported bootstrap manifest schema");
        if (manifestId.Length == 0 || manifestId.Length > 200) throw new InvalidDataException("Bootstrap manifest identity is invalid");
        if (manifest.Revision.Length != 64 || !manifest.Revision.All(IsHex)) throw new InvalidDataException("Bootstrap revision is invalid");
        if (!DateTimeOffset.TryParse(manifest.GeneratedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var generatedAt))
            throw new InvalidDataException("Bootstrap manifest timestamp is invalid");
        if (generatedAt > DateTimeOffset.UtcNow.AddMinutes(10)) throw new InvalidDataException("Bootstrap manifest timestamp is in the future");
        if (string.Equals(previous.ManifestId, manifestId, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(previous.GeneratedAt)
            && DateTimeOffset.TryParse(previous.GeneratedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var previousTimestamp)
            && generatedAt < previousTimestamp)
            throw new InvalidDataException("Bootstrap manifest is older than the installed manifest");
        if (manifest.Packages.Count > 500) throw new InvalidDataException("Bootstrap manifest contains too many packages");
        if (manifest.Configs.Count > 100) throw new InvalidDataException("Bootstrap manifest contains too many configs");
        var coordinates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in manifest.Packages)
            if (!coordinates.Add(package.Coordinate)) throw new InvalidDataException("Duplicate package " + package.Coordinate);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var config in manifest.Configs)
            if (!paths.Add(config.Path)) throw new InvalidDataException("Duplicate config " + config.Path);
    }

    private static string ManifestIdentity(BootstrapManifest manifest) =>
        !string.IsNullOrWhiteSpace(manifest.ManifestId) ? manifest.ManifestId.Trim() : manifest.ServerId.Trim();

    private static void ValidatePackage(ManifestPackage package)
    {
        if (package.Coordinate != $"{package.Namespace}-{package.PackageName}-{package.VersionNumber}")
            throw new InvalidDataException("Package coordinate fields disagree");
        if (!Uri.TryCreate(package.DownloadUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host.Equals("thunderstore.io", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".thunderstore.io", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Package download URL is not trusted");
        if (package.FileSize > MaximumArchiveBytes) throw new InvalidDataException(package.Coordinate + " exceeds the package limit");
    }

    internal static string RelativePath(string root, string path)
    {
        var rootUri = new Uri(AppendSeparator(Path.GetFullPath(root)));
        return Uri.UnescapeDataString(rootUri.MakeRelativeUri(new Uri(Path.GetFullPath(path))).ToString()).Replace('/', Path.DirectorySeparatorChar);
    }

    private static string AppendSeparator(string path) => path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ? path : path + Path.DirectorySeparatorChar;
    private static string ShortRevision(string revision) => revision.Substring(0, Math.Min(12, revision.Length));
    private static bool IsHex(char value) => (value >= '0' && value <= '9') || (value >= 'a' && value <= 'f');
    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    private static bool FixedTimeEquals(string left, string right)
    {
        if (left.Length != right.Length) return false;
        var difference = 0;
        for (var index = 0; index < left.Length; index++) difference |= left[index] ^ right[index];
        return difference == 0;
    }

    internal static bool PackageSetsDiffer(IEnumerable<string> previous, IEnumerable<string> current)
    {
        return !new HashSet<string>(previous, StringComparer.OrdinalIgnoreCase)
            .SetEquals(current);
    }

    private static bool ConfigSetsDiffer(IEnumerable<string> previousPaths, IEnumerable<ManifestConfig> current)
    {
        var previous = new HashSet<string>(previousPaths, StringComparer.OrdinalIgnoreCase);
        return !previous.SetEquals(current.Select(config => config.Path));
    }
    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception error) { Debug.WriteLine(error); }
    }

    private sealed class BootstrapContext
    {
        public BootstrapContext(
            string bepinExRoot,
            string stateRoot,
            string statePath,
            string lastManifestPath,
            string pendingManifestPath,
            BootstrapSettings settings)
        {
            BepInExRoot = bepinExRoot;
            StateRoot = stateRoot;
            StatePath = statePath;
            LastManifestPath = lastManifestPath;
            PendingManifestPath = pendingManifestPath;
            Settings = settings;
        }

        public string BepInExRoot { get; }
        public string StateRoot { get; }
        public string StatePath { get; }
        public string LastManifestPath { get; }
        public string PendingManifestPath { get; }
        public BootstrapSettings Settings { get; }
    }
}

/// <summary>Describes the effects of one synchronization check.</summary>
public sealed class SynchronizationResult
{
    private SynchronizationResult(string revision, bool changed, bool packagesChanged, bool configsChanged)
    {
        Revision = revision;
        Changed = changed;
        PackagesChanged = packagesChanged;
        ConfigsChanged = configsChanged;
    }

    /// <summary>The active manifest revision.</summary>
    public string Revision { get; }
    /// <summary>Whether a new revision was installed.</summary>
    public bool Changed { get; }
    /// <summary>Whether the installed package coordinates changed.</summary>
    public bool PackagesChanged { get; }
    /// <summary>Whether managed configuration may have changed.</summary>
    public bool ConfigsChanged { get; }

    internal static SynchronizationResult Unchanged(string revision) => new(revision, false, false, false);
    internal static SynchronizationResult Applied(string revision, bool packagesChanged, bool configsChanged) =>
        new(revision, true, packagesChanged, configsChanged);
}
