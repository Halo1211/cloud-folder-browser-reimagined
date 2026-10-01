using CloudFolderBrowser.Networking;

namespace CloudFolderBrowser.Tests;

public sealed class FifthPassAuditTests
{
    [Fact]
    public void NodeCopy_PreservesSizePathAndSelectionMetadata()
    {
        var source = new ColumnNode("file", DateTime.Today, DateTime.Today, 123)
        { Path = "/file", CheckBoxEnabled = false, IsChecked = true };
        var copy = new ColumnNode(source);
        Assert.Equal(source.Size, copy.Size);
        Assert.Equal(source.Path, copy.Path);
        Assert.Equal(source.CheckBoxEnabled, copy.CheckBoxEnabled);
        Assert.Equal(source.CheckState, copy.CheckState);
    }

    [Theory]
    [InlineData(SortOrder.Ascending, -1)]
    [InlineData(SortOrder.Descending, 1)]
    public void SizeSort_UsesBytesInsteadOfRoundedDisplay(SortOrder order, int sign)
    {
        var first = new ColumnNode("one", DateTime.Today, DateTime.Today, 1);
        var second = new ColumnNode("two", DateTime.Today, DateTime.Today, 2);
        Assert.Equal(sign, Math.Sign(new FolderItemSorter("Size", order).Compare(first, second)));
    }

    [Theory]
    [InlineData("Created")]
    [InlineData("Modified")]
    public void DateSort_PreservesTimeOfDay(string mode)
    {
        DateTime morning = DateTime.Today.AddHours(9);
        var first = new ColumnNode("one", morning, morning, 1);
        var second = new ColumnNode("two", morning.AddHours(1), morning.AddHours(1), 1);
        Assert.True(new FolderItemSorter(mode, SortOrder.Ascending).Compare(first, second) < 0);
    }

    [Fact]
    public void LocalFolderSort_PutsFoldersBeforeFiles()
    {
        var folder = new ColumnNode("z-folder", DateTime.Today, DateTime.Today, 1) { Tag = new LocalFolder() };
        var file = new ColumnNode("a-file", DateTime.Today, DateTime.Today, 1) { Tag = new FileInfo("a-file") };
        Assert.True(new FolderItemSorter("Name", SortOrder.Ascending).Compare(folder, file) < 0);
    }

    [Fact]
    public void CookieSession_IgnoresNullEntries()
    {
        var session = new CloudflareSession("agent", new FlareSolverrCookie[] { null!, Cookie("good", "value", "example.com") });
        Assert.Equal("good=value", session.GetCookieHeader(new Uri("https://example.com/file")));
    }

    [Fact]
    public void CookieSession_DoesNotTreatMissingDomainAsEveryHost()
    {
        var session = new CloudflareSession("agent", new[] { Cookie("token", "fixture", "") });
        Assert.Empty(session.GetCookieHeader(new Uri("https://unrelated.example/file")));
    }

    [Theory]
    [InlineData("bad\r\nname", "value")]
    [InlineData("bad;name", "value")]
    [InlineData("token", "value\r\nX-Test: injected")]
    [InlineData("token", "value; other=injected")]
    public void CookieSession_IgnoresInvalidHeaderComponents(string name, string value)
    {
        var session = new CloudflareSession("agent", new[] { Cookie(name, value, "example.com") });
        Assert.Empty(session.GetCookieHeader(new Uri("https://example.com/file")));
    }

    [Theory]
    [InlineData("https://example.com/a%23b.bin?token=x%2By&mode=a+b")]
    [InlineData("https://example.com/a%3Fb.bin?token=x%26y")]
    public void JdExport_PreservesEncodedUrl(string url)
    {
        var file = new CloudFile("file.bin", DateTime.MinValue, DateTime.MinValue, 1) { PublicUrl = new Uri(url) };
        Assert.Equal(file.PublicUrl.AbsoluteUri, SyncFilesForm.CreateJdExportLink(file).downloadLink.url);
    }

    [Fact]
    public void JdExport_UnknownSizeIsNotReportedAsEmpty()
    {
        var file = new CloudFile("file.bin", DateTime.MinValue, DateTime.MinValue, 0)
        { PublicUrl = new Uri("https://example.com/file"), HasKnownSize = false };
        Assert.Equal(-1, SyncFilesForm.CreateJdExportLink(file).downloadLink.size);
    }

    [Theory]
    [InlineData(CloudServiceType.Allsync, "https://one.example/s/A", "https://one.example/s/B")]
    [InlineData(CloudServiceType.Allsync, "https://one.example/s/A", "https://two.example/s/A")]
    [InlineData(CloudServiceType.QCloud, "https://one.example/s/A", "https://one.example/s/B")]
    public void HistoryRetry_DoesNotShareCredentialsAcrossShareBoundaries(CloudServiceType service, string first, string second)
    {
        var entries = new[] { new DownloadHistoryEntry { CloudService = service, SourceUrl = first },
            new DownloadHistoryEntry { CloudService = service, SourceUrl = second } };
        Assert.Equal(2, MainForm.GroupRetryEntries(entries).Count());
    }

    [Theory]
    [InlineData(CloudServiceType.Allsync)]
    [InlineData(CloudServiceType.QCloud)]
    public void HistoryRetry_StillGroupsFilesFromTheSameShare(CloudServiceType service)
    {
        var entries = new[] { new DownloadHistoryEntry { CloudService = service, SourceUrl = "https://ONE.example/s/Key?path=/a" },
            new DownloadHistoryEntry { CloudService = service, SourceUrl = "https://one.example/s/Key?path=/b" } };
        Assert.Equal(2, Assert.Single(MainForm.GroupRetryEntries(entries)).Count());
    }

    [Fact]
    public void CookieSession_PreservesValidEmptyValueWithDomainPathAndSecureRules()
    {
        var cookie = Cookie("session", "", ".example.com");
        cookie.Path = "/folder";
        cookie.Secure = true;
        var session = new CloudflareSession("agent", new[] { cookie });
        Assert.Equal("session=", session.GetCookieHeader(new Uri("https://sub.example.com/folder/file")));
        Assert.Empty(session.GetCookieHeader(new Uri("http://sub.example.com/folder/file")));
        Assert.Empty(session.GetCookieHeader(new Uri("https://sub.example.com/folder-sibling/file")));
    }

    private static FlareSolverrCookie Cookie(string name, string value, string domain) => new()
    { Name = name, Value = value, Domain = domain, Path = "/" };
}
