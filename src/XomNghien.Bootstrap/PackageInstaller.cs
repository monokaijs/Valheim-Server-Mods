using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace XomNghien.Bootstrap;

internal static class PackageInstaller
{
    private const int MaximumEntries = 20_000;
    private const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;
    private static readonly HashSet<string> Metadata = new(StringComparer.OrdinalIgnoreCase)
    {
        "manifest.json", "README.md", "CHANGELOG.md", "icon.png",
    };

    public static void ValidateArchive(string archivePath, ManifestPackage package)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaximumEntries) throw new InvalidDataException("Package contains too many files");
        foreach (var entry in archive.Entries) ValidateEntry(entry);
        var manifestEntry = archive.Entries.FirstOrDefault(entry => entry.FullName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
        if (manifestEntry == null) throw new InvalidDataException(package.Coordinate + " has no root manifest.json");
        using var stream = manifestEntry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var packageManifest = Json.Read<PackageManifest>(memory.ToArray());
        if (!string.Equals(packageManifest.Name, package.PackageName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(packageManifest.VersionNumber, package.VersionNumber, StringComparison.Ordinal))
            throw new InvalidDataException(package.Coordinate + " archive identity does not match its manifest");
    }

    public static void Extract(
        string archivePath,
        ManifestPackage package,
        string pluginsRoot,
        string defaultsRoot,
        IDictionary<string, string> owners)
    {
        ValidateArchive(archivePath, package);
        using var archive = ZipFile.OpenRead(archivePath);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            ValidateEntry(entry);
            expanded += entry.Length;
            if (expanded > MaximumExpandedBytes) throw new InvalidDataException(package.Coordinate + " exceeds the expanded package limit");
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;

            var path = StripLoaderWrapper(entry.FullName).Replace('\\', '/');
            if (!path.Contains("/") && Metadata.Contains(path)) continue;
            var route = Route(path, package);
            if (route.Kind == InstallKind.RejectEarlyLoader)
                throw new InvalidDataException(package.Coordinate + " contains BepInEx core, patcher, or monomod files and cannot be managed by this bootstrap");
            var root = route.Kind == InstallKind.ConfigDefault ? defaultsRoot : pluginsRoot;
            var normalized = route.RelativePath.Replace('\\', '/');
            var ownershipKey = route.Kind + ":" + normalized;
            if (owners.TryGetValue(ownershipKey, out var owner))
                throw new InvalidDataException(owner + " and " + package.Coordinate + " both install " + normalized);
            owners[ownershipKey] = package.Coordinate;
            var output = SafeOutput(root, route.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using var source = entry.Open();
            using var target = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None);
            source.CopyTo(target);
        }
    }

    private static InstallRoute Route(string rawPath, ManifestPackage package)
    {
        var parts = rawPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count > 0 && parts[0].Equals("BepInEx", StringComparison.OrdinalIgnoreCase)) parts.RemoveAt(0);
        if (parts.Count == 0) return new InstallRoute(InstallKind.Plugin, PackageDirectory(package));
        var first = parts[0];
        if (first.Equals("core", StringComparison.OrdinalIgnoreCase)
            || first.Equals("patchers", StringComparison.OrdinalIgnoreCase)
            || first.Equals("monomod", StringComparison.OrdinalIgnoreCase))
            return new InstallRoute(InstallKind.RejectEarlyLoader, string.Join("/", parts));
        if (first.Equals("config", StringComparison.OrdinalIgnoreCase))
            return new InstallRoute(InstallKind.ConfigDefault, string.Join("/", parts.Skip(1)));
        if (first.Equals("plugins", StringComparison.OrdinalIgnoreCase)) parts.RemoveAt(0);
        return new InstallRoute(InstallKind.Plugin, PackageDirectory(package) + "/" + string.Join("/", parts));
    }

    private static string PackageDirectory(ManifestPackage package) => package.Namespace + "-" + package.PackageName;

    private static string StripLoaderWrapper(string path)
    {
        const string wrapper = "BepInExPack_Valheim/";
        return path.StartsWith(wrapper, StringComparison.OrdinalIgnoreCase) ? path.Substring(wrapper.Length) : path;
    }

    private static void ValidateEntry(ZipArchiveEntry entry)
    {
        var normalized = entry.FullName.Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.Split('/').Any(part => part == ".." || part.Contains(":")))
            throw new InvalidDataException("Package archive contains an unsafe path");
        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixType == 0xA000) throw new InvalidDataException("Package archive contains a symbolic link");
    }

    private static string SafeOutput(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var output = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!output.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Package archive escaped its managed root");
        return output;
    }

    private enum InstallKind { Plugin, ConfigDefault, RejectEarlyLoader }
    private sealed class InstallRoute
    {
        public InstallRoute(InstallKind kind, string relativePath) { Kind = kind; RelativePath = relativePath; }
        public InstallKind Kind { get; }
        public string RelativePath { get; }
    }
}
