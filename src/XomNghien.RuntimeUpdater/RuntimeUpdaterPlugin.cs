using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using XomNghien.Bootstrap;

namespace XomNghien.RuntimeUpdater;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class RuntimeUpdaterPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.xomnghien.servermods.runtime-updater";
    public const string PluginName = "Xom Nghien Runtime Updater";
    public const string PluginVersion = "1.1.0";

    private ConfigEntry<bool> _enabled = null!;
    private ConfigEntry<int> _pollIntervalSeconds = null!;
    private ConfigEntry<bool> _autoRestart = null!;
    private ConfigEntry<int> _restartDelaySeconds = null!;
    private ConfigEntry<bool> _restartForConfigChanges = null!;
    private Task<SynchronizationResult>? _check;
    private float _nextCheckAt;
    private float? _restartAt;
    private bool _restartRequested;

    private void Awake()
    {
        _enabled = Config.Bind("Live updates", "Enabled", true,
            "Poll the signed server manifest while a dedicated server is running.");
        _pollIntervalSeconds = Config.Bind("Live updates", "PollIntervalSeconds", 60,
            new ConfigDescription("Seconds between manifest checks.", new AcceptableValueRange<int>(30, 3600)));
        _autoRestart = Config.Bind("Restart", "AutoRestartForModChanges", true,
            "Save and quit after installing a changed plugin set. The server supervisor must restart the process.");
        _restartDelaySeconds = Config.Bind("Restart", "DelaySeconds", 60,
            new ConfigDescription("Delay before saving and quitting after a mod change.", new AcceptableValueRange<int>(10, 1800)));
        _restartForConfigChanges = Config.Bind("Restart", "RestartForConfigChanges", false,
            "Also restart after config-only changes. Leave false for mods such as AzuAntiCheat that watch their config files.");

        if (!IsDedicatedServer())
        {
            Logger.LogDebug("Runtime polling is disabled on game clients; clients synchronize during startup.");
            enabled = false;
            return;
        }

        _nextCheckAt = Time.realtimeSinceStartup + Math.Max(30, _pollIntervalSeconds.Value);
        Logger.LogInfo($"Live server mod polling enabled every {Math.Max(30, _pollIntervalSeconds.Value)} seconds");
    }

    private void Update()
    {
        if (!_enabled.Value || _restartRequested) return;

        if (_restartAt.HasValue)
        {
            if (Time.realtimeSinceStartup >= _restartAt.Value) RestartServer();
            return;
        }

        CompleteCheck();
        if (_check == null && Time.realtimeSinceStartup >= _nextCheckAt)
        {
            _nextCheckAt = Time.realtimeSinceStartup + Math.Max(30, _pollIntervalSeconds.Value);
            _check = Task.Run(BootstrapSynchronizer.Run);
        }
    }

    private void CompleteCheck()
    {
        if (_check == null || !_check.IsCompleted) return;
        var completed = _check;
        _check = null;
        if (completed.IsFaulted)
        {
            Logger.LogError("Live mod synchronization failed: " + completed.Exception?.GetBaseException());
            return;
        }

        var result = completed.Result;
        if (!result.Changed) return;
        Logger.LogInfo($"Installed managed revision {ShortRevision(result.Revision)}");

        var requiresRestart = result.PackagesChanged || (_restartForConfigChanges.Value && result.ConfigsChanged);
        if (!requiresRestart) return;

        WriteRestartMarker(result);
        if (!_autoRestart.Value)
        {
            Logger.LogWarning("Managed mods changed. A server restart is required; automatic restart is disabled.");
            return;
        }

        _restartAt = Time.realtimeSinceStartup + Math.Max(10, _restartDelaySeconds.Value);
        Logger.LogWarning($"Managed mods changed. Saving and quitting for supervisor restart in {Math.Max(10, _restartDelaySeconds.Value)} seconds.");
    }

    private void RestartServer()
    {
        _restartRequested = true;
        TrySaveWorld();
        Logger.LogWarning("Quitting dedicated server so its supervisor can load the new managed mods.");
        Application.Quit();
    }

    private void TrySaveWorld()
    {
        try
        {
            var znet = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("ZNet", false))
                .FirstOrDefault(type => type != null);
            if (znet == null)
            {
                Logger.LogWarning("Could not find ZNet; quitting without an explicit pre-restart save.");
                return;
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            var instance = znet.GetField("instance", flags)?.GetValue(null)
                ?? znet.GetProperty("instance", flags)?.GetValue(null, null);
            var save = znet.GetMethod("Save", flags, null, new[] { typeof(bool) }, null);
            if (instance == null || save == null)
            {
                Logger.LogWarning("Could not invoke ZNet.Save; Valheim's normal quit handling will be used.");
                return;
            }

            save.Invoke(instance, new object[] { false });
            Logger.LogInfo("Requested a world save before restart.");
        }
        catch (Exception error)
        {
            Logger.LogError("Pre-restart world save failed: " + error);
        }
    }

    private static void WriteRestartMarker(SynchronizationResult result)
    {
        var stateRoot = Path.Combine(Paths.BepInExRootPath, "xom-bootstrap");
        Directory.CreateDirectory(stateRoot);
        File.WriteAllText(Path.Combine(stateRoot, "restart-required"),
            $"revision={result.Revision}{Environment.NewLine}createdAt={DateTimeOffset.UtcNow:O}{Environment.NewLine}");
    }

    private static string ShortRevision(string revision) => revision.Substring(0, Math.Min(12, revision.Length));

    private static bool IsDedicatedServer() => Environment.GetCommandLineArgs()
        .Any(argument => argument.Equals("-batchmode", StringComparison.OrdinalIgnoreCase));
}
