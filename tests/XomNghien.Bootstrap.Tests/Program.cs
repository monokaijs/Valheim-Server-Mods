using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using XomNghien.Bootstrap;

var tests = new (string Name, Action Run)[]
{
    ("config traversal rejection", RejectConfigTraversal),
    ("safe archive extraction", ExtractSafeArchive),
    ("archive traversal rejection", RejectArchiveTraversal),
    ("early-loader package rejection", RejectEarlyLoaderFiles),
    ("package change detection ignores order and case", DetectPackageChanges),
    ("Valheim string RPC reflection bridge", VerifyRpcReflectionBridge),
    ("generic manifest URL settings", VerifyGenericManifestSettings),
    ("server-only configs are removed from relayed manifests", FilterServerConfigsFromRelay),
};
var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + test.Name + ": " + error); }
}
return failed;

static void RejectConfigTraversal()
{
    var temp = TempDirectory();
    AssertThrows<InvalidDataException>(() => BootstrapSynchronizer.SafeConfigTarget(temp, "../outside.cfg"));
    AssertThrows<InvalidDataException>(() => BootstrapSynchronizer.SafeConfigTarget(temp, "bad:name.cfg"));
    Directory.Delete(temp, true);
}

static void ExtractSafeArchive()
{
    var temp = TempDirectory();
    var archivePath = Path.Combine(temp, "package.zip");
    WriteArchive(archivePath, new Dictionary<string, string>
    {
        ["manifest.json"] = "{\"name\":\"GoodMod\",\"version_number\":\"1.2.3\"}",
        ["GoodMod.dll"] = "plugin",
        ["config/GoodMod.cfg"] = "Enabled = true",
    });
    var plugins = Path.Combine(temp, "plugins");
    var defaults = Path.Combine(temp, "defaults");
    PackageInstaller.Extract(archivePath, Package(), plugins, defaults, new Dictionary<string, string>());
    Assert(File.Exists(Path.Combine(plugins, "Author-GoodMod", "GoodMod.dll")), "plugin was not routed to its isolated directory");
    Assert(File.Exists(Path.Combine(defaults, "GoodMod.cfg")), "config default was not routed");
    Directory.Delete(temp, true);
}

static void RejectArchiveTraversal()
{
    var temp = TempDirectory();
    var archivePath = Path.Combine(temp, "bad.zip");
    WriteArchive(archivePath, new Dictionary<string, string>
    {
        ["manifest.json"] = "{\"name\":\"GoodMod\",\"version_number\":\"1.2.3\"}",
        ["../outside.dll"] = "bad",
    });
    AssertThrows<InvalidDataException>(() => PackageInstaller.ValidateArchive(archivePath, Package()));
    Directory.Delete(temp, true);
}

static void RejectEarlyLoaderFiles()
{
    var temp = TempDirectory();
    var archivePath = Path.Combine(temp, "patcher.zip");
    WriteArchive(archivePath, new Dictionary<string, string>
    {
        ["manifest.json"] = "{\"name\":\"GoodMod\",\"version_number\":\"1.2.3\"}",
        ["BepInEx/patchers/Early.dll"] = "patcher",
    });
    AssertThrows<InvalidDataException>(() => PackageInstaller.Extract(
        archivePath, Package(), Path.Combine(temp, "plugins"), Path.Combine(temp, "defaults"), new Dictionary<string, string>()));
    Directory.Delete(temp, true);
}

static void DetectPackageChanges()
{
    Assert(!BootstrapSynchronizer.PackageSetsDiffer(
        new[] { "Author-One-1.0.0", "Author-Two-2.0.0" },
        new[] { "author-two-2.0.0", "author-one-1.0.0" }), "same package set was reported as changed");
    Assert(BootstrapSynchronizer.PackageSetsDiffer(
        new[] { "Author-One-1.0.0" },
        new[] { "Author-One-1.1.0" }), "version change was not detected");
}

