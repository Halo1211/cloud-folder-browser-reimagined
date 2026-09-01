using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CloudFolderBrowser.Accounts;
using CloudFolderBrowser.Networking;

namespace CloudFolderBrowser.Tests;

public sealed class DebridResolverTests
{
    [Theory]
    [InlineData(CloudAccountProvider.AllDebrid, "{\"status\":\"success\",\"data\":{\"link\":\"https://cdn.example/all.bin\",\"filename\":\"all.bin\",\"filesize\":42}}", "https://cdn.example/all.bin")]
    [InlineData(CloudAccountProvider.RealDebrid, "{\"download\":\"https://cdn.example/real.bin\",\"filename\":\"real.bin\",\"filesize\":43}", "https://cdn.example/real.bin")]
    [InlineData(CloudAccountProvider.DebridLink, "{\"success\":true,\"value\":{\"downloadUrl\":\"https://cdn.example/link.bin\",\"name\":\"link.bin\",\"size\":44}}", "https://cdn.example/link.bin")]
    [InlineData(CloudAccountProvider.Premiumize, "{\"status\":\"success\",\"content\":[{\"path\":\"premium.bin\",\"size\":45,\"link\":\"https://cdn.example/premium.bin\"}]}", "https://cdn.example/premium.bin")]
    public async Task ResolveAsync_ParsesOfficialProviderResponse(
        CloudAccountProvider provider,
        string responseJson,
        string expectedUrl)
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var handler = new StubHandler(async request =>
        {
            captured = request;
            body = request.Content == null ? null : await request.Content.ReadAsStringAsync();
            return Json(responseJson);
        });
        using var client = new HttpClient(handler);
        var profile = new CloudAccountProfile
        {
            Provider = provider,
            DisplayName = "Test",
            Secret = "secret-token"
        };
        var resolver = new DebridLinkResolver(profile, client);

        DebridResolvedLink result = await resolver.ResolveAsync(
            new Uri("https://www.terabox.com/s/1example"),
            CancellationToken.None);

