using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Mono.Cecil;

namespace XomNghien.Bootstrap;

/// <summary>BepInEx 5 preloader entrypoint. It intentionally patches no game assemblies.</summary>
public static class BootstrapPatcher
{
    /// <summary>No game assemblies are modified by this synchronization-only patcher.</summary>
    public static IEnumerable<string> TargetDLLs => Array.Empty<string>();

    /// <summary>Synchronizes managed packages and configs before the BepInEx chainloader starts.</summary>
    public static void Initialize()
    {
        try
        {
            BootstrapSynchronizer.Run();
            ClearRestartMarker();
        }
        catch (Exception error)
        {
            BootstrapLog.Error("Synchronization failed; keeping the last-known-good installation", error);
        }
    }

    /// <summary>Required BepInEx 5 patcher contract method; intentionally does nothing.</summary>
    /// <param name="assembly">An unused target assembly.</param>
    public static void Patch(AssemblyDefinition assembly) { }

    private static void ClearRestartMarker()
    {
        var patcherDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        var bepinexRoot = patcherDirectory == null ? null : Directory.GetParent(patcherDirectory)?.FullName;
        if (bepinexRoot == null) return;
        var marker = Path.Combine(bepinexRoot, "xom-bootstrap", "restart-required");
        if (File.Exists(marker)) File.Delete(marker);
    }
}
