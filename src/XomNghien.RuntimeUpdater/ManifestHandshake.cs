using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using XomNghien.Bootstrap;

namespace XomNghien.RuntimeUpdater;

internal sealed class ManifestHandshake
{
    private const string ManifestRpc = "ServerModBootstrap_Manifest_v1";
    private const BindingFlags AllMembers = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static ManifestHandshake? _instance;
    private readonly ManualLogSource _log;

    public ManifestHandshake(ManualLogSource log)
    {
        _log = log;
    }

    public void Install(Harmony harmony)
    {
        _instance = this;
        var znetType = AccessTools.TypeByName("ZNet") ?? throw new TypeLoadException("Valheim ZNet type was not found");
        var onNewConnection = AccessTools.Method(znetType, "OnNewConnection")
            ?? throw new MissingMethodException(znetType.FullName, "OnNewConnection");
        var callback = AccessTools.Method(typeof(ManifestHandshake), nameof(OnNewConnectionPostfix))
            ?? throw new MissingMethodException(nameof(OnNewConnectionPostfix));
        harmony.Patch(onNewConnection, postfix: new HarmonyMethod(callback) { priority = Priority.First });
        _log.LogInfo("Installed the server manifest relay handshake");
    }

    private static void OnNewConnectionPostfix(object __instance, object __0)
    {
        _instance?.OnNewConnection(__instance, __0);
    }

    private void OnNewConnection(object znet, object peer)
    {
        try
        {
            var rpc = peer.GetType().GetField("m_rpc", AllMembers)?.GetValue(peer);
            if (rpc == null) return;
            RpcReflectionBridge.RegisterString(rpc, ManifestRpc, ReceiveManifest);
            if (!IsServer(znet)) return;

            var manifest = BootstrapSynchronizer.ReadRelayManifest();
            if (string.IsNullOrWhiteSpace(manifest))
            {
                _log.LogWarning("A client connected, but no validated manifest is available to relay");
                return;
            }

            RpcReflectionBridge.InvokeString(rpc, ManifestRpc, manifest!);
            _log.LogInfo($"Relayed the manifest to a connecting client ({manifest!.Length} characters)");
        }
        catch (Exception error)
        {
            _log.LogError("Manifest handshake failed: " + error);
        }
    }

    private static void ReceiveManifest(object _, string manifest)
    {
        RuntimeUpdaterPlugin.Instance?.QueueRelayedManifest(manifest);
    }

    private static bool IsServer(object znet)
    {
        var method = znet.GetType().GetMethod("IsServer", AllMembers, null, Type.EmptyTypes, null);
        if (method?.Invoke(znet, null) is bool result) return result;
        var field = znet.GetType().GetField("m_isServer", AllMembers);
        return field?.GetValue(field.IsStatic ? null : znet) is bool fallback && fallback;
    }
}
