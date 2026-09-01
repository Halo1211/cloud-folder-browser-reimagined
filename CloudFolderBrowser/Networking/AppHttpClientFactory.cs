using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace CloudFolderBrowser.Networking;

public sealed record NetworkDiagnosticResult(
    IReadOnlyList<IPAddress> Addresses,
    TimeSpan DnsElapsed,
    TimeSpan HttpElapsed,
    HttpStatusCode StatusCode,
    Version HttpVersion,
    string RouteDescription,
    string DnsDescription,
    bool UsedSystemDnsFallback,
    SslProtocols TlsProtocol,
    TlsCipherSuite? CipherSuite,
    string CertificateSubject,
    string CertificateIssuer,
    DateTime CertificateExpiryUtc,
    string TlsProbeError = "")
{
    public string ToReport()
    {
        var report = new StringBuilder()
            .AppendLine("Cloud Folder Browser network diagnostics")
            .AppendLine($"Generated (UTC): {DateTime.UtcNow:O}")
            .AppendLine()
            .AppendLine($"DNS: {DnsDescription}")
            .AppendLine($"DNS latency: {DnsElapsed.TotalMilliseconds:0} ms")
            .AppendLine($"DNS fallback used: {(UsedSystemDnsFallback ? "Yes" : "No")}")
            .AppendLine($"Resolved addresses: {string.Join(", ", Addresses)}")
            .AppendLine()
            .AppendLine($"Route: {RouteDescription}")
            .AppendLine($"HTTPS status: {(int)StatusCode} {StatusCode}")
            .AppendLine($"HTTP version: {HttpVersion}")
            .AppendLine($"HTTPS latency: {HttpElapsed.TotalMilliseconds:0} ms")
            .AppendLine()
            .AppendLine("Direct TLS probe (example.com:443):")
            .AppendLine($"TLS protocol: {TlsProtocol}")
            .AppendLine($"Cipher suite: {CipherSuite?.ToString() ?? "Unavailable"}")
            .AppendLine($"Certificate subject: {CertificateSubject}")
            .AppendLine($"Certificate issuer: {CertificateIssuer}")
            .AppendLine($"Certificate expires (UTC): {(CertificateExpiryUtc == DateTime.MinValue ? "Unavailable" : CertificateExpiryUtc.ToString("O"))}");
        if (!string.IsNullOrWhiteSpace(TlsProbeError))
            report.AppendLine($"TLS probe note: {TlsProbeError}");
        return report.ToString();
    }
}

public static class AppHttpClientFactory
{
    public static SocketsHttpHandler CreateHandler(
        DecompressionMethods automaticDecompression = DecompressionMethods.All,
        bool allowAutoRedirect = true,
        NetworkCredential? serverCredentials = null,
        int? maxConnectionsPerServer = null,
        string? routeKey = null)
    {
        NetworkConfiguration configuration = NetworkConfiguration.FromSettings(routeKey);
        configuration.Validate();
        return CreateHandler(configuration, automaticDecompression, allowAutoRedirect, serverCredentials, maxConnectionsPerServer);
    }

    public static SocketsHttpHandler CreateHandler(
        NetworkConfiguration configuration,
        DecompressionMethods automaticDecompression = DecompressionMethods.All,
        bool allowAutoRedirect = true,
        NetworkCredential? serverCredentials = null,
        int? maxConnectionsPerServer = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = allowAutoRedirect,
            AutomaticDecompression = automaticDecompression,
            ConnectTimeout = configuration.ConnectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            Credentials = serverCredentials,
            PreAuthenticate = serverCredentials != null
        };

        if (maxConnectionsPerServer.HasValue)
            handler.MaxConnectionsPerServer = Math.Max(1, maxConnectionsPerServer.Value);

        switch (configuration.ProxyMode)
        {
            case ProxyMode.Direct:
                handler.UseProxy = false;
                break;
            case ProxyMode.Custom:
                handler.UseProxy = true;
                handler.Proxy = configuration.CreateWebProxy();
                break;
            default:
                handler.UseProxy = true;
                break;
        }

        if (configuration.DnsProvider != DnsProviderMode.System)
        {
            IAppDnsResolver resolver = CreateDnsResolver(configuration);
            handler.ConnectCallback = (context, cancellationToken) => ConnectAsync(
                context.DnsEndPoint,
                resolver,
                configuration,
                cancellationToken);
        }

