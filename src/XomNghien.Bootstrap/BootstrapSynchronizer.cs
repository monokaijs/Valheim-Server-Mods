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

namespace XomNghien.Bootstrap;

/// <summary>Synchronizes the signed managed package and configuration manifest.</summary>
public static class BootstrapSynchronizer
{
    private const long MaximumArchiveBytes = 500L * 1024 * 1024;
    private static readonly object SynchronizeLock = new();

    /// <summary>Checks and applies the latest signed manifest.</summary>
    public static SynchronizationResult Run()
    {
        lock (SynchronizeLock) return RunLocked();
    }

    private static SynchronizationResult RunLocked()
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var patcherPath = Assembly.GetExecutingAssembly().Location;
        var patcherDirectory = Path.GetDirectoryName(patcherPath) ?? throw new InvalidOperationException("Bootstrap assembly has no directory");
        var bepinexRoot = Directory.GetParent(patcherDirectory)?.FullName ?? throw new InvalidOperationException("Cannot find BepInEx root");
        var configRoot = Path.Combine(bepinexRoot, "config", "XomNghienBootstrap");
        var stateRoot = Path.Combine(bepinexRoot, "xom-bootstrap");
        BootstrapLog.Initialize(stateRoot);

        var settings = BootstrapSettings.Load(Path.Combine(configRoot, "bootstrap.cfg"));
        var publicKeyPath = Path.Combine(configRoot, "trusted-public-key.xml");
        if (!File.Exists(publicKeyPath)) throw new FileNotFoundException("Trusted bootstrap public key is missing", publicKeyPath);
        var statePath = Path.Combine(stateRoot, "state.json");
        var previous = LoadState(statePath);

        BootstrapLog.Info($"Checking server {settings.ServerId} for managed mod and config updates");
        var conditionalRevision = LocalStateIsHealthy(bepinexRoot, previous) ? previous.Revision : "";
        var envelopeBytes = DownloadManifest(settings, conditionalRevision);
        if (envelopeBytes == null)
        {
            BootstrapLog.Info($"Revision {ShortRevision(previous.Revision)} is already current");
            return SynchronizationResult.Unchanged(previous.Revision);
        }
        var envelope = Json.Read<SignedEnvelope>(envelopeBytes);
        var payloadBytes = SignatureVerifier.Verify(envelope, settings.TrustedKeyId, publicKeyPath);
        var manifest = Json.Read<BootstrapManifest>(payloadBytes);
        ValidateManifest(manifest, settings.ServerId, previous.GeneratedAt);

        if (string.Equals(previous.Revision, manifest.Revision, StringComparison.Ordinal)
            && Directory.Exists(Path.Combine(bepinexRoot, "plugins", "XomNghienManaged"))
            && ConfigsAreCurrent(bepinexRoot, manifest.Configs))
        {
            BootstrapLog.Info($"Revision {ShortRevision(manifest.Revision)} is already installed");
            return SynchronizationResult.Unchanged(manifest.Revision);
        }

        var workRoot = Path.Combine(stateRoot, "staging-" + Guid.NewGuid().ToString("N"));
        var stagedPlugins = Path.Combine(workRoot, "plugins");
        var stagedDefaults = Path.Combine(workRoot, "defaults");
        Directory.CreateDirectory(stagedPlugins);
        Directory.CreateDirectory(stagedDefaults);
        try
        {
            var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var package in manifest.Packages)
            {
                var archive = GetPackageArchive(settings, stateRoot, package);
                PackageInstaller.Extract(archive, package, stagedPlugins, stagedDefaults, owners);
            }
            Apply(bepinexRoot, stagedPlugins, stagedDefaults, manifest, previous, statePath);
            File.WriteAllBytes(Path.Combine(stateRoot, "last-manifest.json"), envelopeBytes);
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
        var url = $"{settings.ApiBaseUrl}/api/launcher/v1/servers/{Uri.EscapeDataString(settings.ServerId)}/bootstrap";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(previousRevision))
            request.Headers.TryAddWithoutValidation("If-None-Match", "\"" + previousRevision + "\"");
        using var response = client.SendAsync(request).GetAwaiter().GetResult();
        if (response.StatusCode == HttpStatusCode.NotModified) return null;
        response.EnsureSuccessStatusCode();
        return response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("XomNghienBootstrap/1.0");
        return client;
    }

    private static void Apply(
        string bepinexRoot,
        string stagedPlugins,
        string stagedDefaults,
        BootstrapManifest manifest,
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

    private static void ValidateManifest(BootstrapManifest manifest, string serverId, string previousGeneratedAt)
    {
        if (manifest.SchemaVersion != 1) throw new InvalidDataException("Unsupported bootstrap manifest schema");
        if (!string.Equals(manifest.ServerId, serverId, StringComparison.Ordinal)) throw new InvalidDataException("Bootstrap manifest is for another server");
        if (manifest.Revision.Length != 64 || !manifest.Revision.All(IsHex)) throw new InvalidDataException("Bootstrap revision is invalid");
        if (!DateTimeOffset.TryParse(manifest.GeneratedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var generatedAt))
            throw new InvalidDataException("Bootstrap manifest timestamp is invalid");
        if (generatedAt > DateTimeOffset.UtcNow.AddMinutes(10)) throw new InvalidDataException("Bootstrap manifest timestamp is in the future");
        if (!string.IsNullOrWhiteSpace(previousGeneratedAt)
            && DateTimeOffset.TryParse(previousGeneratedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var previousTimestamp)
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

    /// <summary>The active signed manifest revision.</summary>
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
