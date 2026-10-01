using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace CloudFolderBrowser.Networking;

public readonly record struct DnsServerEndpoint(IPAddress Address, int Port)
{
    public static DnsServerEndpoint Parse(string value)
    {
        value = value?.Trim() ?? string.Empty;
        if (IPAddress.TryParse(value, out IPAddress? direct))
            return new DnsServerEndpoint(direct, 53);

        if (Uri.TryCreate("udp://" + value, UriKind.Absolute, out Uri? uri)
            && string.IsNullOrEmpty(uri.UserInfo)
            && uri.AbsolutePath == "/"
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && IPAddress.TryParse(uri.Host, out IPAddress? address)
            && uri.Port is >= 1 and <= 65535)
        {
            return new DnsServerEndpoint(address, uri.Port);
        }

        throw new FormatException("Custom DNS must be an IP address, optionally followed by a port (for example 1.1.1.1:53). ");
    }
}

public interface IAppDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, bool preferIpv6 = false, CancellationToken cancellationToken = default);
}

public sealed class CustomDnsResolver : IAppDnsResolver
{
    private const ushort TypeA = 1;
    private const ushort TypeAaaa = 28;
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly DnsServerEndpoint _server;
    private readonly TimeSpan _timeout;

    public CustomDnsResolver(DnsServerEndpoint server, TimeSpan? timeout = null)
    {
        _server = server;
        _timeout = timeout is { } configured && configured > TimeSpan.Zero
            ? configured
            : TimeSpan.FromSeconds(5);
    }

    public async Task<IPAddress[]> ResolveAsync(
        string host,
        bool preferIpv6 = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        cancellationToken.ThrowIfCancellationRequested();
        if (IPAddress.TryParse(host, out IPAddress? literal))
            return new[] { literal };
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return Order(new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }, preferIpv6);

        string asciiHost = new IdnMapping().GetAscii(host.TrimEnd('.'));
        string cacheKey = $"{_server.Address}:{_server.Port}:{asciiHost}";
        if (Cache.TryGetValue(cacheKey, out CacheEntry? cached) && cached.ExpiresUtc > DateTime.UtcNow)
            return Order(cached.Addresses, preferIpv6);

