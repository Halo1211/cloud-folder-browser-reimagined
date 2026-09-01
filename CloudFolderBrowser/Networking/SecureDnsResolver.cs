using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;

namespace CloudFolderBrowser.Networking;

public readonly record struct DnsTlsEndpoint(string Host, int Port, string ServerName)
{
    public static DnsTlsEndpoint Parse(string value)
    {
        value = value?.Trim() ?? string.Empty;
        if (value.StartsWith("tls://", StringComparison.OrdinalIgnoreCase))
            value = value[6..];
        if (!Uri.TryCreate("tls://" + value, UriKind.Absolute, out Uri? uri)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.Port != -1 && uri.Port is < 1 or > 65535))
        {
            throw new FormatException("DNS-over-TLS endpoint must be a host and optional port, for example dns.example.com:853.");
        }
        return new DnsTlsEndpoint(uri.Host, uri.Port == -1 ? 853 : uri.Port, uri.Host);
    }
}

public static class SecureDnsEndpoint
{
    public static Uri GetDohUri(NetworkConfiguration configuration)
    {
        string value = configuration.DnsProvider switch
        {
            DnsProviderMode.Cloudflare => "https://cloudflare-dns.com/dns-query",
            DnsProviderMode.Google => "https://dns.google/dns-query",
            DnsProviderMode.Quad9 => "https://dns.quad9.net/dns-query",
            DnsProviderMode.AdGuard => "https://dns.adguard-dns.com/dns-query",
            DnsProviderMode.Custom => configuration.CustomDnsServer.Trim(),
            _ => throw new InvalidOperationException("Secure DNS is not used with the Windows resolver.")
        };
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new FormatException("Custom DNS-over-HTTPS must be an absolute HTTPS URL.");
        }
        return uri;
    }

    public static DnsTlsEndpoint GetDotEndpoint(NetworkConfiguration configuration) =>
        configuration.DnsProvider switch
        {
            DnsProviderMode.Cloudflare => new DnsTlsEndpoint("1.1.1.1", 853, "one.one.one.one"),
            DnsProviderMode.Google => new DnsTlsEndpoint("8.8.8.8", 853, "dns.google"),
            DnsProviderMode.Quad9 => new DnsTlsEndpoint("9.9.9.9", 853, "dns.quad9.net"),
            DnsProviderMode.AdGuard => new DnsTlsEndpoint("94.140.14.14", 853, "dns.adguard-dns.com"),
            DnsProviderMode.Custom => DnsTlsEndpoint.Parse(configuration.CustomDnsServer),
            _ => throw new InvalidOperationException("Secure DNS is not used with the Windows resolver.")
        };
}

public sealed class SecureDnsResolver : IAppDnsResolver
{
    private const ushort TypeA = 1;
    private const ushort TypeAaaa = 28;
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly DnsTransport _transport;
    private readonly Uri? _dohUri;
    private readonly DnsTlsEndpoint _dotEndpoint;
    private readonly TimeSpan _timeout;

    public SecureDnsResolver(NetworkConfiguration configuration, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _transport = configuration.DnsTransport;
        _timeout = timeout is { } configured && configured > TimeSpan.Zero
            ? configured
            : TimeSpan.FromSeconds(8);
        if (_transport == DnsTransport.Https)
        {
            _dohUri = SecureDnsEndpoint.GetDohUri(configuration);
        }
        else if (_transport == DnsTransport.Tls)
        {
            _dotEndpoint = SecureDnsEndpoint.GetDotEndpoint(configuration);
        }
        else
        {
            throw new ArgumentException("SecureDnsResolver requires HTTPS or TLS transport.", nameof(configuration));
        }
    }

    public async Task<IPAddress[]> ResolveAsync(
        string host,
        bool preferIpv6 = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (IPAddress.TryParse(host, out IPAddress? literal))
            return new[] { literal };
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return Order(new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }, preferIpv6);

        string asciiHost = new IdnMapping().GetAscii(host.TrimEnd('.'));
        string endpoint = _transport == DnsTransport.Https ? _dohUri!.AbsoluteUri : _dotEndpoint.ToString();
        string cacheKey = $"{_transport}:{endpoint}:{asciiHost}";
        if (Cache.TryGetValue(cacheKey, out CacheEntry? cached) && cached.ExpiresUtc > DateTime.UtcNow)
            return Order(cached.Addresses, preferIpv6);

