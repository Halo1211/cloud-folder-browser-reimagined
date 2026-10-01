using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using CloudFolderBrowser.Networking;

namespace CloudFolderBrowser.Tests;

public sealed class DnsCacheAuditTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1200)]
    public async Task UdpResolver_DoesNotUseAddressesPastTheirTtl(int ttl, int delayMs)
    {
        await using var server = new DnsFixture((uint)ttl);
        var resolver = new CustomDnsResolver(server.Endpoint, TimeSpan.FromSeconds(2));
        string host = Guid.NewGuid().ToString("N") + ".example";
        var first = await resolver.ResolveAsync(host);
        if (delayMs > 0) await Task.Delay(delayMs);
        var second = await resolver.ResolveAsync(host);
        Assert.Equal(IPAddress.Parse("192.0.2.1"), Assert.Single(first));
        Assert.Equal(IPAddress.Parse("192.0.2.2"), Assert.Single(second));
        Assert.Equal(4, server.Requests);
    }

    [Fact]
    public async Task UdpResolver_ReusesAnUnexpiredAnswer()
    {
        await using var server = new DnsFixture(120);
        var resolver = new CustomDnsResolver(server.Endpoint, TimeSpan.FromSeconds(2));
        string host = Guid.NewGuid().ToString("N") + ".example";
        Assert.Equal(await resolver.ResolveAsync(host), await resolver.ResolveAsync(host));
        Assert.Equal(2, server.Requests);
    }

    [Fact]
    public async Task SecureResolver_ZeroTtlIsNotCached()
    {
        int requests = 0;
        var resolver = Secure((query, _) => Task.FromResult(Answer(query, 0, Interlocked.Increment(ref requests))));
        string host = Guid.NewGuid().ToString("N") + ".example";
        await resolver.ResolveAsync(host);
        await resolver.ResolveAsync(host);
        Assert.Equal(4, requests);
    }

    [Fact]
    public async Task SecureResolver_OneTruncatedConnectionDoesNotDiscardOtherAddressFamily()
    {
        var resolver = Secure((query, _) => IsIpv4(query)
            ? Task.FromResult(Answer(query, 120, 1))
            : Task.FromException<byte[]>(new EndOfStreamException("fixture truncated TLS response")));
        var result = await resolver.ResolveAsync(Guid.NewGuid().ToString("N") + ".example");
        Assert.Equal(IPAddress.Parse("192.0.2.1"), Assert.Single(result));
    }

    [Fact]
    public async Task SecureResolver_ExternalCancellationIsPropagated()
    {
        var resolver = Secure(async (_, token) => { await Task.Delay(10000, token); return Array.Empty<byte>(); });
        using var cancellation = new CancellationTokenSource();
        Task operation = resolver.ResolveAsync(Guid.NewGuid().ToString("N") + ".example", cancellationToken: cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    private static SecureDnsResolver Secure(Func<byte[], CancellationToken, Task<byte[]>> transport) => new(
        new NetworkConfiguration { DnsProvider = DnsProviderMode.Cloudflare, DnsTransport = DnsTransport.Https }, transport);

    private static bool IsIpv4(byte[] query) => BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(query.Length - 4)) == 1;

    private static byte[] Answer(byte[] query, uint ttl, int addressSuffix)
    {
        bool ipv4 = IsIpv4(query);
        using var response = new MemoryStream();
        response.Write(query);
        byte[] bytes = response.GetBuffer();
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), 0x8180);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6), (ushort)(ipv4 ? 1 : 0));
        if (ipv4)
        {
            response.Write(new byte[] { 0xC0, 0x0C, 0, 1, 0, 1 });
            Span<byte> ttlBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(ttlBytes, ttl);
            response.Write(ttlBytes);
            response.Write(new byte[] { 0, 4, 192, 0, 2, (byte)addressSuffix });
        }
        return response.ToArray();
    }

    private sealed class DnsFixture : IAsyncDisposable
    {
        private readonly UdpClient _socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _loop;
        private int _requests;
        private int _ipv4Requests;
        public int Requests => Volatile.Read(ref _requests);
        public DnsServerEndpoint Endpoint => new(IPAddress.Loopback, ((IPEndPoint)_socket.Client.LocalEndPoint!).Port);
        public DnsFixture(uint ttl) => _loop = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    UdpReceiveResult request = await _socket.ReceiveAsync(_shutdown.Token);
                    Interlocked.Increment(ref _requests);
                    int suffix = IsIpv4(request.Buffer) ? Interlocked.Increment(ref _ipv4Requests) : 0;
                    await _socket.SendAsync(Answer(request.Buffer, ttl, suffix), request.RemoteEndPoint, _shutdown.Token);
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        });
        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            await _loop;
            _socket.Dispose();
            _shutdown.Dispose();
        }
    }
}
