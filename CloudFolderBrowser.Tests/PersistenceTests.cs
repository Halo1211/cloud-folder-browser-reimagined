using CloudFolderBrowser.Sync;
using CloudFolderBrowser.Networking;

namespace CloudFolderBrowser.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cfb-persistence-tests-" + Guid.NewGuid().ToString("N"));

    public PersistenceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ChecksumManifest_DetectsSameLengthCorruption()
    {
        string source = Path.Combine(_root, "payload.txt");
        await File.WriteAllTextAsync(source, "hello");
        var store = new ChecksumManifestStore(Path.Combine(_root, "checksums.json"));

        ChecksumManifestEntry entry = await store.RecordFileAsync(source);
        Assert.Equal("2CF24DBA5FB0A30E26E83B2AC5B9E29E1B161E5C1FA7425E73043362938B9824", entry.Sha256);
        Assert.True(await store.VerifyFileAsync(source));

        await File.WriteAllTextAsync(source, "jello");
        Assert.False(await store.VerifyFileAsync(source));
    }

    [Fact]
    public async Task DownloadHistory_ConvertsInterruptedJobToPausedOnReload()
    {
        string stateFile = Path.Combine(_root, "history.json");
        var firstStore = new DownloadHistoryStore(stateFile);
        var entry = new DownloadHistoryEntry
        {
            Id = Guid.NewGuid(),
            FileName = "file.bin",
            SavePath = Path.Combine(_root, "file.bin"),
            Status = DownloadJobStatus.Downloading
        };
        await firstStore.UpsertAsync(entry);

        var reloadedStore = new DownloadHistoryStore(stateFile);
        DownloadHistoryEntry restored = Assert.Single(await reloadedStore.GetAllAsync());
        Assert.Equal(DownloadJobStatus.Paused, restored.Status);
        Assert.Equal(entry.Id, restored.Id);
    }

    [Fact]
    public async Task DownloadHistory_EncryptsSourceUrlAndRestoresItForResume()
    {
        string stateFile = Path.Combine(_root, "secure-history.json");
        const string sourceUrl = "https://mega.nz/file/example#sensitive-decryption-key";
        var store = new DownloadHistoryStore(stateFile);
        await store.UpsertAsync(new DownloadHistoryEntry
        {
            FileName = "archive.bin",
            SourceUrl = sourceUrl,
            SavePath = Path.Combine(_root, "archive.bin"),
            Status = DownloadJobStatus.Paused
        });

        string persisted = await File.ReadAllTextAsync(stateFile);
        Assert.DoesNotContain("sensitive-decryption-key", persisted);
        Assert.Contains("ProtectedSourceUrl", persisted);

        DownloadHistoryEntry restored = Assert.Single(
            await new DownloadHistoryStore(stateFile).GetAllAsync());
        Assert.Equal(sourceUrl, restored.SourceUrl);
    }

    [Fact]
    public async Task DownloadHistory_PreservesRequestedAndEffectiveRoutes()
    {
        string stateFile = Path.Combine(_root, "route-history.json");
        Guid accountId = Guid.NewGuid();
        var store = new DownloadHistoryStore(stateFile);
        await store.UpsertAsync(new DownloadHistoryEntry
        {
            FileName = "archive.bin",
            SourceUrl = "https://files.example/archive.bin",
            SavePath = Path.Combine(_root, "archive.bin"),
            RequestedRouteId = DownloadRouteIds.Automatic,
            EffectiveRouteId = DownloadRouteIds.ForAccount(accountId),
            EffectiveRouteName = "Real-Debrid",
            Status = DownloadJobStatus.Paused
        });

        DownloadHistoryEntry restored = Assert.Single(
            await new DownloadHistoryStore(stateFile).GetAllAsync());
        Assert.Equal(DownloadRouteIds.Automatic, restored.RequestedRouteId);
        Assert.Equal(DownloadRouteIds.ForAccount(accountId), restored.EffectiveRouteId);
        Assert.Equal("Real-Debrid", restored.EffectiveRouteName);
    }

    [Fact]
    public async Task DownloadHistory_MigratesLegacyPlaintextSourceUrl()
    {
        string stateFile = Path.Combine(_root, "legacy-history.json");
        const string sourceUrl = "https://files.example/legacy.bin";
        await File.WriteAllTextAsync(stateFile, $$"""
            [{
              "Id": "{{Guid.NewGuid()}}",
              "CloudService": 6,
              "FileName": "legacy.bin",
              "SourceUrl": "{{sourceUrl}}",
              "SavePath": "legacy.bin",
              "Status": 2
            }]
            """);

        DownloadHistoryEntry restored = Assert.Single(
            await new DownloadHistoryStore(stateFile).GetAllAsync());
        Assert.Equal(sourceUrl, restored.SourceUrl);
    }

    [Fact]
    public async Task FileDownload_HistoryWriteFailureDoesNotAbortTransferState()
    {
        var download = new FileDownload
        {
            HistoryStore = new DownloadHistoryStore(_root),
            HistoryEntry = new DownloadHistoryEntry
            {
                FileName = "payload.bin",
                SavePath = Path.Combine(_root, "payload.bin")
            },
            SavePath = Path.Combine(_root, "payload.bin")
        };

        await download.UpdateHistoryAsync(DownloadJobStatus.Downloading);

        Assert.True(Directory.Exists(_root));
        Assert.Equal(DownloadJobStatus.Downloading, download.HistoryEntry.Status);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }
}
