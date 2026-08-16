using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using XomNghien.Bootstrap;

namespace XomNghien.RuntimeUpdater;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class RuntimeUpdaterPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "org.servermodbootstrap.runtime-updater";
    public const string PluginName = "Server Mod Bootstrap Runtime Updater";
    public const string PluginVersion = "2.1.0";
    internal static RuntimeUpdaterPlugin? Instance { get; private set; }

    private ConfigEntry<bool> _enabled = null!;
    private ConfigEntry<int> _pollIntervalSeconds = null!;
    private ConfigEntry<bool> _autoRestart = null!;
    private ConfigEntry<int> _restartDelaySeconds = null!;
    private ConfigEntry<bool> _restartForConfigChanges = null!;
    private Task<SynchronizationResult>? _check;
    private Task<SynchronizationResult>? _relayedCheck;
    private readonly object _relayLock = new();
    private string? _pendingManifest;
    private float _nextCheckAt;
    private float? _restartAt;
    private bool _restartRequested;
    private bool _isDedicatedServer;
    private bool _showRestartPrompt;
    private string _promptMessage = "Server mods were updated. Restart Valheim before reconnecting.";
    private Harmony? _harmony;

    private void Awake()
    {
        Instance = this;
        _enabled = Config.Bind("Live updates", "Enabled", true,
            "Poll the server manifest while a dedicated server is running.");
        _pollIntervalSeconds = Config.Bind("Live updates", "PollIntervalSeconds", 60,
            new ConfigDescription("Seconds between manifest checks.", new AcceptableValueRange<int>(30, 3600)));
        _autoRestart = Config.Bind("Restart", "AutoRestartForModChanges", true,
            "Save and quit after installing a changed plugin set. The server supervisor must restart the process.");
        _restartDelaySeconds = Config.Bind("Restart", "DelaySeconds", 60,
            new ConfigDescription("Delay before saving and quitting after a mod change.", new AcceptableValueRange<int>(10, 1800)));
        _restartForConfigChanges = Config.Bind("Restart", "RestartForConfigChanges", false,
            "Also restart after config-only changes. Leave false for mods such as AzuAntiCheat that watch their config files.");

        _isDedicatedServer = IsDedicatedServer();
        _harmony = new Harmony(PluginGuid);
        try { new ManifestHandshake(Logger).Install(_harmony); }
        catch (Exception error) { Logger.LogError("Could not install the manifest handshake: " + error); }

        if (!_isDedicatedServer)
        {
            Logger.LogInfo("Client manifest relay receiver is ready; no client configuration is required");
            return;
        }

        _nextCheckAt = Time.realtimeSinceStartup + Math.Max(30, _pollIntervalSeconds.Value);
        Logger.LogInfo($"Live server mod polling enabled every {Math.Max(30, _pollIntervalSeconds.Value)} seconds");
    }

    private void Update()
    {
        CompleteRelayedCheck();
        StartRelayedCheck();
        if (!_isDedicatedServer || !_enabled.Value || _restartRequested) return;

        if (_restartAt.HasValue)
        {
            if (Time.realtimeSinceStartup >= _restartAt.Value) RestartServer();
            return;
        }

        CompleteCheck();
        if (_check == null && Time.realtimeSinceStartup >= _nextCheckAt)
        {
            _nextCheckAt = Time.realtimeSinceStartup + Math.Max(30, _pollIntervalSeconds.Value);
            _check = Task.Run(BootstrapSynchronizer.StageConfiguredUpdate);
        }
    }

    internal void QueueRelayedManifest(string manifest)
    {
        if (_isDedicatedServer || string.IsNullOrWhiteSpace(manifest)) return;
        lock (_relayLock) _pendingManifest = manifest;
        Logger.LogInfo("Received the server mod manifest; checking local managed mods");
    }

    private void StartRelayedCheck()
    {
        if (_isDedicatedServer || _relayedCheck != null) return;
        string? manifest;
        lock (_relayLock)
        {
            manifest = _pendingManifest;
            _pendingManifest = null;
        }
        if (manifest != null)
            _relayedCheck = Task.Run(() => BootstrapSynchronizer.StageRelayedManifest(manifest));
    }

    private void CompleteRelayedCheck()
    {
        if (_relayedCheck == null || !_relayedCheck.IsCompleted) return;
        var completed = _relayedCheck;
        _relayedCheck = null;
        if (completed.IsFaulted)
        {
            var reason = completed.Exception?.GetBaseException().Message ?? "Unknown manifest error";
            Logger.LogError("The server manifest was rejected: " + reason);
            DisconnectClient();
            ShowPrompt("The server's mod manifest was invalid. Connection stopped.\n\n" + reason);
            return;
        }

        var result = completed.Result;
        if (!result.Changed)
        {
            Logger.LogInfo("The installed managed mods already match the server");
            return;
        }

        Logger.LogWarning($"Staged server revision {ShortRevision(result.Revision)}; Valheim must restart before reconnecting");
        WriteRestartMarker(result);
        DisconnectClient();
        ShowPrompt("This server requires a different managed mod set.\n\nThe mods are downloaded and staged. Restart Valheim, then connect again.");
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

    private void DisconnectClient()
    {
        try
        {
            var gameType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("Game", false))
                .FirstOrDefault(type => type != null);
            if (gameType == null) return;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            var instance = gameType.GetField("instance", flags)?.GetValue(null)
                ?? gameType.GetProperty("instance", flags)?.GetValue(null, null);
            var logout = gameType.GetMethod("Logout", flags, null, new[] { typeof(bool), typeof(bool) }, null);
            logout?.Invoke(instance, new object[] { true, true });
        }
        catch (Exception error)
        {
            Logger.LogWarning("Could not disconnect after a managed mod update: " + error.Message);
        }
    }

    private void ShowPrompt(string message)
    {
        _promptMessage = message;
        _showRestartPrompt = true;
    }

    private void OnGUI()
    {
        if (!_showRestartPrompt || _isDedicatedServer) return;
        const float width = 520f;
        const float height = 220f;
        var area = new Rect((Screen.width - width) / 2f, (Screen.height - height) / 2f, width, height);
        GUI.Box(area, "Server Mod Update");
        GUI.Label(new Rect(area.x + 24f, area.y + 48f, width - 48f, 100f), _promptMessage);
        if (GUI.Button(new Rect(area.x + 150f, area.y + 164f, 220f, 36f), "Quit Valheim now"))
            Application.Quit();
    }

    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
        if (ReferenceEquals(Instance, this)) Instance = null;
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
