using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using CloudFolderBrowser.Networking;

namespace CloudFolderBrowser.Tests;

public sealed class NetworkingTests
{
    [Fact]
    public void RemoteChecksum_RecognizesContentMd5()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
        };
        response.Content.Headers.ContentMD5 = Enumerable.Repeat((byte)7, 16).ToArray();

        RemoteChecksum checksum = Assert.IsType<RemoteChecksum>(
            RemoteChecksumValidator.Read(response));

        Assert.Equal("MD5", checksum.Algorithm);
        Assert.Equal(Enumerable.Repeat((byte)7, 16), checksum.Expected);
    }

    [Fact]
    public async Task RemoteChecksum_RejectsMismatchedFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "cfb-checksum-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3, 4 });
        try
        {
            await Assert.ThrowsAsync<RemoteChecksumMismatchException>(() =>
                RemoteChecksumValidator.ValidateFileAsync(
                    path,
                    new RemoteChecksum("MD5", new byte[16]),
                    CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("1.1.1.1", "1.1.1.1", 53)]
    [InlineData("8.8.8.8:5353", "8.8.8.8", 5353)]
    [InlineData("[2001:4860:4860::8888]:53", "2001:4860:4860::8888", 53)]
    public void DnsServerEndpoint_ParsesAddressAndOptionalPort(string value, string address, int port)
    {
        DnsServerEndpoint endpoint = DnsServerEndpoint.Parse(value);

        Assert.Equal(IPAddress.Parse(address), endpoint.Address);
        Assert.Equal(port, endpoint.Port);
    }

    [Theory]
    [InlineData("dns.example.com")]
    [InlineData("1.1.1.1:70000")]
    [InlineData("")]
    public void DnsServerEndpoint_RejectsUnsafeOrInvalidValues(string value)
    {
        Assert.Throws<FormatException>(() => DnsServerEndpoint.Parse(value));
    }

    [Theory]
    [InlineData("127.0.0.1:8080", "http://127.0.0.1:8080/")]
    [InlineData("socks5://localhost:1080", "socks5://localhost:1080/")]
    public void NetworkConfiguration_NormalizesSupportedProxyUrls(string value, string expected)
    {
        var configuration = new NetworkConfiguration
        {
            ProxyMode = ProxyMode.Custom,
            ProxyUrl = value
        };

        Assert.Equal(expected, configuration.GetValidatedProxyUri().AbsoluteUri);
    }

    [Fact]
    public void NetworkConfiguration_RejectsUnsupportedProxyScheme()
    {
        var configuration = new NetworkConfiguration
        {
            ProxyMode = ProxyMode.Custom,
            ProxyUrl = "ftp://127.0.0.1:21"
        };

        Assert.Throws<FormatException>(() => configuration.Validate());
    }

    [Theory]
    [InlineData("http://user:plaintext@127.0.0.1:8080")]
    [InlineData("http://127.0.0.1:8080/path")]
    [InlineData("http://127.0.0.1:8080?token=plaintext")]
    public void NetworkConfiguration_RejectsProxySecretsOrPathsInUrl(string value)
    {
        var configuration = new NetworkConfiguration
        {
            ProxyMode = ProxyMode.Custom,
            ProxyUrl = value
        };

        Assert.Throws<FormatException>(() => configuration.Validate());
    }

    [Fact]
    public void ProxySecretProtector_RoundTripsWithoutPersistingPlaintext()
    {
        const string password = "proxy-secret-value";

        string protectedValue = ProxySecretProtector.Protect(password);

        Assert.NotEqual(password, protectedValue);
        Assert.DoesNotContain(password, protectedValue);
        Assert.Equal(password, ProxySecretProtector.Unprotect(protectedValue));
    }

    [Fact]
    public void DnsResponseParser_ReadsARecordAndTtl()
    {
        const ushort id = 0x1234;
        byte[] query = CustomDnsResolver.BuildQuery(id, "example.com", 1);
        using var response = new MemoryStream();
        response.Write(query);
        byte[] bytes = response.GetBuffer();
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2, 2), 0x8180);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6, 2), 1);
        response.Position = query.Length;
        response.Write(new byte[]
        {
            0xC0, 0x0C,             // compressed example.com name
            0x00, 0x01,             // A
            0x00, 0x01,             // IN
            0x00, 0x00, 0x00, 0x78, // TTL 120
            0x00, 0x04,
            93, 184, 216, 34
        });

        CustomDnsResolver.DnsAnswer answer = CustomDnsResolver.ParseResponse(response.ToArray(), id, 1);

        Assert.Equal((uint)120, answer.MinimumTtl);
        Assert.Equal(IPAddress.Parse("93.184.216.34"), Assert.Single(answer.Addresses));
    }

    [Fact]
    public void DnsResponseParser_RejectsMismatchedTransaction()
    {
        byte[] response = CustomDnsResolver.BuildQuery(42, "example.com", 1);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2, 2), 0x8180);

        Assert.Throws<InvalidDataException>(() => CustomDnsResolver.ParseResponse(response, 43, 1));
    }

    [Theory]
    [InlineData(DnsProviderMode.Cloudflare, "https://cloudflare-dns.com/dns-query")]
    [InlineData(DnsProviderMode.Google, "https://dns.google/dns-query")]
    [InlineData(DnsProviderMode.Quad9, "https://dns.quad9.net/dns-query")]
    [InlineData(DnsProviderMode.AdGuard, "https://dns.adguard-dns.com/dns-query")]
    public void SecureDnsEndpoint_MapsDohPresets(DnsProviderMode provider, string expected)
    {
        var configuration = new NetworkConfiguration { DnsProvider = provider, DnsTransport = DnsTransport.Https };

        Assert.Equal(expected, SecureDnsEndpoint.GetDohUri(configuration).AbsoluteUri);
    }

    [Fact]
    public void SecureDnsEndpoint_ValidatesCustomDohAndDot()
    {
        var doh = new NetworkConfiguration
        {
            DnsProvider = DnsProviderMode.Custom,
            DnsTransport = DnsTransport.Https,
            CustomDnsServer = "https://resolver.example/dns-query"
        };
        var dot = doh with { DnsTransport = DnsTransport.Tls, CustomDnsServer = "tls://resolver.example:8853" };

        doh.Validate();
        dot.Validate();
        Assert.Equal(8853, SecureDnsEndpoint.GetDotEndpoint(dot).Port);
        Assert.Throws<FormatException>(() => (doh with { CustomDnsServer = "http://resolver.example" }).Validate());
        Assert.Throws<FormatException>(() => (doh with { CustomDnsServer = "https://user:secret@resolver.example/dns-query" }).Validate());
        Assert.Throws<FormatException>(() => (dot with { CustomDnsServer = "resolver.example:853/unexpected" }).Validate());
    }

    [Fact]
    public async Task BandwidthLimiter_UnlimitedModeDoesNotDelay()
    {
        var limiter = new BandwidthLimiter(0);

        await limiter.ThrottleAsync(16 * 1024);

        Assert.False(limiter.IsLimited);
        Assert.Equal(0, limiter.BytesPerSecond);
    }

    [Fact]
    public void SettingsBackupEncryption_RoundTripsAndHidesSecrets()
    {
        var document = new SettingsBackupDocument
        {
            Settings = new SettingsBackupSnapshot { ProxyPassword = "proxy-super-secret" },
            Accounts = new List<CloudFolderBrowser.Accounts.CloudAccountProfile>
            {
                new() { DisplayName = "Personal", Secret = "account-super-secret" }
            }
        };

        byte[] encrypted = SettingsBackupService.Encrypt(document, "strong backup password");
        SettingsBackupDocument restored = SettingsBackupService.Decrypt(encrypted, "strong backup password");

        string raw = Encoding.UTF8.GetString(encrypted);
        Assert.DoesNotContain("proxy-super-secret", raw);
        Assert.DoesNotContain("account-super-secret", raw);
        Assert.Equal("proxy-super-secret", restored.Settings.ProxyPassword);
        Assert.Equal("account-super-secret", Assert.Single(restored.Accounts).Secret);
    }

    [Fact]
    public void SettingsBackupEncryption_RejectsWrongPasswordAndTampering()
    {
        byte[] encrypted = SettingsBackupService.Encrypt(new SettingsBackupDocument(), "correct password");

        Assert.Throws<System.Security.Cryptography.CryptographicException>(() =>
            SettingsBackupService.Decrypt(encrypted, "incorrect password"));
        encrypted[^1] ^= 0x01;
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() =>
            SettingsBackupService.Decrypt(encrypted, "correct password"));
    }

    [Fact]
    public void NetworkDiagnosticsReport_DoesNotExposeProxyCredentials()
    {
        var result = new NetworkDiagnosticResult(
            new[] { IPAddress.Parse("93.184.216.34") },
            TimeSpan.FromMilliseconds(12),
            TimeSpan.FromMilliseconds(34),
            HttpStatusCode.OK,
            HttpVersion.Version20,
            "custom proxy (proxy.example:8080)",
            "Cloudflare via Https",
            false,
            SslProtocols.Tls13,
            TlsCipherSuite.TLS_AES_128_GCM_SHA256,
            "CN=example.com",
            "CN=Example CA",
            DateTime.UtcNow.AddDays(30));

        string report = result.ToReport();

        Assert.Contains("proxy.example:8080", report);
        Assert.DoesNotContain("password", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("username", report, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, ProviderHealthState.Online)]
    [InlineData(HttpStatusCode.Unauthorized, ProviderHealthState.Online)]
    [InlineData(HttpStatusCode.MethodNotAllowed, ProviderHealthState.Online)]
    [InlineData(HttpStatusCode.NotFound, ProviderHealthState.Degraded)]
    [InlineData((HttpStatusCode)429, ProviderHealthState.Degraded)]
    [InlineData(HttpStatusCode.BadGateway, ProviderHealthState.Degraded)]
    public void ProviderHealth_ClassifiesTransportHealth(HttpStatusCode status, ProviderHealthState expected)
    {
        Assert.Equal(expected, ProviderHealthCheckService.Classify(status));
    }

    [Fact]
    public void ProviderHealthTargets_AddConfiguredServicesWithoutLeakingUrlCredentials()
    {
        var accounts = new[]
        {
            new CloudFolderBrowser.Accounts.CloudAccountProfile
            {
                Provider = CloudFolderBrowser.Accounts.CloudAccountProvider.WebDav,
                DisplayName = "Team storage",
                ServerUrl = "https://cloud.example.test/remote.php/dav/files/team/?token=private"
            },
            new CloudFolderBrowser.Accounts.CloudAccountProfile
            {
                Provider = CloudFolderBrowser.Accounts.CloudAccountProvider.WebDav,
                DisplayName = "Unsafe",
                ServerUrl = "https://user:secret@cloud.example.test/"
            }
        };

        IReadOnlyList<ProviderHealthTarget> targets = ProviderHealthCheckService.CreateTargets(
            accounts,
            "https://fog.example.test/?key=private",
            flareSolverrEnabled: true,
            flareSolverrAddress: "http://127.0.0.1:8191/v1");

        ProviderHealthTarget webDav = Assert.Single(targets, target => target.DisplayName == "WebDAV — Team storage");
        Assert.Empty(webDav.Endpoint.Query);
        Assert.DoesNotContain(targets, target => target.DisplayName.Contains("Unsafe", StringComparison.Ordinal));
        Assert.Contains(targets, target => target.RouteKey == "FogLink" && string.IsNullOrEmpty(target.Endpoint.Query));
        Assert.Contains(targets, target => target.RouteKey == "FlareSolverr");
    }
}
