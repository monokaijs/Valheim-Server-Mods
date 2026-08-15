using System.Collections.Generic;
using System.Runtime.Serialization;

namespace XomNghien.Bootstrap;

[DataContract]
internal sealed class BootstrapManifest
{
    [DataMember(Name = "schemaVersion", IsRequired = true)] public int SchemaVersion { get; set; }
    [DataMember(Name = "manifestId", EmitDefaultValue = false)] public string ManifestId { get; set; } = "";
    [DataMember(Name = "serverId", EmitDefaultValue = false)] public string ServerId { get; set; } = "";
    [DataMember(Name = "revision", IsRequired = true)] public string Revision { get; set; } = "";
    [DataMember(Name = "generatedAt", IsRequired = true)] public string GeneratedAt { get; set; } = "";
    [DataMember(Name = "packages", IsRequired = true)] public List<ManifestPackage> Packages { get; set; } = new();
    [DataMember(Name = "configs", IsRequired = true)] public List<ManifestConfig> Configs { get; set; } = new();
}

[DataContract]
internal sealed class ManifestPackage
{
    [DataMember(Name = "coordinate", IsRequired = true)] public string Coordinate { get; set; } = "";
    [DataMember(Name = "namespace", IsRequired = true)] public string Namespace { get; set; } = "";
    [DataMember(Name = "packageName", IsRequired = true)] public string PackageName { get; set; } = "";
    [DataMember(Name = "versionNumber", IsRequired = true)] public string VersionNumber { get; set; } = "";
    [DataMember(Name = "downloadUrl", IsRequired = true)] public string DownloadUrl { get; set; } = "";
    [DataMember(Name = "fileSize")] public long? FileSize { get; set; }
    [DataMember(Name = "dependencies", IsRequired = true)] public List<string> Dependencies { get; set; } = new();
}

[DataContract]
internal sealed class ManifestConfig
{
    [DataMember(Name = "path", IsRequired = true)] public string Path { get; set; } = "";
    [DataMember(Name = "sha256", IsRequired = true)] public string Sha256 { get; set; } = "";
    [DataMember(Name = "contentBase64", IsRequired = true)] public string ContentBase64 { get; set; } = "";
}

[DataContract]
internal sealed class BootstrapState
{
    [DataMember(Name = "manifestId", EmitDefaultValue = false)] public string ManifestId { get; set; } = "";
    [DataMember(Name = "revision", IsRequired = true)] public string Revision { get; set; } = "";
    [DataMember(Name = "generatedAt")] public string GeneratedAt { get; set; } = "";
    [DataMember(Name = "packages", IsRequired = true)] public List<string> Packages { get; set; } = new();
    [DataMember(Name = "managedConfigs", IsRequired = true)] public List<string> ManagedConfigs { get; set; } = new();
    [DataMember(Name = "managedConfigHashes", EmitDefaultValue = false)] public Dictionary<string, string> ManagedConfigHashes { get; set; } = new();
}

[DataContract]
internal sealed class PackageManifest
{
    [DataMember(Name = "name", IsRequired = true)] public string Name { get; set; } = "";
    [DataMember(Name = "version_number", IsRequired = true)] public string VersionNumber { get; set; } = "";
}
