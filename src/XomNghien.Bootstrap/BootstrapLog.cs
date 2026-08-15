using System;
using System.IO;

namespace XomNghien.Bootstrap;

internal static class BootstrapLog
{
    private static string? _path;

    public static void Initialize(string stateRoot)
    {
        Directory.CreateDirectory(stateRoot);
        _path = Path.Combine(stateRoot, "bootstrap.log");
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message, Exception error) => Write("ERROR", message + Environment.NewLine + error);

    private static void Write(string level, string message)
    {
        var line = $"[{DateTimeOffset.UtcNow:O}] [{level}] {message}";
        Console.WriteLine("[XomNghienBootstrap] " + line);
        try
        {
            if (_path != null) File.AppendAllText(_path, line + Environment.NewLine);
        }
        catch { }
    }
}
