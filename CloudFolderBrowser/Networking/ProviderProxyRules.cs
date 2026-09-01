using Newtonsoft.Json;

namespace CloudFolderBrowser.Networking;

public sealed class ProviderProxyRule
{
    public string RouteKey { get; set; } = string.Empty;
    public ProxyMode Mode { get; set; } = ProxyMode.System;
    public string ProxyUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string ProtectedPassword { get; set; } = string.Empty;
    public bool BypassLocal { get; set; } = true;

    [JsonIgnore]
    public string Password
    {
        get => ProxySecretProtector.Unprotect(ProtectedPassword);
        set => ProtectedPassword = ProxySecretProtector.Protect(value);
    }
}

public static class ProviderRouteCatalog
{
    public static IReadOnlyList<KeyValuePair<string, string>> Options { get; } = new[]
    {
        Pair("Mega", "MEGA"),
        Pair("Yadisk", "Yandex Disk"),
        Pair("h5ai", "h5ai index"),
        Pair("Allsync", "AllSync / WebDAV share"),
        Pair("QCloud", "Qloud"),
        Pair("TheTrove", "The Trove"),
        Pair("Dropbox", "Dropbox"),
        Pair("GoogleDrive", "Google Drive"),
        Pair("TeraBox", "TeraBox"),
        Pair("Other", "Generic HTTP"),
        Pair("WebDav", "WebDAV account"),
        Pair("AllDebrid", "AllDebrid"),
        Pair("RealDebrid", "Real-Debrid"),
        Pair("DebridLink", "Debrid-Link"),
        Pair("Premiumize", "Premiumize.me"),
        Pair("TorBox", "TorBox"),
        Pair("FogLink", "FogLink"),
        Pair("FlareSolverr", "FlareSolverr")
    };

    public static string GetDisplayName(string routeKey) => Options
        .FirstOrDefault(option => option.Key.Equals(routeKey, StringComparison.OrdinalIgnoreCase)).Value
        ?? routeKey;

    private static KeyValuePair<string, string> Pair(string key, string value) => new(key, value);
}

public static class ProviderProxyRuleStore
{
    public static IReadOnlyList<ProviderProxyRule> Load()
    {
        try
        {
            return JsonConvert.DeserializeObject<List<ProviderProxyRule>>(
                    Properties.Settings.Default.providerProxyRulesJson ?? "[]")
                ?.Where(rule => !string.IsNullOrWhiteSpace(rule.RouteKey)
                    && Enum.IsDefined(typeof(ProxyMode), rule.Mode))
                .GroupBy(rule => rule.RouteKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToArray()
                ?? Array.Empty<ProviderProxyRule>();
        }
        catch (JsonException)
        {
            return Array.Empty<ProviderProxyRule>();
        }
    }

    public static void Save(IEnumerable<ProviderProxyRule> rules)
    {
        ProviderProxyRule[] values = rules
            .Where(rule => !string.IsNullOrWhiteSpace(rule.RouteKey))
            .GroupBy(rule => rule.RouteKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(rule => rule.RouteKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (ProviderProxyRule rule in values)
            Validate(rule);
        Properties.Settings.Default.providerProxyRulesJson = JsonConvert.SerializeObject(values);
        Properties.Settings.Default.Save();
    }

    public static NetworkConfiguration Apply(NetworkConfiguration global, string? routeKey)
    {
        if (string.IsNullOrWhiteSpace(routeKey))
            return global;
        ProviderProxyRule? rule = Load().FirstOrDefault(value =>
            value.RouteKey.Equals(routeKey, StringComparison.OrdinalIgnoreCase));
        if (rule == null)
            return global;
        return global with
        {
            ProxyMode = rule.Mode,
            ProxyUrl = rule.ProxyUrl,
            ProxyUsername = rule.Username,
            ProxyPassword = rule.Password,
            BypassProxyForLocal = rule.BypassLocal
        };
    }

    public static void Validate(ProviderProxyRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (!ProviderRouteCatalog.Options.Any(option => option.Key.Equals(rule.RouteKey, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Select a supported provider.");
        if (!Enum.IsDefined(typeof(ProxyMode), rule.Mode))
            throw new ArgumentException("Select a supported proxy mode.");
        if (rule.Mode == ProxyMode.Custom)
        {
            _ = new NetworkConfiguration
            {
                ProxyMode = ProxyMode.Custom,
                ProxyUrl = rule.ProxyUrl ?? string.Empty,
                ProxyUsername = rule.Username ?? string.Empty,
                ProxyPassword = rule.Password,
                BypassProxyForLocal = rule.BypassLocal
            }.GetValidatedProxyUri();
        }
    }
}