static void VerifyRpcReflectionBridge()
{
    var rpc = new MockRpc();
    RpcReflectionBridge.RegisterString(rpc, "ServerModBootstrap_Manifest_v1", (_, _) => { });
    Assert(rpc.Name == "ServerModBootstrap_Manifest_v1", "manifest RPC was registered under the wrong name");
    rpc.Callback?.Invoke(rpc, "received");
    RpcReflectionBridge.InvokeString(rpc, "ServerModBootstrap_Manifest_v1", "relayed");
    Assert(rpc.InvokedName == "ServerModBootstrap_Manifest_v1", "manifest RPC was invoked under the wrong name");
    Assert(rpc.InvokedArguments?.Length == 1 && Equals(rpc.InvokedArguments[0], "relayed"), "manifest RPC payload changed");
}

static void VerifyGenericManifestSettings()
{
    var temp = TempDirectory();
    var clientPath = Path.Combine(temp, "client.cfg");
    File.WriteAllText(clientPath, "ManifestUrl =\n");
    var client = BootstrapSettings.Load(clientPath);
    Assert(!client.HasManifestUrl, "client should not require a manifest URL");

    var serverPath = Path.Combine(temp, "server.cfg");
    File.WriteAllText(serverPath, "ManifestUrl = https://mods.example.net/manifests/server-a.json\n");
    var server = BootstrapSettings.Load(serverPath);
    Assert(server.HasManifestUrl && server.ManifestUrl == "https://mods.example.net/manifests/server-a.json", "generic manifest URL was not loaded");

    File.WriteAllText(serverPath, "ManifestUrl = http://mods.example.net/unsafe.json\n");
    AssertThrows<InvalidDataException>(() => BootstrapSettings.Load(serverPath));
    Directory.Delete(temp, true);
}

static void FilterServerConfigsFromRelay()
{
    var serverRevision = new string('a', 64);
    var clientRevision = new string('b', 64);
    var manifest = "{" +
        "\"schemaVersion\":2," +
        "\"manifestId\":\"server-a\"," +
        "\"revision\":\"" + serverRevision + "\"," +
        "\"clientRevision\":\"" + clientRevision + "\"," +
        "\"generatedAt\":\"2026-08-16T00:00:00.000Z\"," +
        "\"packages\":[]," +
        "\"configs\":[" +
        ConfigJson("server.cfg", "server") + "," +
        ConfigJson("client.cfg", "client") + "," +
        ConfigJson("shared.cfg", "both") + "]}";

    var relayed = Json.Read<BootstrapManifest>(System.Text.Encoding.UTF8.GetBytes(
        BootstrapSynchronizer.CreateRelayManifest(manifest)));
    Assert(relayed.Revision == clientRevision, "relay did not use the client revision");
    Assert(relayed.Configs.Count == 2, "relay did not filter the expected number of configs");
    Assert(relayed.Configs.All(config => config.Path != "server.cfg"), "server-only config was relayed");
}

static string ConfigJson(string path, string target) =>
    "{\"path\":\"" + path + "\",\"sha256\":\"" + new string('c', 64) +
    "\",\"contentBase64\":\"dmFsdWU=\",\"target\":\"" + target + "\"}";

static ManifestPackage Package() => new()
{
    Coordinate = "Author-GoodMod-1.2.3", Namespace = "Author", PackageName = "GoodMod", VersionNumber = "1.2.3",
    DownloadUrl = "https://gcdn.thunderstore.io/package.zip", Dependencies = new List<string>(),
};

static void WriteArchive(string path, IReadOnlyDictionary<string, string> entries)
{
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var pair in entries)
    {
        var entry = archive.CreateEntry(pair.Key);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(pair.Value);
    }
}

static string TempDirectory()
{
    var path = Path.Combine(Path.GetTempPath(), "xn-bootstrap-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void AssertThrows<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}

sealed class MockRpc
{
    public string? Name { get; private set; }
    public Action<MockRpc, string>? Callback { get; private set; }
    public string? InvokedName { get; private set; }
    public object[]? InvokedArguments { get; private set; }

    public void Register<T>(string name, Action<MockRpc, T> callback)
    {
        Name = name;
        Callback = (rpc, value) => callback(rpc, (T)(object)value);
    }

    public void Invoke(string name, params object[] arguments)
    {
        InvokedName = name;
        InvokedArguments = arguments;
    }
}
