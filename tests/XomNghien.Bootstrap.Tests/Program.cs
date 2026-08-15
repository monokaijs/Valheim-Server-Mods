using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using XomNghien.Bootstrap;

var tests = new (string Name, Action Run)[]
{
    ("signed envelope verification", VerifySignedEnvelope),
    ("config traversal rejection", RejectConfigTraversal),
    ("safe archive extraction", ExtractSafeArchive),
    ("archive traversal rejection", RejectArchiveTraversal),
    ("early-loader package rejection", RejectEarlyLoaderFiles),
    ("package change detection ignores order and case", DetectPackageChanges),
};
var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + test.Name + ": " + error); }
}
return failed;

static void VerifySignedEnvelope()
{
    using var rsa = RSA.Create(2048);
    var payload = Encoding.UTF8.GetBytes("{\"schemaVersion\":1}");
    var signature = rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var parameters = rsa.ExportParameters(false);
    var temp = TempDirectory();
    var keyPath = Path.Combine(temp, "key.xml");
    File.WriteAllText(keyPath, $"<RSAKeyValue><Modulus>{Convert.ToBase64String(parameters.Modulus!)}</Modulus><Exponent>{Convert.ToBase64String(parameters.Exponent!)}</Exponent></RSAKeyValue>");
    var verified = SignatureVerifier.Verify(new SignedEnvelope
    {
        Algorithm = "RS256", KeyId = "test", Payload = Convert.ToBase64String(payload), Signature = Convert.ToBase64String(signature),
    }, "test", keyPath);
    Assert(payload.SequenceEqual(verified), "verified payload changed");
    Directory.Delete(temp, true);
}

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
