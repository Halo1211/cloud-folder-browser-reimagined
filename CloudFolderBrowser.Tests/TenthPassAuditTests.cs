using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using CloudFolderBrowser.Networking;
using CloudFolderBrowser.Providers;
using CloudFolderBrowser.Sync;
using CloudFolderBrowser.Accounts;
using System.Text;

namespace CloudFolderBrowser.Tests;

[Collection("Real download integration")]
public sealed class TenthPassAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cfb-tenth-audit-" + Guid.NewGuid().ToString("N"));
    public TenthPassAuditTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("dropbox", "ftp://www.dropbox.com/s/key/file.bin")]
    [InlineData("google", "ftp://drive.google.com/file/d/key/view")]
    [InlineData("dropbox", "file.bin")]
    [InlineData("google", "file.bin")]
    public void PublicProviders_RejectNonHttpAndRelativeUris(string id, string url)
    {
        PublicFileProviderBase provider = id == "dropbox"
            ? new DropboxPublicFileProvider() : new GoogleDrivePublicFileProvider();
        Assert.False(provider.CanHandle(new Uri(url, UriKind.RelativeOrAbsolute)));
    }

    [Theory]
    [InlineData(204)]
    [InlineData(205)]
    [InlineData(206)]
    public async Task PublicMetadata_DoesNotTreatNonFileResponsesAsKnownSize(int status)
    {
        await using var server = new LoopbackDownloadServer((_, _) => new LoopbackResponse
        {
            StatusCode = (HttpStatusCode)status,
            Body = status == 206 ? new byte[5] : Array.Empty<byte>()
        });
        CloudFolder root = await new GenericHttpFileProvider().LoadAsync(server.Url("archive.bin").AbsoluteUri);
        CloudFile file = Assert.Single(root.Files);
        Assert.False(file.HasKnownSize);
        Assert.Equal("archive.bin", file.Name);
    }

    [Fact]
    public async Task PublicMetadata_DoesNotUseHtmlChallengeSizeOrRedirectAsFileIdentity()
    {
        await using var server = new LoopbackDownloadServer((request, _) =>
        {
            if (request.Path == "/archive.bin")
            {
                var redirect = new LoopbackResponse { StatusCode = HttpStatusCode.Found };
                redirect.Headers["Location"] = "/login";
                return redirect;
            }
            return new LoopbackResponse { ContentType = "text/html", Body = "<html>Sign in</html>"u8.ToArray() };
        });
        Uri source = server.Url("archive.bin");
        CloudFile file = Assert.Single((await new GenericHttpFileProvider().LoadAsync(source.AbsoluteUri)).Files);
        Assert.False(file.HasKnownSize);
        Assert.Equal(source, file.PublicUrl);
    }

    [Fact]
    public async Task PublicMetadata_StillAcceptsHtmlFilesAndEmptyFiles()
    {
        await using var server = new LoopbackDownloadServer((request, _) => new LoopbackResponse
        {
            ContentType = request.Path.EndsWith(".html") ? "text/html" : "application/octet-stream",
            Body = request.Path.EndsWith(".html") ? "<html>Document</html>"u8.ToArray() : Array.Empty<byte>()
        });
        var provider = new GenericHttpFileProvider();
        CloudFile html = Assert.Single((await provider.LoadAsync(server.Url("page.html").AbsoluteUri)).Files);
        Assert.True(html.HasKnownSize);
        Assert.Equal(21, html.Size);
        CloudFile empty = Assert.Single((await provider.LoadAsync(server.Url("empty.bin").AbsoluteUri)).Files);
        Assert.True(empty.HasKnownSize);
        Assert.Equal(0, empty.Size);
    }

    [Theory]
    [InlineData("Digest")]
    [InlineData("x-goog-hash")]
    [InlineData("Content-MD5")]
    public void RemoteChecksum_IgnoresHashesWithInvalidByteLength(string header)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        string invalid = Convert.ToBase64String(new byte[1]);
        if (header == "Content-MD5")
            response.Content.Headers.TryAddWithoutValidation(header, invalid);
        else
            response.Headers.TryAddWithoutValidation(header, "md5=" + invalid);
        Assert.Null(RemoteChecksumValidator.Read(response));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RemoteChecksum_PrefersValidSha256RegardlessOfHeaderOrder(bool md5First)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        string md5 = "md5=" + Convert.ToBase64String(MD5.HashData([]));
        string sha = "sha-256=" + Convert.ToBase64String(SHA256.HashData([]));
        response.Headers.TryAddWithoutValidation("Digest", md5First ? md5 + ", " + sha : sha + ", " + md5);
        Assert.Equal("SHA256", RemoteChecksumValidator.Read(response)?.Algorithm);
    }

    [Theory]
    [InlineData(false, "127.0.0.1")]
    [InlineData(false, "localhost")]
    [InlineData(true, "127.0.0.1")]
    [InlineData(true, "localhost")]
    public async Task Dns_CancelledLookupDoesNotSucceedThroughFastPaths(bool secure, string host)
    {
        IAppDnsResolver resolver = secure
            ? new SecureDnsResolver(new NetworkConfiguration { DnsProvider = DnsProviderMode.Cloudflare, DnsTransport = DnsTransport.Https })
            : new CustomDnsResolver(new DnsServerEndpoint(IPAddress.Loopback, 53));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(host, cancellationToken: cancelled.Token));
    }

    [Fact]
    public async Task SyncPlan_CancelledEmptySelectionStillReportsCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MainFormModel().BuildSyncPlanAsync(
            [], [], _root, true, false, cancelled.Token));
    }

    [Fact]
    public void SyncPreview_ResetPreservesSuggestedRenameAndValidationDoesNotMutatePlan()
    {
        RunOnUiThread(() =>
        {
            var file = new CloudFile("original.bin", DateTime.MinValue, DateTime.MinValue, 1) { Path = "/original.bin" };
            var item = new SyncPlanItem(file, SyncDifference.SizeMismatch, SyncPlanAction.Rename, "fixture",
                Path.Combine(_root, "original.bin")) { TargetName = "suggested.bin" };
            using var form = new SyncPreviewForm([item]);
            var rows = (System.Collections.IList)typeof(SyncPreviewForm)
                .GetField("_rows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
            object row = rows[0]!;
            Type type = row.GetType();
            type.GetProperty("TargetName")!.SetValue(row, "edited.bin");
            type.GetMethod("GetTargetPath")!.Invoke(row, null);
            Assert.Equal("suggested.bin", item.TargetName);
            type.GetMethod("Reset")!.Invoke(row, null);
            Assert.Equal("suggested.bin", type.GetProperty("TargetName")!.GetValue(row));
        });
    }

    private static void RunOnUiThread(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        failure?.Throw();
    }

    [Fact]
    public void Accounts_UpsertingAnActiveProfileWithoutAutoActivationKeepsOneActiveAccount()
    {
        var store = new CloudAccountStore(Path.Combine(_root, "accounts.json"));
        store.Upsert(new CloudAccountProfile { Provider = CloudAccountProvider.WebDav, DisplayName = "first" });
        var second = new CloudAccountProfile { Provider = CloudAccountProvider.WebDav, DisplayName = "second", IsActive = true };
        store.Upsert(second, makeActive: false);
        Assert.Equal(second.Id, Assert.Single(store.GetAll(), account => account.IsActive).Id);
    }

    [Theory]
    [InlineData("/", "/public.php/webdav/")]
    [InlineData("/Library+A/日本/", "/public.php/webdav/Library%2BA/%E6%97%A5%E6%9C%AC/")]
    public async Task WebDavSharedListing_UsesRequestedPathAndUtf8Password(string path, string expectedPath)
    {
        await using var server = new LoopbackDownloadServer((_, _) => new LoopbackResponse
        {
            StatusCode = (HttpStatusCode)207,
            ContentType = "application/xml",
            Body = "<d:multistatus xmlns:d=\"DAV:\" />"u8.ToArray()
        });
        var credential = new NetworkCredential("share", "päss_日本");
        var client = NetworkClientAdapters.CreateWebDavClient(credential);
        client.Server = server.BaseUri.GetLeftPart(UriPartial.Authority);
        client.BasePath = "/public.php/webdav/";
        await client.ListShared(path);
        LoopbackRequest request = Assert.Single(server.Requests);
        Assert.Equal(expectedPath, request.Path);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("share:päss_日本")),
            request.Headers["Authorization"]);
    }

    [Fact]
    public async Task WebDavSharedListing_DoesNotSendCredentialsWhenRedirectedServerChallenges()
    {
        await using var foreign = new LoopbackDownloadServer((_, _) =>
        {
            var response = new LoopbackResponse { StatusCode = HttpStatusCode.Unauthorized };
            response.Headers["WWW-Authenticate"] = "Basic realm=\"fixture\"";
            return response;
        });
        await using var source = new LoopbackDownloadServer((_, _) =>
        {
            var response = new LoopbackResponse { StatusCode = HttpStatusCode.TemporaryRedirect };
            response.Headers["Location"] = foreign.Url("foreign").AbsoluteUri;
            return response;
        });
        var client = NetworkClientAdapters.CreateWebDavClient(new NetworkCredential("fixture", "fixture-secret"));
        client.Server = source.BaseUri.GetLeftPart(UriPartial.Authority);
        client.BasePath = "/public.php/webdav/";
        await Assert.ThrowsAsync<WebDAVClient.Helpers.WebDAVException>(() => client.ListShared());
        Assert.NotEmpty(foreign.Requests);
        Assert.All(foreign.Requests, request => Assert.False(request.Headers.ContainsKey("Authorization")));
    }

    [Theory]
    [InlineData("http://one.example/private/file", false)]
    [InlineData("https://one.example/public/file", false)]
    [InlineData("https://two.example/private/file", false)]
    [InlineData("https://one.example/private/file", true)]
    public void BrowserCookieContainer_RespectsSecureDomainAndPath(string target, bool expected)
    {
        var container = new CookieContainer();
        var session = new CloudflareSession("fixture", [new FlareSolverrCookie
        { Name = "clearance", Value = "fixture", Domain = "one.example", Path = "/private", Secure = true }]);
        session.ApplyCookies(container);
        Assert.Equal(expected, container.GetCookieHeader(new Uri(target)).Contains("clearance=fixture"));
        session.ApplyCookies(container, expire: true);
        Assert.Empty(container.GetCookieHeader(new Uri("https://one.example/private/file")));
    }

    [Fact]
    public async Task WebDavCookieContainer_ScopesCookiesAcrossRedirects()
    {
        await using var server = new LoopbackDownloadServer((request, _) =>
        {
            if (request.Path == "/private/")
            {
                var response = new LoopbackResponse { StatusCode = HttpStatusCode.TemporaryRedirect };
                response.Headers["Location"] = "/public/";
                return response;
            }
            return new LoopbackResponse { StatusCode = (HttpStatusCode)207,
                Body = "<d:multistatus xmlns:d=\"DAV:\" />"u8.ToArray(), ContentType = "application/xml" };
        });
        var session = new CloudflareSession("fixture", [new FlareSolverrCookie
        { Name = "clearance", Value = "fixture", Domain = "127.0.0.1", Path = "/private" }]);
        var client = NetworkClientAdapters.CreateWebDavClient(new NetworkCredential("fixture", "fixture"), cloudflareSession: session);
        client.Server = server.BaseUri.GetLeftPart(UriPartial.Authority);
        client.BasePath = "/private/";
        await client.ListShared();
        Assert.Collection(server.Requests,
            request => Assert.Contains("clearance=fixture", request.Headers.GetValueOrDefault("Cookie") ?? ""),
            request => Assert.False(request.Headers.ContainsKey("Cookie")));
    }

    [Fact]
    public async Task WebDavSharedListing_ExcludesTheRequestedParentCollection()
    {
        await using var server = new LoopbackDownloadServer((_, _) => new LoopbackResponse
        {
            StatusCode = (HttpStatusCode)207,
            ContentType = "application/xml",
            Body = """
                <d:multistatus xmlns:d="DAV:">
                  <d:response><d:href>/public.php/webdav/Library%2BA/</d:href>
                    <d:propstat><d:prop><d:resourcetype><d:collection /></d:resourcetype></d:prop></d:propstat></d:response>
                  <d:response><d:href>/public.php/webdav/Library+A/child/</d:href>
                    <d:propstat><d:prop><d:resourcetype><d:collection /></d:resourcetype></d:prop></d:propstat></d:response>
                </d:multistatus>
                """u8.ToArray()
        });
        var client = NetworkClientAdapters.CreateWebDavClient(new NetworkCredential("fixture", "fixture"));
        client.Server = server.BaseUri.GetLeftPart(UriPartial.Authority);
        client.BasePath = "/public.php/webdav/";
        Assert.EndsWith("/child/", Assert.Single(await client.ListShared("/Library+A/")).Href);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