        Task<QueryOutcome> ipv4 = QuerySafelyAsync(asciiHost, TypeA, cancellationToken);
        Task<QueryOutcome> ipv6 = QuerySafelyAsync(asciiHost, TypeAaaa, cancellationToken);
        await Task.WhenAll(ipv4, ipv6).ConfigureAwait(false);
        IPAddress[] addresses = ipv4.Result.Answer.Addresses
            .Concat(ipv6.Result.Answer.Addresses)
            .Distinct()
            .ToArray();
        if (addresses.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        uint ttl = Math.Min(ipv4.Result.Answer.MinimumTtl, ipv6.Result.Answer.MinimumTtl);
        if (ttl == uint.MaxValue)
            ttl = 60;
        Cache[cacheKey] = new CacheEntry(addresses, DateTime.UtcNow.AddSeconds(Math.Clamp(ttl, 5, 3600)));
        return Order(addresses, preferIpv6);
    }

    public static void ClearCache() => Cache.Clear();

    private async Task<QueryOutcome> QuerySafelyAsync(string host, ushort type, CancellationToken cancellationToken)
    {
        try
        {
            return new QueryOutcome(await QueryAsync(host, type, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is TimeoutException or SocketException or InvalidDataException
            or HttpRequestException or AuthenticationException)
        {
            return new QueryOutcome(new CustomDnsResolver.DnsAnswer(Array.Empty<IPAddress>(), uint.MaxValue));
        }
    }

    private async Task<CustomDnsResolver.DnsAnswer> QueryAsync(
        string host,
        ushort type,
        CancellationToken cancellationToken)
    {
        ushort id = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1);
        byte[] query = CustomDnsResolver.BuildQuery(id, host, type);
        byte[] response = _transport == DnsTransport.Https
            ? await QueryDohAsync(query, cancellationToken).ConfigureAwait(false)
            : await QueryDotAsync(query, cancellationToken).ConfigureAwait(false);
        return CustomDnsResolver.ParseResponse(response, id, type);
    }

    private async Task<byte[]> QueryDohAsync(byte[] query, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);
        using var client = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = _timeout
        }) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Post, _dohUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));
        request.Content = new ByteArrayContent(query);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, timeoutCts.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            byte[] payload = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);
            if (payload.Length is < 12 or > 65535)
                throw new InvalidDataException("DNS-over-HTTPS returned an invalid message length.");
            return payload;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("DNS-over-HTTPS request timed out.");
        }
    }

    private async Task<byte[]> QueryDotAsync(byte[] query, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);
        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(_dotEndpoint.Host, _dotEndpoint.Port, timeoutCts.Token).ConfigureAwait(false);
            await using var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = _dotEndpoint.ServerName,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, timeoutCts.Token).ConfigureAwait(false);

            byte[] prefix = { (byte)(query.Length >> 8), (byte)query.Length };
            await tls.WriteAsync(prefix, timeoutCts.Token).ConfigureAwait(false);
            await tls.WriteAsync(query, timeoutCts.Token).ConfigureAwait(false);
            await tls.FlushAsync(timeoutCts.Token).ConfigureAwait(false);
            byte[] lengthBytes = new byte[2];
            await ReadExactlyAsync(tls, lengthBytes, timeoutCts.Token).ConfigureAwait(false);
            int length = (lengthBytes[0] << 8) | lengthBytes[1];
            if (length is < 12 or > 65535)
                throw new InvalidDataException("DNS-over-TLS returned an invalid message length.");
            byte[] response = new byte[length];
            await ReadExactlyAsync(tls, response, timeoutCts.Token).ConfigureAwait(false);
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("DNS-over-TLS request timed out.");
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("Secure DNS connection ended early.");
            offset += read;
        }
    }

    private static IPAddress[] Order(IEnumerable<IPAddress> addresses, bool preferIpv6) => addresses
        .OrderBy(address => preferIpv6
            ? address.AddressFamily == AddressFamily.InterNetworkV6 ? 0 : 1
            : address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
        .ToArray();

    private sealed record CacheEntry(IPAddress[] Addresses, DateTime ExpiresUtc);
    private sealed record QueryOutcome(CustomDnsResolver.DnsAnswer Answer);
}