        Assert.Equal(expectedUrl, result.DownloadUri.AbsoluteUri);
        Assert.Equal("Bearer", captured?.Headers.Authorization?.Scheme);
        Assert.Equal("secret-token", captured?.Headers.Authorization?.Parameter);
        Assert.Contains("terabox.com", body ?? string.Empty);
    }

    [Fact]
    public async Task TorBox_TeraBoxShareRequestsZipAndReturnsPreparedLink()
    {
        var requests = new List<(Uri Uri, string Body)>();
        var handler = new StubHandler(async request =>
        {
            string body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync();
            requests.Add((request.RequestUri!, body));
            return requests.Count == 1
                ? Json("{\"success\":true,\"data\":[{\"name\":\"Terabox\",\"domains\":[\"terabox.com\",\"dubox.com\"],\"status\":true}]}")
                : requests.Count == 2
                ? Json("{\"success\":true,\"data\":{\"webdownload_id\":77,\"name\":\"Shared folder\"}}")
                : Json("{\"success\":true,\"data\":\"https://cdn.example/terabox-folder.zip\"}");
        });
        using var client = new HttpClient(handler);
        var resolver = new DebridLinkResolver(new CloudAccountProfile
        {
            Provider = CloudAccountProvider.TorBox,
            DisplayName = "TorBox",
            Secret = "torbox-token"
        }, client);

        DebridResolvedLink result = await resolver.ResolveAsync(
            new Uri("https://www.terabox.com/s/1example"),
            CancellationToken.None);

        Assert.Equal("https://cdn.example/terabox-folder.zip", result.DownloadUri.AbsoluteUri);
        Assert.Equal(3, requests.Count);
        Assert.EndsWith("/webdl/hosters", requests[0].Uri.AbsolutePath);
        Assert.Contains("terabox.com", requests[1].Body);
        Assert.Contains("zip_link=true", requests[2].Uri.Query);
        Assert.Contains("token=torbox-token", requests[2].Uri.Query);
    }

    [Fact]
    public async Task TorBox_TeraBoxShareReportsTemporarilyUnavailableHost()
    {
        using var client = new HttpClient(new StubHandler(_ => Task.FromResult(Json(
            "{\"success\":true,\"data\":[{\"name\":\"Terabox\",\"domains\":[\"terabox.com\"],\"status\":false}]}"))));
        var resolver = new DebridLinkResolver(new CloudAccountProfile
        {
            Provider = CloudAccountProvider.TorBox,
            DisplayName = "TorBox",
            Secret = "token"
        }, client);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(new Uri("https://www.terabox.com/s/1example"), CancellationToken.None));
        Assert.Contains("currently reports TeraBox as unavailable", error.Message);
    }

    [Fact]
    public async Task ResolveAsync_DoesNotAcceptProviderErrorAsDownloadLink()
    {
        using var client = new HttpClient(new StubHandler(_ => Task.FromResult(Json(
            "{\"status\":\"error\",\"error\":{\"message\":\"host limit reached\"}}"))));
        var resolver = new DebridLinkResolver(new CloudAccountProfile
        {
            Provider = CloudAccountProvider.AllDebrid,
            DisplayName = "Test",
            Secret = "token"
        }, client);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(new Uri("https://mega.nz/file/example#key"), CancellationToken.None));
        Assert.Contains("host limit reached", error.Message);
    }

    [Fact]
    public async Task AutomaticRoute_UsesDirectThenFallsBackToDebridAfterAccessFailure()
    {
        Guid accountId = Guid.NewGuid();
        var account = new CloudAccountProfile
        {
            Id = accountId,
            Provider = CloudAccountProvider.RealDebrid,
            DisplayName = "Primary",
            Secret = "token",
            IsActive = true
        };
        var fallback = new StubResolver(new Uri("https://cdn.example/fallback.bin"));
        using var automatic = new AutomaticDownloadLinkResolver(
            new[] { account },
            preferredAccountId: null,
            _ => fallback);
        var file = new CloudFile("payload.bin", DateTime.MinValue, DateTime.MinValue, 1)
        {
            PublicUrl = new Uri("https://hoster.example/file/1"),
            DownloadHistoryId = Guid.NewGuid()
        };

        DebridResolvedLink direct = await automatic.ResolveAsync(
            file, file.PublicUrl, CancellationToken.None);
        Assert.Equal(DownloadRouteIds.Direct, direct.RouteId);

        automatic.ReportFailure(
            file,
            file.PublicUrl,
            new HttpRequestException("Forbidden", null, HttpStatusCode.Forbidden));
        DebridResolvedLink resolved = await automatic.ResolveAsync(
            file, file.PublicUrl, CancellationToken.None);

        Assert.Equal("https://cdn.example/fallback.bin", resolved.DownloadUri.AbsoluteUri);
        Assert.Equal(DownloadRouteIds.ForAccount(accountId), resolved.RouteId);
        Assert.Equal("Real-Debrid", resolved.RouteDisplayName);
        Assert.Equal(1, fallback.ResolveCount);
    }

    [Fact]
    public async Task AutomaticRoute_RequiredLinkSkipsDirectAndContinuesAfterProviderFailure()
    {
        var first = new CloudAccountProfile
        {
            Id = Guid.NewGuid(),
            Provider = CloudAccountProvider.AllDebrid,
            DisplayName = "First",
            Secret = "token-1",
            IsActive = true
        };
        var second = new CloudAccountProfile
        {
            Id = Guid.NewGuid(),
            Provider = CloudAccountProvider.TorBox,
            DisplayName = "Second",
            Secret = "token-2",
            IsActive = true
        };
        using var automatic = new AutomaticDownloadLinkResolver(
            new[] { first, second },
            preferredAccountId: null,
            profile => profile.Id == first.Id
                ? new FailingResolver("host unsupported")
                : new StubResolver(new Uri("https://cdn.example/terabox.zip")));
        var file = new CloudFile("terabox.zip", DateTime.MinValue, DateTime.MinValue, 0)
        {
            PublicUrl = new Uri("https://www.terabox.com/s/example"),
            RequiresLinkResolver = true,
            HasKnownSize = false,
            DownloadHistoryId = Guid.NewGuid()
        };

        DebridResolvedLink resolved = await automatic.ResolveAsync(
            file, file.PublicUrl, CancellationToken.None);

        Assert.Equal("https://cdn.example/terabox.zip", resolved.DownloadUri.AbsoluteUri);
        Assert.Equal(DownloadRouteIds.ForAccount(second.Id), resolved.RouteId);
        Assert.Equal("TorBox", resolved.RouteDisplayName);
    }

    [Fact]
    public async Task AutomaticRoute_CoolsDownFailingProviderForOtherFilesOnSameHost()
    {
        var first = new CloudAccountProfile
        {
            Id = Guid.NewGuid(),
            Provider = CloudAccountProvider.AllDebrid,
            DisplayName = "Unhealthy",
            Secret = "token-1",
            IsActive = true
        };
        var second = new CloudAccountProfile
        {
            Id = Guid.NewGuid(),
            Provider = CloudAccountProvider.TorBox,
            DisplayName = "Healthy",
            Secret = "token-2",
            IsActive = true
        };
        var failing = new FailingResolver("host temporarily unavailable");
        var healthy = new StubResolver(new Uri("https://cdn.example/result.bin"));
        using var automatic = new AutomaticDownloadLinkResolver(
            new[] { first, second },
            preferredAccountId: null,
            profile => profile.Id == first.Id ? failing : healthy);
        CloudFile firstFile = RequiredFile("https://hoster.example/file/one");
        CloudFile secondFile = RequiredFile("https://hoster.example/file/two");

        await automatic.ResolveAsync(firstFile, firstFile.PublicUrl, CancellationToken.None);
        await automatic.ResolveAsync(secondFile, secondFile.PublicUrl, CancellationToken.None);

        Assert.Equal(1, failing.ResolveCount);
        Assert.Equal(2, healthy.ResolveCount);
    }

    [Fact]
    public async Task AutomaticRouteScoring_PrefersPreviouslySuccessfulProviderAfterCooldown()
    {
        bool original = Properties.Settings.Default.routeScoringEnabled;
        Properties.Settings.Default.routeScoringEnabled = true;
        try
        {
            DateTime now = new(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);
            var first = new CloudAccountProfile
            {
                Id = Guid.NewGuid(),
                Provider = CloudAccountProvider.AllDebrid,
                DisplayName = "Unreliable",
                Secret = "token-1",
                IsActive = true
            };
            var second = new CloudAccountProfile
            {
                Id = Guid.NewGuid(),
                Provider = CloudAccountProvider.TorBox,
                DisplayName = "Reliable",
                Secret = "token-2",
                IsActive = true
            };
            var failing = new FailingResolver("temporary resolver failure");
            var healthy = new StubResolver(new Uri("https://cdn.example/scored.bin"));
            using var automatic = new AutomaticDownloadLinkResolver(
                new[] { first, second },
                preferredAccountId: null,
                profile => profile.Id == first.Id ? failing : healthy,
                () => now);

            CloudFile learningFile = RequiredFile("https://scored-hoster.example/file/one");
            await automatic.ResolveAsync(
                learningFile, learningFile.PublicUrl, CancellationToken.None);
            now = now.AddMinutes(2);
            CloudFile nextFile = RequiredFile("https://scored-hoster.example/file/two");
            DebridResolvedLink resolved = await automatic.ResolveAsync(
                nextFile, nextFile.PublicUrl, CancellationToken.None);

            Assert.Equal(DownloadRouteIds.ForAccount(second.Id), resolved.RouteId);
            Assert.Equal(1, failing.ResolveCount);
            Assert.Equal(2, healthy.ResolveCount);
        }
        finally
        {
            Properties.Settings.Default.routeScoringEnabled = original;
        }
    }

    private static CloudFile RequiredFile(string url) => new(
        "payload.bin", DateTime.MinValue, DateTime.MinValue, 0)
    {
        PublicUrl = new Uri(url),
        RequiresLinkResolver = true,
        HasKnownSize = false,
        DownloadHistoryId = Guid.NewGuid()
    };

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;
        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => _handler(request);
    }

    private sealed class StubResolver : IDownloadLinkResolver
    {
        private readonly Uri _result;
        public int ResolveCount { get; private set; }
        public string DisplayName => "Stub";

        public StubResolver(Uri result) => _result = result;

        public Task<DebridResolvedLink> ResolveAsync(Uri source, CancellationToken cancellationToken)
        {
            ResolveCount++;
            return Task.FromResult(new DebridResolvedLink(_result));
        }
    }

    private sealed class FailingResolver : IDownloadLinkResolver
    {
        private readonly string _message;
        public int ResolveCount { get; private set; }
        public string DisplayName => "Failing";
        public FailingResolver(string message) => _message = message;
        public Task<DebridResolvedLink> ResolveAsync(Uri source, CancellationToken cancellationToken)
        {
            ResolveCount++;
            return Task.FromException<DebridResolvedLink>(new InvalidOperationException(_message));
        }
    }
}
