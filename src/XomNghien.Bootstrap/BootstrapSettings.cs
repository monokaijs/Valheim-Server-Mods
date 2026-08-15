using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace XomNghien.Bootstrap;

internal sealed class BootstrapSettings
{
    public string ApiBaseUrl { get; private set; } = "https://xomnghien.com";
    public string ServerId { get; private set; } = "1";
    public string TrustedKeyId { get; private set; } = "xn-bootstrap-1";
    public int RequestTimeoutSeconds { get; private set; } = 45;

    public static BootstrapSettings Load(string path)
    {
        var settings = new BootstrapSettings();
        if (!File.Exists(path)) return settings;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal)) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            values[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
        }

        if (values.TryGetValue("ApiBaseUrl", out var apiBaseUrl)) settings.ApiBaseUrl = apiBaseUrl.TrimEnd('/');
        if (values.TryGetValue("ServerId", out var serverId)) settings.ServerId = serverId;
        if (values.TryGetValue("TrustedKeyId", out var keyId)) settings.TrustedKeyId = keyId;
        if (values.TryGetValue("RequestTimeoutSeconds", out var timeout)
            && int.TryParse(timeout, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            settings.RequestTimeoutSeconds = Math.Max(10, Math.Min(120, seconds));
        settings.Validate();
        return settings;
    }

    private void Validate()
    {
        if (!Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("ApiBaseUrl must be an absolute HTTPS URL");
        if (ServerId.Length == 0 || ServerId.Length > 20 || !long.TryParse(ServerId, out var id) || id < 1)
            throw new InvalidDataException("ServerId must be a positive integer");
        if (TrustedKeyId.Length == 0 || TrustedKeyId.Length > 100)
            throw new InvalidDataException("TrustedKeyId is invalid");
    }
}