        Task<DnsQueryOutcome> ipv4 = QuerySafelyAsync(asciiHost, TypeA, cancellationToken);
        Task<DnsQueryOutcome> ipv6 = QuerySafelyAsync(asciiHost, TypeAaaa, cancellationToken);
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
        if (ttl > 0)
            Cache[cacheKey] = new CacheEntry(addresses, DateTime.UtcNow.AddSeconds(Math.Min(ttl, 3600)));
        else
            Cache.TryRemove(cacheKey, out _);
        return Order(addresses, preferIpv6);
    }

    public static void ClearCache() => Cache.Clear();

    private async Task<DnsAnswer> QueryAsync(string host, ushort type, CancellationToken cancellationToken)
    {
        ushort id = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1);
        byte[] request = BuildQuery(id, host, type);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);
        using var udp = new UdpClient(_server.Address.AddressFamily);
        udp.Connect(_server.Address, _server.Port);

        try
        {
            await udp.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            UdpReceiveResult result = await udp.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
            return ParseResponse(result.Buffer, id, type, host);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"DNS server {_server.Address}:{_server.Port} did not respond within {_timeout.TotalSeconds:0} seconds.");
        }
    }

    private async Task<DnsQueryOutcome> QuerySafelyAsync(
        string host,
        ushort type,
        CancellationToken cancellationToken)
    {
        try
        {
            return new DnsQueryOutcome(await QueryAsync(host, type, cancellationToken).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is TimeoutException or SocketException or InvalidDataException)
        {
            return new DnsQueryOutcome(new DnsAnswer(Array.Empty<IPAddress>(), uint.MaxValue), ex);
        }
    }

    internal static byte[] BuildQuery(ushort id, string host, ushort type)
    {
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 0x0100);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);
        stream.Write(header);
        foreach (string label in host.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63)
                throw new FormatException("DNS host contains an invalid label.");
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }
        stream.WriteByte(0);
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(tail, type);
        BinaryPrimitives.WriteUInt16BigEndian(tail[2..], 1);
        stream.Write(tail);
        return stream.ToArray();
    }

    internal static DnsAnswer ParseResponse(
        byte[] response, ushort expectedId, ushort expectedType, string? expectedHost = null)
    {
        if (response.Length < 12 || ReadUInt16(response, 0) != expectedId)
            throw new InvalidDataException("DNS server returned a malformed or unrelated response.");

        ushort flags = ReadUInt16(response, 2);
        if ((flags & 0x8000) == 0 || (flags & 0x0200) != 0 || (flags & 0x7800) != 0)
            throw new InvalidDataException("DNS response is incomplete or truncated.");
        int responseCode = flags & 0x000F;
        int questionCount = ReadUInt16(response, 4);
        if (questionCount != 1)
            throw new InvalidDataException("DNS response does not contain the requested question.");
        int answerCount = ReadUInt16(response, 6);
        int offset = 12;
        string questionName = ReadName(response, ref offset);
        EnsureAvailable(response, offset, 4);
        ushort questionType = ReadUInt16(response, offset);
        ushort questionClass = ReadUInt16(response, offset + 2);
        offset += 4;
        if (questionType != expectedType || questionClass != 1
            || expectedHost != null && !questionName.Equals(expectedHost.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("DNS response question does not match the request.");
        if (responseCode == 3)
            return new DnsAnswer(Array.Empty<IPAddress>(), uint.MaxValue);
        if (responseCode != 0)
            throw new InvalidDataException($"DNS server returned error code {responseCode}.");

        var records = new List<(string Owner, IPAddress Address, uint Ttl)>();
        var aliases = new List<(string Owner, string Target, uint Ttl)>();
        uint minimumTtl = uint.MaxValue;
        for (int i = 0; i < answerCount; i++)
        {
            string owner = ReadName(response, ref offset);
            EnsureAvailable(response, offset, 10);
            ushort type = ReadUInt16(response, offset);
            ushort recordClass = ReadUInt16(response, offset + 2);
            uint ttl = ReadUInt32(response, offset + 4);
            int length = ReadUInt16(response, offset + 8);
            offset += 10;
            EnsureAvailable(response, offset, length);

            if (recordClass == 1 && type == expectedType
                && ((type == TypeA && length == 4) || (type == TypeAaaa && length == 16)))
            {
                records.Add((owner, new IPAddress(response.AsSpan(offset, length)), ttl));
            }
            else if (recordClass == 1 && type == 5)
            {
                int aliasOffset = offset;
                string target = ReadName(response, ref aliasOffset);
                if (aliasOffset != offset + length)
                    throw new InvalidDataException("DNS response contains an invalid CNAME record.");
                aliases.Add((owner, target, ttl));
            }
            offset += length;
        }
        var reachable = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            [questionName] = uint.MaxValue
        };
        for (int pass = 0; pass < aliases.Count; pass++)
        {
            bool changed = false;
            foreach (var alias in aliases)
            {
                if (!reachable.TryGetValue(alias.Owner, out uint parentTtl))
                    continue;
                uint aliasTtl = Math.Min(parentTtl, alias.Ttl);
                if (!reachable.TryGetValue(alias.Target, out uint currentTtl) || aliasTtl < currentTtl)
                {
                    reachable[alias.Target] = aliasTtl;
                    changed = true;
                }
            }
            if (!changed)
                break;
        }
        var addresses = new List<IPAddress>();
        foreach (var record in records)
        {
            if (!reachable.TryGetValue(record.Owner, out uint aliasTtl))
                continue;
            addresses.Add(record.Address);
            minimumTtl = Math.Min(minimumTtl, Math.Min(aliasTtl, record.Ttl));
        }
        return new DnsAnswer(addresses.ToArray(), minimumTtl);
    }

    private static string ReadName(byte[] buffer, ref int offset)
    {
        var labels = new List<string>();
        int cursor = offset;
        int nextOffset = -1;
        int totalLength = 0;
        while (true)
        {
            EnsureAvailable(buffer, cursor, 1);
            byte length = buffer[cursor];
            if (length == 0)
            {
                offset = nextOffset >= 0 ? nextOffset : cursor + 1;
                return string.Join('.', labels);
            }
            if ((length & 0xC0) == 0xC0)
            {
                EnsureAvailable(buffer, cursor, 2);
                int target = ((length & 0x3F) << 8) | buffer[cursor + 1];
                if (target < 12 || target >= cursor)
                    throw new InvalidDataException("DNS response contains an invalid name pointer.");
                if (nextOffset < 0)
                    nextOffset = cursor + 2;
                cursor = target;
                continue;
            }
            if ((length & 0xC0) != 0 || length > 63 || labels.Count >= 127)
                throw new InvalidDataException("DNS response contains an invalid name.");
            EnsureAvailable(buffer, cursor + 1, length);
            labels.Add(System.Text.Encoding.ASCII.GetString(buffer, cursor + 1, length));
            totalLength += length + 1;
            if (totalLength > 255)
                throw new InvalidDataException("DNS response contains an overlong name.");
            cursor += 1 + length;
        }
    }

    private static void EnsureAvailable(byte[] buffer, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > buffer.Length - count)
            throw new InvalidDataException("DNS response ended unexpectedly.");
    }

    private static ushort ReadUInt16(byte[] buffer, int offset)
    {
        EnsureAvailable(buffer, offset, 2);
        return BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset, 2));
    }

    private static uint ReadUInt32(byte[] buffer, int offset)
    {
        EnsureAvailable(buffer, offset, 4);
        return BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset, 4));
    }

    private static IPAddress[] Order(IEnumerable<IPAddress> addresses, bool preferIpv6) => addresses
        .OrderBy(address => preferIpv6
            ? address.AddressFamily == AddressFamily.InterNetworkV6 ? 0 : 1
            : address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
        .ToArray();

    private sealed record CacheEntry(IPAddress[] Addresses, DateTime ExpiresUtc);
    internal sealed record DnsAnswer(IPAddress[] Addresses, uint MinimumTtl);
    private sealed record DnsQueryOutcome(DnsAnswer Answer, Exception? Error);
}
