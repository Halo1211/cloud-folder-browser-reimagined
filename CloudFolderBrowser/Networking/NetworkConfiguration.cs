using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace CloudFolderBrowser.Networking;

public enum DnsProviderMode
{
    System = 0,
    Cloudflare = 1,
    Google = 2,
    Quad9 = 3,
    AdGuard = 4,
    Custom = 5
}

public enum ProxyMode
{
    Direct = 0,
    System = 1,
    Custom = 2
}

public enum DnsTransport
{
    Udp = 0,
    Https = 1,
    Tls = 2
}

public sealed record NetworkConfiguration
{
    public DnsProviderMode DnsProvider { get; init; } = DnsProviderMode.System;
    public DnsTransport DnsTransport { get; init; } = DnsTransport.Udp;
    public string CustomDnsServer { get; init; } = string.Empty;
    public bool FallbackToSystemDns { get; init; } = true;
    public bool PreferIpv6 { get; init; }
    public ProxyMode ProxyMode { get; init; } = ProxyMode.System;
    public string ProxyUrl { get; init; } = string.Empty;
    public string ProxyUsername { get; init; } = string.Empty;
    public string ProxyPassword { get; init; } = string.Empty;
    public bool BypassProxyForLocal { get; init; } = true;
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public string DnsServer => DnsProvider switch
    {
        DnsProviderMode.Cloudflare => "1.1.1.1",
        DnsProviderMode.Google => "8.8.8.8",
        DnsProviderMode.Quad9 => "9.9.9.9",
        DnsProviderMode.AdGuard => "94.140.14.14",
        DnsProviderMode.Custom => CustomDnsServer.Trim(),
        _ => string.Empty
    };

    public static NetworkConfiguration FromSettings(string? routeKey = null)
    {
        var settings = Properties.Settings.Default;
        var configuration = new NetworkConfiguration
        {
            DnsProvider = Enum.IsDefined(typeof(DnsProviderMode), settings.dnsProvider)
                ? (DnsProviderMode)settings.dnsProvider
                : DnsProviderMode.System,
            DnsTransport = Enum.IsDefined(typeof(DnsTransport), settings.dnsTransport)
                ? (DnsTransport)settings.dnsTransport
                : DnsTransport.Udp,
            CustomDnsServer = settings.customDnsServer ?? string.Empty,
            FallbackToSystemDns = settings.dnsFallbackToSystem,
            PreferIpv6 = settings.preferIpv6,
            ProxyMode = Enum.IsDefined(typeof(ProxyMode), settings.proxyMode)
                ? (ProxyMode)settings.proxyMode
                : ProxyMode.System,
            ProxyUrl = settings.proxyUrl ?? string.Empty,
            ProxyUsername = settings.proxyUsername ?? string.Empty,
            ProxyPassword = ProxySecretProtector.Unprotect(settings.protectedProxyPassword),
            BypassProxyForLocal = settings.proxyBypassLocal,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Clamp(settings.connectTimeoutSeconds, 5, 120))
        };
        return ProviderProxyRuleStore.Apply(configuration, routeKey);
    }

    public void Validate()
    {
        if (DnsProvider == DnsProviderMode.Custom)
        {
            switch (DnsTransport)
            {
                case DnsTransport.Udp:
                    _ = DnsServerEndpoint.Parse(CustomDnsServer);
                    break;
                case DnsTransport.Https:
                    _ = SecureDnsEndpoint.GetDohUri(this);
                    break;
                case DnsTransport.Tls:
                    _ = SecureDnsEndpoint.GetDotEndpoint(this);
                    break;
            }
        }

        if (ProxyMode == ProxyMode.Custom)
            _ = GetValidatedProxyUri();

        if (ConnectTimeout < TimeSpan.FromSeconds(1) || ConnectTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(ConnectTimeout));
    }

    public IWebProxy? CreateWebProxy()
    {
        if (ProxyMode != ProxyMode.Custom)
            return null;

        var proxy = new WebProxy(GetValidatedProxyUri(), BypassProxyForLocal);
        if (!string.IsNullOrWhiteSpace(ProxyUsername))
            proxy.Credentials = new NetworkCredential(ProxyUsername.Trim(), ProxyPassword);
        return proxy;
    }

    public Uri GetValidatedProxyUri()
    {
        string value = ProxyUrl.Trim();
        if (!value.Contains("://", StringComparison.Ordinal))
            value = "http://" + value;

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || string.IsNullOrWhiteSpace(uri.Host)
            || uri.Port is < 1 or > 65535
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.Scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5"))
        {
            throw new FormatException(
                "Proxy must be an HTTP(S) or SOCKS server URL without credentials or a path, for example http://127.0.0.1:8080 or socks5://127.0.0.1:1080.");
        }

        return uri;
    }
}

public static class ProxySecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CloudFolderBrowser.ProxyPassword.v1");

    public static string Protect(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        byte[] clear = Encoding.UTF8.GetBytes(value);
        try
        {
            return Convert.ToBase64String(
                ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    public static string Unprotect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        try
        {
            byte[] encrypted = Convert.FromBase64String(value);
            byte[] clear = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                return Encoding.UTF8.GetString(clear);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return string.Empty;
        }
    }
}
