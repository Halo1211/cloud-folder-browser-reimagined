using CloudFolderBrowser.Sync;

namespace CloudFolderBrowser.Tests;

public sealed class SyncPlanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cfb-plan-tests-" + Guid.NewGuid().ToString("N"));

    public SyncPlanTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task BuildSyncPlan_DistinguishesMissingMatchingAndDifferentSize()
    {
        var folder = new CloudFolder("root", DateTime.MinValue, DateTime.MinValue, 0) { Path = "/" };
        var file = new CloudFile("sample.bin", DateTime.MinValue, DateTime.MinValue, 5) { Path = "/sample.bin" };
        folder.AddFile(file);
        var model = new MainFormModel();

        SyncPlanItem missing = Assert.Single(await model.BuildSyncPlanAsync(
            new List<CloudFolder> { folder }, new List<CloudFolder>(), _root, true, false));
        Assert.Equal(SyncDifference.Missing, missing.Difference);
        Assert.Equal(SyncPlanAction.Download, missing.Action);

        await File.WriteAllBytesAsync(Path.Combine(_root, "sample.bin"), new byte[5]);
        Assert.Empty(await model.BuildSyncPlanAsync(
            new List<CloudFolder> { folder }, new List<CloudFolder>(), _root, true, false));

        SyncPlanItem matching = Assert.Single(await model.BuildSyncPlanAsync(
            new List<CloudFolder> { folder }, new List<CloudFolder>(), _root, false, false));
        Assert.Equal(SyncDifference.UpToDate, matching.Difference);
        Assert.Equal(SyncPlanAction.Overwrite, matching.Action);

        file.Modified = DateTime.Now.AddHours(1);
        SyncPlanItem newerCloud = Assert.Single(await model.BuildSyncPlanAsync(
            new List<CloudFolder> { folder }, new List<CloudFolder>(), _root, true, false));
        Assert.Equal(SyncDifference.ModifiedMismatch, newerCloud.Difference);
        Assert.Equal(SyncPlanAction.Overwrite, newerCloud.Action);

        file.Modified = DateTime.MinValue;
        await File.WriteAllBytesAsync(Path.Combine(_root, "sample.bin"), new byte[3]);
        SyncPlanItem mismatch = Assert.Single(await model.BuildSyncPlanAsync(
            new List<CloudFolder> { folder }, new List<CloudFolder>(), _root, true, false));
        Assert.Equal(SyncDifference.SizeMismatch, mismatch.Difference);
        Assert.Equal(SyncPlanAction.Overwrite, mismatch.Action);
    }

    [Fact]
    public async Task BuildSyncPlan_EmptyFilesWithMatchingSizeAreUpToDate()
    {
        var folder = new CloudFolder("root", DateTime.MinValue, DateTime.MinValue, 0) { Path = "/" };
        var file = new CloudFile("empty.bin", DateTime.MinValue, DateTime.MinValue, 0) { Path = "/empty.bin" };
        folder.AddFile(file);
        await File.WriteAllBytesAsync(Path.Combine(_root, "empty.bin"), Array.Empty<byte>());

        var model = new MainFormModel();
        Assert.Empty(await model.BuildSyncPlanAsync(
            new List<CloudFolder> { folder }, new List<CloudFolder>(), _root, true, false));

        SyncPlanItem visibleMatch = Assert.Single(await model.BuildSyncPlanAsync(
            new List<CloudFolder> { folder }, new List<CloudFolder>(), _root, false, false));
        Assert.Equal(SyncDifference.UpToDate, visibleMatch.Difference);
    }

    [Fact]
    public async Task BuildSyncPlan_UnknownRemoteSizeDoesNotForceFalseMismatch()
    {
        var folder = new CloudFolder("root", DateTime.MinValue, DateTime.MinValue, 0) { Path = "/" };
        var file = new CloudFile("share.zip", DateTime.MinValue, DateTime.MinValue, 0)
        {
            Path = "/share.zip",
            HasKnownSize = false
        };
        folder.AddFile(file);
        await File.WriteAllBytesAsync(Path.Combine(_root, "share.zip"), new byte[17]);

        var model = new MainFormModel();
        Assert.Empty(await model.BuildSyncPlanAsync(
            new List<CloudFolder> { folder }, new List<CloudFolder>(), _root, true, false));
    }

    [Fact]
    public void SyncPlanItem_UsesSanitizedRenameForCollisionChecksAndDownload()
    {
        var file = new CloudFile("sample.bin", DateTime.MinValue, DateTime.MinValue, 5)
        {
            Path = "/folder/sample.bin"
        };
        var item = new SyncPlanItem(
            file,
            SyncDifference.SizeMismatch,
            SyncPlanAction.Rename,
            "Different size",
            Path.Combine(_root, "folder", "sample.bin"))
        {
            TargetName = "report?.bin"
        };

        Assert.Equal("/folder/report_.bin", item.GetTargetCloudPath());
        Assert.Equal("report_.bin", item.Apply().LocalNameOverride);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }
}