        return handler;
    }

    public static HttpClient CreateClient(
        TimeSpan? timeout = null,
        DecompressionMethods automaticDecompression = DecompressionMethods.All,
        bool allowAutoRedirect = true,
        NetworkCredential? serverCredentials = null,
        int? maxConnectionsPerServer = null,
        string? routeKey = null)
    {
        var client = new HttpClient(CreateHandler(
            automaticDecompression,
            allowAutoRedirect,
            serverCredentials,
            maxConnectionsPerServer,
            routeKey));
        client.Timeout = timeout ?? TimeSpan.FromSeconds(100);
        return client;
    }

    public static async Task<NetworkDiagnosticResult> TestAsync(
        NetworkConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        configuration.Validate();
        IPAddress[] addresses;
        bool fallbackUsed = false;
        var dnsStopwatch = Stopwatch.StartNew();
        if (configuration.DnsProvider == DnsProviderMode.System)
        {
            addresses = await Dns.GetHostAddressesAsync("example.com", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            IAppDnsResolver resolver = CreateDnsResolver(configuration);
            try
            {
                addresses = await resolver.ResolveAsync("example.com", configuration.PreferIpv6, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch when (configuration.FallbackToSystemDns && !cancellationToken.IsCancellationRequested)
            {
                fallbackUsed = true;
                addresses = await Dns.GetHostAddressesAsync("example.com", cancellationToken).ConfigureAwait(false);
            }
        }
        dnsStopwatch.Stop();

        TlsProbeResult tls = await ProbeTlsAsync(addresses, cancellationToken).ConfigureAwait(false);

        var httpStopwatch = Stopwatch.StartNew();
        using var client = new HttpClient(CreateHandler(configuration))
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(10, configuration.ConnectTimeout.TotalSeconds + 5))
        };
        using var request = new HttpRequestMessage(HttpMethod.Head, "https://example.com/");
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        httpStopwatch.Stop();
        return new NetworkDiagnosticResult(
            addresses,
            dnsStopwatch.Elapsed,
            httpStopwatch.Elapsed,
            response.StatusCode,
            response.Version,
            configuration.ProxyMode switch
            {
                ProxyMode.Direct => "direct",
                ProxyMode.System => "system proxy",
                _ => $"custom proxy ({configuration.GetValidatedProxyUri().Host}:{configuration.GetValidatedProxyUri().Port})"
            },
            configuration.DnsProvider == DnsProviderMode.System
                ? "Windows system resolver"
                : $"{configuration.DnsProvider} via {configuration.DnsTransport}",
            fallbackUsed,
            tls.Protocol,
            tls.CipherSuite,
            tls.CertificateSubject,
            tls.CertificateIssuer,
            tls.CertificateExpiryUtc,
            tls.Error);
    }

    private static async ValueTask<Stream> ConnectAsync(
        DnsEndPoint destination,
        IAppDnsResolver resolver,
        NetworkConfiguration configuration,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await resolver.ResolveAsync(
                destination.Host,
                configuration.PreferIpv6,
                cancellationToken).ConfigureAwait(false);
        }
        catch when (configuration.FallbackToSystemDns && !cancellationToken.IsCancellationRequested)
        {
            addresses = await Dns.GetHostAddressesAsync(destination.Host, cancellationToken).ConfigureAwait(false);
        }

        Exception? lastError = null;
        foreach (IPAddress address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            try
            {
                await socket.ConnectAsync(address, destination.Port, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                if (ex is OperationCanceledException)
                    throw;
                lastError = ex;
            }
        }

        throw new HttpRequestException($"Unable to connect to {destination.Host}:{destination.Port}.", lastError);
    }

    internal static IAppDnsResolver CreateDnsResolver(NetworkConfiguration configuration) =>
        configuration.DnsTransport switch
        {
            DnsTransport.Udp => new CustomDnsResolver(DnsServerEndpoint.Parse(configuration.DnsServer)),
            DnsTransport.Https or DnsTransport.Tls => new SecureDnsResolver(configuration),
            _ => throw new InvalidOperationException("Unsupported DNS transport.")
        };

    private static async Task<TlsProbeResult> ProbeTlsAsync(
        IReadOnlyList<IPAddress> addresses,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        foreach (IPAddress address in addresses)
        {
            using var tcp = new TcpClient(address.AddressFamily);
            try
            {
                await tcp.ConnectAsync(address, 443, cancellationToken).ConfigureAwait(false);
                await using var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "example.com",
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.Online
                }, cancellationToken).ConfigureAwait(false);
                using var certificate = tls.RemoteCertificate == null
                    ? null
                    : new X509Certificate2(tls.RemoteCertificate);
                return new TlsProbeResult(
                    tls.SslProtocol,
                    tls.NegotiatedCipherSuite,
                    certificate?.Subject ?? "Unavailable",
                    certificate?.Issuer ?? "Unavailable",
                    certificate?.NotAfter.ToUniversalTime() ?? DateTime.MinValue);
            }
            catch (Exception ex) when (ex is SocketException or AuthenticationException or IOException)
            {
                lastError = ex;
            }
        }
        return new TlsProbeResult(
            SslProtocols.None,
            null,
            "Unavailable",
            "Unavailable",
            DateTime.MinValue,
            lastError?.Message ?? "Direct connection was unavailable.");
    }

    private sealed record TlsProbeResult(
        SslProtocols Protocol,
        TlsCipherSuite? CipherSuite,
        string CertificateSubject,
        string CertificateIssuer,
        DateTime CertificateExpiryUtc,
        string Error = "");
}
