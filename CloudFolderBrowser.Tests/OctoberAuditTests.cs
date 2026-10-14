using System.Net;
using System.Reflection;
using System.Text;
using System.Diagnostics;
using CloudFolderBrowser.Networking;
using CloudFolderBrowser.Providers;
using Newtonsoft.Json;

namespace CloudFolderBrowser.Tests;

[Collection("Real download integration")]
public sealed class OctoberAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cfb-october-audit-" + Guid.NewGuid().ToString("N"));
    public OctoberAuditTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Allsync_DefaultPasswordLoaderReadsEncryptedCache()
    {
        string original = Properties.Settings.Default.savedPasswordsJson;
        try
        {
            var cache = new Dictionary<string, string> { ["https://allsync.com/s/share"] = "fixture-password" };
            Properties.Settings.Default.savedPasswordsJson = ShareCredentialCacheCodec.Serialize(cache);
            var loader = typeof(AllsyncPublicShareProvider).GetMethod("LoadPasswords", BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.Equal(cache, Assert.IsType<Dictionary<string, string>>(loader.Invoke(null, null)));
        }
        finally { Properties.Settings.Default.savedPasswordsJson = original; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Allsync_UsesOnlyCredentialsScopedToSource(bool scoped)
    {
        const string xml = "<d:multistatus xmlns:d=\"DAV:\"><d:response><d:href>/public.php/webdav/file.bin</d:href><d:propstat><d:prop><d:getcontentlength>1</d:getcontentlength></d:prop></d:propstat></d:response></d:multistatus>";
        await using var server = new LoopbackDownloadServer((_, _) => new LoopbackResponse
        { StatusCode = (HttpStatusCode)207, Body = Encoding.UTF8.GetBytes(xml), ContentType = "application/xml" });
        Uri source = server.Url("s/share");
        var passwords = new Dictionary<string, string>
        { [scoped ? ShareCredentialCacheCodec.GetScopeKey(source, "share") : "share"] = "fixture-password" };
        Dictionary<string, string>? saved = null;
        var provider = new AllsyncPublicShareProvider(() => passwords, value => saved = new(value));
        await provider.LoadAsync(new CloudProviderLoadContext(), source.AbsoluteUri);
        string expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("share:" + (scoped ? "fixture-password" : "null")));
        Assert.Equal("Basic " + expected, Assert.Single(server.Requests).Headers["Authorization"]);
        if (scoped) Assert.Equal("fixture-password", saved![ShareCredentialCacheCodec.GetScopeKey(source, "share")]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Allsync_AcceptsAbsoluteSameOriginDavHrefs(bool absolute)
    {
        string href = string.Empty;
        await using var server = new LoopbackDownloadServer((_, _) => new LoopbackResponse
        {
            StatusCode = (HttpStatusCode)207, ContentType = "application/xml",
            Body = Encoding.UTF8.GetBytes($"<d:multistatus xmlns:d=\"DAV:\"><d:response><d:href>{href}</d:href><d:propstat><d:prop><d:getcontentlength>1</d:getcontentlength></d:prop></d:propstat></d:response></d:multistatus>")
        });
        href = absolute ? server.Url("public.php/webdav/file.bin").AbsoluteUri : "/public.php/webdav/file.bin";
        var provider = new AllsyncPublicShareProvider(() => new(), _ => { });
        var root = await provider.LoadAsync(new CloudProviderLoadContext(), server.Url("s/share").AbsoluteUri);
        Assert.Equal("/file.bin", Assert.Single(root.Files).Path);
    }

    [Theory]
    [InlineData("1.2 MB", false)]
    [InlineData("2 KiB", false)]
    [InlineData("1234 B", true)]
    [InlineData("1234", true)]
    public async Task WebIndex_OnlyByteCountsAreExact(string size, bool exact)
    {
        string html = $"<html><body><table><tr><td><img alt='file'/></td><td><a href='file.bin'>file.bin</a></td><td></td><td>{size}</td></tr></table></body></html>";
        await using var server = new LoopbackDownloadServer((_, _) => new LoopbackResponse
        { Body = Encoding.UTF8.GetBytes(html), ContentType = "text/html" });
        var root = await new H5AiIndexProvider().LoadAsync(new CloudProviderLoadContext(), server.Url("h5ailink/").AbsoluteUri);
        Assert.Equal(exact, Assert.Single(root.Files).HasKnownSize);
    }

    [Theory]
    [InlineData("report%20final.bin")]
    [InlineData("report%2Ffinal.bin")]
    public async Task PublicMetadata_PreservesLiteralPercentNames(string name)
    {
        await using var server = new LoopbackDownloadServer((_, _) =>
        {
            var response = new LoopbackResponse { Body = new byte[1] };
            response.Headers["Content-Disposition"] = $"attachment; filename=\"{name}\"";
            return response;
        });
        var file = Assert.Single((await new GenericHttpFileProvider().LoadAsync(server.Url("file").AbsoluteUri)).Files);
        Assert.Equal(name, Path.GetFileName(Utility.GetSafeDownloadPath(_root, file.Path)));
    }

    [Theory]
    [InlineData("yandex")]
    [InlineData("h5ai")]
    [InlineData("trove")]
    [InlineData("allsync")]
    [InlineData("mega")]
    [InlineData("generic")]
    [InlineData("terabox")]
    public void Providers_RelativeUrisAreRejectedWithoutThrowing(string id)
    {
        ICloudProviderDescriptor provider = id switch
        {
            "yandex" => new YandexPublicFolderProvider(), "h5ai" => new H5AiIndexProvider(),
            "trove" => new TheTroveIndexProvider(), "allsync" => new AllsyncPublicShareProvider(),
            "mega" => new MegaPublicFolderProvider(), "terabox" => new TeraBoxPublicShareProvider(),
            _ => new GenericHttpFileProvider()
        };
        Assert.False(provider.CanHandle(new Uri("file.bin", UriKind.Relative)));
    }

    [Theory]
    [InlineData("{\"Name\":\"root\",\"Path\":null}")]
    [InlineData("{\"Name\":\"root\",\"Path\":\"/\",\"Size\":-1}")]
    [InlineData("{\"Name\":\"root\",\"Path\":\"/\",\"Files\":[{\"Name\":null,\"Path\":\"/file\"}]}")]
    [InlineData("{\"Name\":\"root\",\"Path\":\"/\",\"Files\":[{\"Name\":\"file\",\"Path\":null}]}")]
    [InlineData("{\"Name\":\"root\",\"Path\":\"/\",\"Files\":[{\"Name\":\"file\",\"Path\":\"/file\",\"Size\":-1}]}")]
    public void SavedCache_RejectsInvalidTreeMetadata(string json)
    {
        Assert.Throws<JsonSerializationException>(() => SavedFolderCache.Deserialize(json));
    }

    [Fact]
    public async Task Allsync_EmptyShareLoadsAnEmptyRoot()
    {
        await using var server = new LoopbackDownloadServer((_, _) => new LoopbackResponse
        { StatusCode = (HttpStatusCode)207, ContentType = "application/xml", Body = "<d:multistatus xmlns:d='DAV:'/>"u8.ToArray() });
        var provider = new AllsyncPublicShareProvider(() => new(), _ => { });
        var root = await provider.LoadAsync(new CloudProviderLoadContext(), server.Url("s/share").AbsoluteUri);
        Assert.Empty(root.Files);
        Assert.Empty(root.Subfolders);
    }

    [Fact]
    public async Task Allsync_ReturnsAuthenticatedDownloadContextWithoutSerializingSecrets()
    {
        const string xml = "<d:multistatus xmlns:d='DAV:'><d:response><d:href>/public.php/webdav/file.bin</d:href><d:propstat><d:prop/></d:propstat></d:response></d:multistatus>";
        await using var server = new LoopbackDownloadServer((_, _) => new LoopbackResponse
        { StatusCode = (HttpStatusCode)207, ContentType = "application/xml", Body = Encoding.UTF8.GetBytes(xml) });
        Uri source = server.Url("s/share");
        var provider = new AllsyncPublicShareProvider(
            () => new() { [ShareCredentialCacheCodec.GetScopeKey(source, "share")] = "fixture-password" }, _ => { });
        var root = await provider.LoadAsync(new CloudProviderLoadContext(), source.AbsoluteUri);
        Assert.NotNull(root.DownloadCredential);
        Assert.Equal("share", root.DownloadCredential.UserName);
        Assert.Equal("fixture-password", root.DownloadCredential.Password);
        Assert.DoesNotContain("fixture-password", SavedFolderCache.Serialize(root));
    }

    [Fact]
    public void SearchCallback_AfterDisposalDoesNotThrowOrRaiseEvents()
    {
        RunSta(() =>
        {
            var box = new CloudFolderBrowser.TextBox();
            int events = 0;
            box.TextChangedComplete += (_, _) => events++;
            box.Dispose();
            var elapsed = typeof(CloudFolderBrowser.TextBox).GetMethod("timer_Elapsed", BindingFlags.Instance | BindingFlags.NonPublic)!;
            elapsed.Invoke(box, new object?[] { null, null });
            typeof(CloudFolderBrowser.TextBox).GetMethod("OnTextChangedComplete", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(box, null);
            Assert.Equal(0, events);
        });
    }

    [Fact]
    public void SearchCallback_QueuedOldChangeDoesNotCompleteNewSearchPrematurely()
    {
        RunSta(() =>
        {
            using var box = new CloudFolderBrowser.TextBox { TextChangedCompleteDelay = TimeSpan.FromSeconds(30) };
            _ = box.Handle;
            int events = 0;
            box.TextChangedComplete += (_, _) => events++;
            box.Text = "first";
            typeof(CloudFolderBrowser.TextBox).GetMethod("timer_Elapsed", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(box, new object?[] { null, null });
            box.Text = "second";
            Application.DoEvents();
            Assert.Equal(0, events);
        });
    }

    private static void RunSta(Action action)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "UI check did not finish.");
        failure?.Throw();
    }

    [Theory]
    [InlineData("9007199254740993 B", 9007199254740993L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    public void WebIndex_ExactLargeByteCountsDoNotLosePrecision(string value, long expected)
    {
        Assert.Equal(expected, WebIndexProviderUtilities.ParseSizeToBytes(value));
    }

    [Fact]
    public void PortableProviderContext_ReplacesStaleCredentialsAndRemembersNewShare()
    {
        var model = new MainFormModel { WebdavCredential = new NetworkCredential("old", "old-fixture") };
        var folder = new CloudFolder("root", DateTime.MinValue, DateTime.MinValue, 0)
        {
            OriginalString = "https://allsync.com/s/new-share",
            DownloadCredential = new NetworkCredential("new-share", "new-fixture")
        };
        model.UsePortableDownloadContext(folder);
        Assert.Equal("new-fixture", model.WebdavCredential.Password);
        Assert.Equal("new-fixture", model.GetSavedSharePassword(new Uri(folder.OriginalString), "new-share"));
        model.UsePortableDownloadContext(new CloudFolder());
        Assert.Null(model.WebdavCredential);
        Assert.Null(model.FlareSolverrSession);
    }

    [Fact]
    public async Task SavedCache_DoesNotInheritPreviousShareCredentials()
    {
        string path = Path.Combine(_root, "cache.json");
        await File.WriteAllTextAsync(path, SavedFolderCache.Serialize(new CloudFolder
        { OriginalString = "https://allsync.com/s/other-share" }));
        var model = new MainFormModel { WebdavCredential = new NetworkCredential("old-share", "fixture-password") };
        await model.LoadSavedFolderCacheAsync(path);
        Assert.Null(model.WebdavCredential);
    }

    [Theory]
    [InlineData("/Library/")]
    [InlineData("/library/")]
    public async Task Allsync_PreservesCaseSensitiveFolderIdentity(string requested)
    {
        const string xml = "<d:multistatus xmlns:d='DAV:'><d:response><d:href>/public.php/webdav/Library/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response><d:response><d:href>/public.php/webdav/library/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response><d:response><d:href>/public.php/webdav/Library/a.bin</d:href><d:propstat><d:prop/></d:propstat></d:response><d:response><d:href>/public.php/webdav/library/b.bin</d:href><d:propstat><d:prop/></d:propstat></d:response></d:multistatus>";
        await using var server = new LoopbackDownloadServer((_, _) => new LoopbackResponse
        { StatusCode = (HttpStatusCode)207, ContentType = "application/xml", Body = Encoding.UTF8.GetBytes(xml) });
        var provider = new AllsyncPublicShareProvider(() => new(), _ => { });
        var root = await provider.LoadAsync(new CloudProviderLoadContext(), server.Url("s/share").AbsoluteUri);
        var child = Assert.Single(root.Subfolders.Cast<CloudFolder>(), folder => folder.Path == requested);
        Assert.Single(child.Files);
        Assert.StartsWith(requested, child.Files[0].Path);
        Assert.Equal(2, root.GetFlatFilesList().Count);
    }

    [Fact]
    public async Task DownloadTargets_RejectDirectoryJunctionsBeforeWriting()
    {
        string outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        string link = Path.Combine(_root, "linked");
        var start = new ProcessStartInfo("powershell.exe")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("New-Item -ItemType Junction -Path $env:CFB_AUDIT_LINK_PATH -Target $env:CFB_AUDIT_LINK_TARGET -ErrorAction Stop | Out-Null");
        start.Environment["CFB_AUDIT_LINK_PATH"] = link;
        start.Environment["CFB_AUDIT_LINK_TARGET"] = outside;
        await DownloadPostProcessor.RunProcessAsync(start, "fixture junction", CancellationToken.None);
        try
        {
            Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));
            Assert.Throws<InvalidDataException>(() => Utility.ValidateDownloadTargets(new[] { Path.Combine(link, "file.bin") }));
        }
        finally { Directory.Delete(link); }
    }

    [Theory]
    [InlineData("NUL.bin")]
    [InlineData("stream.bin:secret")]
    [InlineData("trailing.bin.")]
    public void DownloadTargets_RejectUnsafeExplicitWindowsTargets(string name)
    {
        Assert.Throws<InvalidDataException>(() => Utility.ValidateDownloadTargets(new[] { Path.Combine(_root, name) }));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
