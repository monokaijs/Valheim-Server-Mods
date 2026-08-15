using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace XomNghien.Bootstrap;

internal sealed class BootstrapSettings
{
    public string ManifestUrl { get; private set; } = "";
    public int RequestTimeoutSeconds { get; private set; } = 45;
    public bool HasManifestUrl => ManifestUrl.Length > 0;

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

        if (values.TryGetValue("ManifestUrl", out var manifestUrl)) settings.ManifestUrl = manifestUrl;
        if (values.TryGetValue("RequestTimeoutSeconds", out var timeout)
            && int.TryParse(timeout, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            settings.RequestTimeoutSeconds = Math.Max(10, Math.Min(120, seconds));
        settings.Validate();
        return settings;
    }

    private void Validate()
    {
        if (HasManifestUrl
            && (!Uri.TryCreate(ManifestUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidDataException("ManifestUrl must be an absolute HTTPS URL");
    }
}
