using CloudFolderBrowser.Networking;
using Newtonsoft.Json.Linq;

namespace CloudFolderBrowser.Tests;

public sealed class FourthPassAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cfb-fourth-audit-" + Guid.NewGuid().ToString("N"));

    public FourthPassAuditTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void DefaultCloudFolder_CanBeEnumeratedAndPopulated()
    {
        var folder = new CloudFolder();
        Assert.Empty(folder.GetFlatFilesList());
        folder.AddFile(new CloudFile("file.bin", DateTime.MinValue, DateTime.MinValue, 1));
        folder.CalculateFolderSize();
        Assert.Equal(1, folder.FilesNumber);
    }

    [Theory]
    [InlineData("/a%20b.bin", "/a b.bin")]
    [InlineData("/folder%2Ffile.bin", "/folder/file.bin")]
    [InlineData("/name.bin", "/name.bin ")]
    public async Task SyncPlan_PreservesRemotePathsBeforeLocalCollisionReview(string first, string second)
    {
        var folder = new CloudFolder("root", DateTime.MinValue, DateTime.MinValue, 0) { Path = "/" };
        folder.AddFile(new CloudFile("first", DateTime.MinValue, DateTime.MinValue, 1) { Path = first });
        folder.AddFile(new CloudFile("second", DateTime.MinValue, DateTime.MinValue, 2) { Path = second });
        var plan = await new MainFormModel().BuildSyncPlanAsync(new() { folder, folder }, new(), _root, true, false);
        Assert.Equal(2, plan.Count);
    }

    [Theory]
    [InlineData("single")]
    [InlineData("tree")]
    [InlineData("flat")]
    public async Task FogLink_PreservesFileModificationTimestamp(string format)
    {
        DateTime created = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime modified = created.AddYears(5);
        var file = new FogLinkFile { Id = "file", ParentId = "root", Name = "file.bin", IsFile = true,
            Size = 1, Created = created, Modified = modified };
        var root = new FogLinkFile { Id = "root", Name = "root", Children = new[] { file } };
        var model = new MainFormModel();
        if (format == "flat")
            await model.LoadMega2(new() { root, file }, "foglink-fixture");
        else
            await model.LoadMega(new() { format == "single" ? file : root }, "foglink-fixture");
        Assert.Equal(modified, Assert.Single(model.CloudPublicFolder.GetFlatFilesList()).Modified);
        Assert.Equal(created, Assert.Single(model.CloudPublicFolder.GetFlatFilesList()).Created);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FogLink_MissingSizeRemainsUnknown(bool flat)
    {
        var file = new FogLinkFile { Id = "file", ParentId = "root", Name = "file.bin", IsFile = true };
        var root = new FogLinkFile { Id = "root", Name = "root", Children = new[] { file } };
        var model = new MainFormModel();
        if (flat) await model.LoadMega2(new() { root, file }, "fixture");
        else await model.LoadMega(new() { root }, "fixture");
        Assert.False(Assert.Single(model.CloudPublicFolder.GetFlatFilesList()).HasKnownSize);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FlatFogLink_LoadsChildrenRegardlessOfResponseOrder(bool rootLast)
    {
        var root = new FogLinkFile { Id = "root", Name = "root" };
        var child = new FogLinkFile { Id = "child", ParentId = "root", Name = "child" };
        var nested = new FogLinkFile { Id = "nested", ParentId = "child", Name = "nested" };
        var file = new FogLinkFile { Id = "file", ParentId = "nested", Name = "file.bin", IsFile = true, Size = 7 };
        var nodes = rootLast ? new List<FogLinkFile> { file, nested, child, root } : new() { root, file, nested, child };
        var model = new MainFormModel();
        await model.LoadMega2(nodes, "fixture");
        Assert.Equal("/child/nested/file.bin", Assert.Single(model.CloudPublicFolder.GetFlatFilesList()).Path);
        Assert.Equal(7, model.CloudPublicFolder.Size);
        Assert.Equal(4 - 1, model.AllFolders.Count);
    }

    [Theory]
    [InlineData("dns-provider")]
    [InlineData("dns-transport")]
    [InlineData("proxy")]
    public void NetworkConfiguration_RejectsUndefinedModes(string mode)
    {
        NetworkConfiguration configuration = mode switch
        {
            "dns-provider" => new() { DnsProvider = (DnsProviderMode)999 },
            "dns-transport" => new() { DnsTransport = (DnsTransport)999 },
            _ => new() { ProxyMode = (ProxyMode)999 }
        };
        Assert.Throws<ArgumentOutOfRangeException>(configuration.Validate);
    }

    [Fact]
    public async Task History_PersistsUnknownSizeAndResolverRequirement()
    {
        string path = Path.Combine(_root, "history.json");
        var entry = new DownloadHistoryEntry { FileName = "file.bin", SourceUrl = "https://example.com/share",
            ExpectedSize = 0, HasKnownSize = false, RequiresLinkResolver = true,
            SavePath = Path.Combine(_root, "file.bin"), Status = DownloadJobStatus.Paused };
        await new DownloadHistoryStore(path).UpsertAsync(entry);
        JObject persisted = (JObject)JArray.Parse(await File.ReadAllTextAsync(path)).Single();
        Assert.NotNull(persisted["HasKnownSize"]);
        Assert.NotNull(persisted["RequiresLinkResolver"]);
        DownloadHistoryEntry restored = Assert.Single(await new DownloadHistoryStore(path).GetAllAsync());
        CloudFile retry = Assert.IsType<CloudFile>(restored.ToRetryFile());
        Assert.False(retry.HasKnownSize);
        Assert.True(retry.RequiresLinkResolver);
        Assert.Equal(entry.Id, retry.DownloadHistoryId);
        Assert.Equal(entry.SavePath, retry.LocalSavePathOverride);
    }

    [Theory]
    [InlineData(0, DownloadJobStatus.Paused, false)]
    [InlineData(0, DownloadJobStatus.Completed, true)]
    [InlineData(8, DownloadJobStatus.Paused, true)]
    public async Task History_MigratesLegacySizeMetadata(long size, DownloadJobStatus status, bool known)
    {
        string path = Path.Combine(_root, "legacy.json");
        await File.WriteAllTextAsync(path, $$"""
            [{"ExpectedSize":{{size}},"Status":{{(int)status}},"CloudService":9,"SourceUrl":"https://example.com/share"}]
            """);
        var entry = Assert.Single(await new DownloadHistoryStore(path).GetAllAsync());
        Assert.Equal(known, entry.HasKnownSize);
        Assert.True(entry.RequiresLinkResolver);
    }

    [Fact]
    public async Task History_PreservesKnownEmptyFiles()
    {
        string path = Path.Combine(_root, "empty.json");
        await new DownloadHistoryStore(path).UpsertAsync(new DownloadHistoryEntry
        { ExpectedSize = 0, HasKnownSize = true, Status = DownloadJobStatus.Paused,
            SourceUrl = "https://example.com/empty", FileName = "empty",
            SavePath = Path.Combine(_root, "empty") });
        var restored = Assert.Single(await new DownloadHistoryStore(path).GetAllAsync());
        Assert.True(Assert.IsType<CloudFile>(restored.ToRetryFile()).HasKnownSize);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("cycle")]
    [InlineData("orphan")]
    public async Task FlatFogLink_RejectsInvalidTreeWithoutReplacingLoadedFolder(string kind)
    {
        var root = new FogLinkFile { Id = "root", Name = "root" };
        var first = new FogLinkFile { Id = "first", ParentId = "root", Name = "first", IsFile = true };
        var second = new FogLinkFile { Id = "second", ParentId = "first", Name = "second", IsFile = true };
        if (kind == "duplicate") second.Id = first.Id;
        if (kind == "orphan") second.ParentId = "missing";
        if (kind == "cycle") { first.ParentId = second.Id; second.ParentId = first.Id; }
        var model = new MainFormModel();
        CloudFolder original = model.CloudPublicFolder;
        await Assert.ThrowsAsync<InvalidDataException>(() => model.LoadMega2(new() { root, first, second }, "fixture"));
        Assert.Same(original, model.CloudPublicFolder);
    }

    [Fact]
    public async Task TreeFogLink_DoesNotDoubleCountReportedFolderSizes()
    {
        var file = new FogLinkFile { Name = "file.bin", IsFile = true, Size = 7 };
        var child = new FogLinkFile { Name = "child", Size = 7, Children = new[] { file } };
        var model = new MainFormModel();
        await model.LoadMega(new() { new FogLinkFile { Name = "root", Children = new[] { child } } }, "fixture");
        Assert.Equal(7, model.CloudPublicFolder.Size);
    }

    [Theory]
    [InlineData("folder.")]
    [InlineData("folder ")]
    public async Task TreeFogLink_PreservesRemoteFolderPath(string folderName)
    {
        var file = new FogLinkFile { Name = "file.bin", IsFile = true, Size = 1 };
        var child = new FogLinkFile { Name = folderName, Children = new[] { file } };
        var model = new MainFormModel();
        await model.LoadMega(new() { new FogLinkFile { Name = "root", Children = new[] { child } } }, "fixture");
        Assert.Equal("/" + folderName + "/file.bin", Assert.Single(model.CloudPublicFolder.GetFlatFilesList()).Path);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
