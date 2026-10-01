using CloudFolderBrowser.Networking;
using CloudFolderBrowser.Sync;
using CloudFolderBrowser.Accounts;
using CG.Web.MegaApiClient;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CloudFolderBrowser.Tests;

[Collection("Real download integration")]
public sealed class SeventhPassAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cfb-seventh-audit-" + Guid.NewGuid().ToString("N"));
    public SeventhPassAuditTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task History_FailedInitialWriteDoesNotPublishEntriesInMemory()
    {
        string target = Directory.CreateDirectory(Path.Combine(_root, "history.json")).FullName;
        var store = new DownloadHistoryStore(target);
        var entry = new DownloadHistoryEntry { FileName = "fixture.bin", UpdatedUtc = DateTime.UnixEpoch };
        await AssertWriteFailureAsync(() => store.UpsertManyAsync(new[] { entry }));
        Assert.Empty(await store.GetAllAsync());
        Assert.Equal(DateTime.UnixEpoch, entry.UpdatedUtc);
    }

    [Fact]
    public async Task History_FailedUpdateKeepsPreviouslyPersistedEntry()
    {
        string path = Path.Combine(_root, "history.json");
        var store = new DownloadHistoryStore(path);
        var entry = new DownloadHistoryEntry { FileName = "before.bin", Status = DownloadJobStatus.Paused };
        await store.UpsertAsync(entry);
        Directory.CreateDirectory(path + ".tmp");
        entry.FileName = "after.bin";
        await AssertWriteFailureAsync(() => store.UpsertAsync(entry));
        Assert.Equal("before.bin", Assert.Single(await store.GetAllAsync()).FileName);
        Assert.Equal("before.bin", Assert.Single(await new DownloadHistoryStore(path).GetAllAsync()).FileName);
    }

    [Fact]
    public async Task History_FailedClearCompletedKeepsPreviousView()
    {
        string path = Path.Combine(_root, "history.json");
        var store = new DownloadHistoryStore(path);
        await store.UpsertAsync(new DownloadHistoryEntry { FileName = "complete.bin", Status = DownloadJobStatus.Completed });
        Directory.CreateDirectory(path + ".tmp");
        await AssertWriteFailureAsync(() => store.RemoveCompletedAsync());
        Assert.Equal("complete.bin", Assert.Single(await store.GetAllAsync()).FileName);
    }

    [Fact]
    public async Task Checksum_FailedInitialWriteDoesNotPublishAnUnstoredBaseline()
    {
        string file = Path.Combine(_root, "payload.bin");
        await File.WriteAllBytesAsync(file, new byte[] { 1, 2, 3 });
        string target = Directory.CreateDirectory(Path.Combine(_root, "checksums.json")).FullName;
        var store = new ChecksumManifestStore(target);
        await AssertWriteFailureAsync(() => store.RecordFileAsync(file));
        Assert.Null(await store.FindAsync(file));
        Assert.Null(await store.VerifyFileAsync(file));
    }

    [Fact]
    public async Task Checksum_FailedReplacementKeepsPreviousBaseline()
    {
        string file = Path.Combine(_root, "payload.bin");
        string manifest = Path.Combine(_root, "checksums.json");
        await File.WriteAllBytesAsync(file, new byte[] { 1, 2, 3 });
        var store = new ChecksumManifestStore(manifest);
        string oldHash = (await store.RecordFileAsync(file)).Sha256;
        await File.WriteAllBytesAsync(file, new byte[] { 4, 5, 6 });
        Directory.CreateDirectory(manifest + ".tmp");
        await AssertWriteFailureAsync(() => store.RecordFileAsync(file));
        Assert.Equal(oldHash, (await store.FindAsync(file))!.Sha256);
        Assert.False(await store.VerifyFileAsync(file));
    }

    [Fact]
    public void SavedFolderCache_RejectsInjectedLocalFolderSubtype()
    {
        var root = new CloudFolder("root", DateTime.MinValue, DateTime.MinValue, 0) { Path = "/" };
        root.AddSubfolder(new CloudFolder("child", DateTime.MinValue, DateTime.MinValue, 0) { Path = "/child/" });
        var json = JObject.Parse(SavedFolderCache.Serialize(root));
        ((JObject)json["Subfolders"]![0]!)["$type"] = $"{typeof(LocalFolder).FullName}, {typeof(LocalFolder).Assembly.GetName().Name}";
        Assert.ThrowsAny<JsonException>(() => SavedFolderCache.Deserialize(json.ToString()));
    }

    [Fact]
    public void SavedFolderCache_RoundTripsExistingNestedTree()
    {
        var root = new CloudFolder("root", DateTime.MinValue, DateTime.MinValue, 0) { Path = "/" };
        var child = new CloudFolder("child", DateTime.MinValue, DateTime.MinValue, 0) { Path = "/child/" };
        child.AddFile(new CloudFile("file.bin", DateTime.MinValue, DateTime.MinValue, 7) { Path = "/child/file.bin" });
        root.AddSubfolder(child);
        string json = SavedFolderCache.Serialize(root);
        CloudFolder loaded = Assert.IsType<CloudFolder>(SavedFolderCache.Deserialize(json));
        Assert.Equal("file.bin", Assert.Single(Assert.IsType<CloudFolder>(Assert.Single(loaded.Subfolders)).Files).Name);
    }

    [Theory]
    [InlineData("Subfolders")]
    [InlineData("Files")]
    public void SavedFolderCache_RejectsMissingCollections(string field)
    {
        var root = new CloudFolder("root", DateTime.MinValue, DateTime.MinValue, 0) { Path = "/" };
        var json = JObject.Parse(SavedFolderCache.Serialize(root));
        json[field] = JValue.CreateNull();
        Assert.ThrowsAny<JsonException>(() => SavedFolderCache.Deserialize(json.ToString()));
    }

    [Fact]
    public void SavedFolderCache_RejectsNullChild()
    {
        var root = new CloudFolder("root", DateTime.MinValue, DateTime.MinValue, 0) { Path = "/" };
        var json = JObject.Parse(SavedFolderCache.Serialize(root));
        json["Subfolders"] = new JArray(JValue.CreateNull());
        Assert.ThrowsAny<JsonException>(() => SavedFolderCache.Deserialize(json.ToString()));
    }

    [Fact]
    public void SavedFolderCache_FailedWritePreservesPreviousFile()
    {
        string previousDirectory = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_root);
            var root = new CloudFolder("before", DateTime.MinValue, DateTime.MinValue, 0)
            {
                Path = "/", OriginalString = "https://example.invalid/fixture"
            };
            root.SaveToJson();
            string cachePath = Path.Combine(_root, "jsons", Utility.GetHashString(root.OriginalString) + ".json");
            string original = File.ReadAllText(cachePath);
            Directory.CreateDirectory(cachePath + ".tmp");
            root.Name = "after";
            Exception? failure = Record.Exception(root.SaveToJson);
            Assert.True(failure is IOException or UnauthorizedAccessException);
            Assert.Equal(original, File.ReadAllText(cachePath));
        }
        finally { Directory.SetCurrentDirectory(previousDirectory); }
    }

    [Fact]
    public async Task SavedFolderLoad_InvalidCacheKeepsCurrentModel()
    {
        string path = Path.Combine(_root, "invalid-cache.json");
        await File.WriteAllTextAsync(path, "{invalid json");
        var previousRoot = new CloudFolder("previous", DateTime.MinValue, DateTime.MinValue, 0) { Path = "/" };
        var previousFolders = new List<CloudFolder> { previousRoot };
        var model = new MainFormModel { CloudPublicFolder = previousRoot, AllFolders = previousFolders, LoadedFromFile = false };
        await Assert.ThrowsAnyAsync<JsonException>(() => model.LoadSavedFolderCacheAsync(path));
        Assert.Same(previousRoot, model.CloudPublicFolder);
        Assert.Same(previousFolders, model.AllFolders);
        Assert.False(model.LoadedFromFile);
    }

    [Fact]
    public async Task SavedFolderLoad_ValidCacheReplacesTree()
    {
        string path = Path.Combine(_root, "valid-cache.json");
        var root = new CloudFolder("new", DateTime.MinValue, DateTime.MinValue, 0) { Path = "/" };
        root.AddSubfolder(new CloudFolder("child", DateTime.MinValue, DateTime.MinValue, 0) { Path = "/child/" });
        await File.WriteAllTextAsync(path, SavedFolderCache.Serialize(root));
        var model = new MainFormModel();
        await model.LoadSavedFolderCacheAsync(path);
        Assert.Equal("new", model.CloudPublicFolder.Name);
        Assert.Equal(new[] { "new", "child" }, model.AllFolders.Select(folder => folder.Name));
        Assert.True(model.LoadedFromFile);
    }

    [Fact]
    public void AccountStore_FailedWriteDoesNotChangeCallerProfile()
    {
        string target = Directory.CreateDirectory(Path.Combine(_root, "accounts.json")).FullName;
        var store = new CloudAccountStore(target);
        var profile = new CloudAccountProfile
        {
            Provider = CloudAccountProvider.Mega, DisplayName = "fixture", Secret = "fixture-only",
            IsActive = false, UpdatedUtc = DateTime.UnixEpoch
        };
        Exception? failure = Record.Exception(() => store.Upsert(profile, makeActive: true));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.False(profile.IsActive);
        Assert.Equal(DateTime.UnixEpoch, profile.UpdatedUtc);
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void LocalScan_SkipsAFileRemovedAfterEnumeration()
    {
        var folder = new LocalFolder();
        Assert.False(folder.TryAddScannedFile(new FileInfo(Path.Combine(_root, "removed.bin"))));
        Assert.Empty(folder.Files);
        Assert.Equal(0, folder.SizeTopDirectoryOnly);
    }

    [Fact]
    public async Task LocalScan_CountsAvailableFileOnlyOnce()
    {
        string path = Path.Combine(_root, "present.bin");
        await File.WriteAllBytesAsync(path, new byte[7]);
        var folder = new LocalFolder();
        Assert.True(folder.TryAddScannedFile(new FileInfo(path)));
        Assert.Equal(7, folder.SizeTopDirectoryOnly);
        Assert.Equal(path, Assert.Single(folder.Files).FullName);
    }

    [Fact]
    public void MegaSession_StoredTypeMetadataCannotInstantiateASubclass()
    {
        FixtureMegaToken.Constructed = 0;
        var json = JObject.FromObject(new MegaApiClient.LogonSessionToken("fixture-session", new byte[16]));
        json.AddFirst(new JProperty("$type", $"{typeof(FixtureMegaToken).FullName}, {typeof(FixtureMegaToken).Assembly.GetName().Name}"));
        MegaApiClient.LogonSessionToken token = Assert.IsType<MegaApiClient.LogonSessionToken>(
            MegaSessionTokenCodec.Deserialize(json.ToString()));
        Assert.Equal("fixture-session", token.SessionId);
        Assert.Equal(0, FixtureMegaToken.Constructed);
    }

    [Fact]
    public void MegaSession_LegacyTokenRemainsReadable()
    {
        var original = new MegaApiClient.LogonSessionToken("fixture-session", new byte[16]);
        string json = JsonConvert.SerializeObject(original, new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto });
        var loaded = Assert.IsType<MegaApiClient.LogonSessionToken>(MegaSessionTokenCodec.Deserialize(json));
        Assert.Equal(original.SessionId, loaded.SessionId);
        Assert.Equal(original.MasterKey, loaded.MasterKey);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"SessionId\":\"fixture\"}")]
    [InlineData("{\"SessionId\":\"\",\"MasterKey\":\"AAAAAAAAAAAAAAAAAAAAAA==\"}")]
    [InlineData("{\"SessionId\":\"fixture\",\"MasterKey\":\"AA==\"}")]
    public void MegaSession_IncompleteTokenIsRejectedBeforeLogin(string json)
    {
        Assert.ThrowsAny<JsonException>(() => MegaSessionTokenCodec.Deserialize(json));
    }

    [Theory]
    [InlineData("%2F")]
    [InlineData("%252F")]
    [InlineData("report?.bin")]
    [InlineData("..")]
    public void RenamePreviewAndDownloadResolveTheSameTarget(string requested)
    {
        var file = new CloudFile("original.bin", DateTime.MinValue, DateTime.MinValue, 1) { Path = "/child/original.bin" };
        var item = new SyncPlanItem(file, SyncDifference.SizeMismatch, SyncPlanAction.Rename, "fixture",
            Path.Combine(_root, "child", "original.bin")) { TargetName = requested };
        Assert.Equal(item.GetTargetLocalPath(), Utility.GetSafeDownloadPath(_root, item.GetTargetCloudPath()));
    }

    private static async Task AssertWriteFailureAsync(Func<Task> operation)
    {
        Exception error = await Assert.ThrowsAnyAsync<Exception>(operation);
        Assert.True(error is IOException or UnauthorizedAccessException, $"Unexpected write failure: {error.GetType().Name}");
    }

    public sealed class FixtureMegaToken : MegaApiClient.LogonSessionToken
    {
        public static int Constructed;
        public FixtureMegaToken() : base("fixture-session", new byte[16]) => Interlocked.Increment(ref Constructed);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
