using CloudFolderBrowser.Networking;
using CloudFolderBrowser.Sync;
using CloudFolderBrowser.Theming;
using CloudFolderBrowser.FormsSecondary;
using Aga.Controls.Tree;
using Aga.Controls.Tree.NodeControls;
using YandexDiskSharp.Models;

namespace CloudFolderBrowser.Tests;

public sealed class BugRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "cfb-regression-tests-" + Guid.NewGuid().ToString("N"));

    public BugRegressionTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("https://mega.nz/file/fileId#fileKey", "node", "https://mega.nz/file/fileId#fileKey")]
    [InlineData("https://mega.nz/folder/rootId#folderKey", "childId", "https://mega.nz/folder/rootId#folderKey/file/childId")]
    public void MegaFileLink_IsCanonicalForSingleFilesAndFolderChildren(
        string share,
        string nodeId,
        string expected)
    {
        Assert.Equal(expected, MainFormModel.BuildMegaFileLink(share, nodeId).AbsoluteUri);
    }

    [Fact]
    public void ThemeApply_PreservesExplicitTypography()
    {
        using var form = new Form();
        using var heading = new Label
        {
            Font = new Font("Segoe UI Semibold", 17F, FontStyle.Bold)
        };
        form.Controls.Add(heading);

        ThemeManager.Apply(form);

        Assert.Equal(17F, heading.Font.Size);
        Assert.True(heading.Font.Bold);
    }

    [Fact]
    public void TreeViewAdv_RendersNodesUsingItsForegroundColor()
    {
        using var tree = new TreeViewAdv
        {
            Size = new Size(260, 80),
            BackColor = Color.FromArgb(23, 25, 31),
            ForeColor = Color.Fuchsia,
            UseColumns = false
        };
        tree.NodeControls.Add(new NodeTextBox { DataPropertyName = nameof(Node.Text) });
        var model = new TreeModel();
        model.Nodes.Add(new Node("Theme foreground probe"));
        tree.Model = model;

        using var bitmap = new Bitmap(tree.Width, tree.Height);
        tree.DrawToBitmap(bitmap, tree.ClientRectangle);

        bool containsForegroundPixel = Enumerable.Range(0, bitmap.Width)
            .SelectMany(x => Enumerable.Range(0, bitmap.Height).Select(y => bitmap.GetPixel(x, y)))
            .Any(pixel => pixel.R > 150 && pixel.B > 150 && pixel.G < 100);
        Assert.True(containsForegroundPixel);
    }

    [Fact]
    public void ThemedProgressBar_RendersSmallValuesProportionally()
    {
        ThemeManager.SetMode(AppThemeMode.Dark);
        using var threePercent = RenderProgressBar(3);
        using var sixPercent = RenderProgressBar(6);
        int threePercentWidth = CountAccentPixels(threePercent);
        int sixPercentWidth = CountAccentPixels(sixPercent);

        Assert.Equal(6, threePercentWidth);
        Assert.Equal(12, sixPercentWidth);
    }

    [Fact]
    public void QloudProvider_DoesNotMatchPartialHostLabel()
    {
        var provider = CloudFolderBrowser.Providers.CloudProviderRegistry.Default.Resolve(
            "https://notqloud.example/s/key");
        Assert.Equal("generic-http", provider?.Id);
        Assert.Equal(CloudServiceType.Other, provider?.ServiceType);
    }

    [Fact]
    public void DownloadLogUri_RedactsPathQueryAndFragment()
    {
        string safe = CommonFileDownload.GetSafeUriForLog(
            new Uri("https://cdn.example.com/private-token/file.bin?auth=secret#fragment"));

        Assert.Equal("https://cdn.example.com/…", safe);
        Assert.DoesNotContain("secret", safe);
        Assert.DoesNotContain("private-token", safe);
    }

    [Fact]
    public void CookiePath_DoesNotMatchSiblingPrefix()
    {
        var session = new CloudflareSession("agent", new[]
        {
            new FlareSolverrCookie { Name = "session", Value = "value", Domain = "example.com", Path = "/foo" }
        });

        Assert.Equal("session=value", session.GetCookieHeader(new Uri("https://example.com/foo/bar")));
        Assert.Equal(string.Empty, session.GetCookieHeader(new Uri("https://example.com/foobar")));
    }

    [Fact]
    public async Task DownloadHistory_PreservesCorruptFileAndSupportsBatchUpsert()
    {
        string stateFile = Path.Combine(_root, "history.json");
        await File.WriteAllTextAsync(stateFile, "{not-json");
        var store = new DownloadHistoryStore(stateFile);

        Assert.Empty(await store.GetAllAsync());
        Assert.Single(Directory.GetFiles(_root, "history.json.corrupt-*"));

        await store.UpsertManyAsync(new[]
        {
            new DownloadHistoryEntry { FileName = "one.bin", SavePath = Path.Combine(_root, "one.bin") },
            new DownloadHistoryEntry { FileName = "two.bin", SavePath = Path.Combine(_root, "two.bin") }
        });

        Assert.Equal(2, (await store.GetAllAsync()).Count);
    }

    [Fact]
    public async Task ChecksumManifest_FindReturnsAnIndependentSnapshot()
    {
        string payload = Path.Combine(_root, "payload.bin");
        await File.WriteAllBytesAsync(payload, new byte[] { 1, 2, 3 });
        var store = new ChecksumManifestStore(Path.Combine(_root, "checksums.json"));
        await store.RecordFileAsync(payload);

        ChecksumManifestEntry first = Assert.IsType<ChecksumManifestEntry>(await store.FindAsync(payload));
        first.Sha256 = "changed-outside-the-store";
        ChecksumManifestEntry second = Assert.IsType<ChecksumManifestEntry>(await store.FindAsync(payload));

        Assert.NotEqual(first.Sha256, second.Sha256);
    }

    [Fact]
    public void ParsePath_ReturnsEmptyCollectionWhenNoSegmentMatches()
    {
        Assert.Empty(Utility.ParsePath("plain-file-name"));
    }

    [Fact]
    public void YandexLastUploadedList_ReadOnlyIListSupportsIndexing()
    {
        LastUploadedResourceList resources = LastUploadedResourceList.Parse(
            "{\"items\":[{\"name\":\"one.bin\",\"size\":7,\"type\":\"file\"}],\"limit\":20}");
        IList<Resource> list = resources;

        Assert.Equal("one.bin", list[0].Name);
        Assert.Equal(7, list[0].Size);
        Assert.Throws<NotSupportedException>(() => list.RemoveAt(0));
    }

    [Fact]
    public void FolderSorter_UsesExpectedAscendingOrderAndToleratesBadDates()
    {
        var alpha = new ColumnNode("Alpha", DateTime.Today, DateTime.Today, 1);
        var beta = new ColumnNode("Beta", DateTime.Today, DateTime.Today, 2);
        var nameSorter = new FolderItemSorter("Name", SortOrder.Ascending);

        Assert.True(nameSorter.Compare(alpha, beta) < 0);

        alpha.NodeControl2 = "not-a-date";
        beta.NodeControl2 = "also-not-a-date";
        var dateSorter = new FolderItemSorter("Created", SortOrder.Ascending);
        Assert.Equal(0, dateSorter.Compare(alpha, beta));
    }

    [Fact]
    public void CircularProgressIndicator_RendersAndDisposesWithoutLegacyPackage()
    {
        using var indicator = new CircularProgressIndicator
        {
            Size = new Size(72, 72),
            Style = ProgressBarStyle.Marquee,
            AnimationSpeed = 1000,
            ProgressWidth = 8
        };
        using var bitmap = new Bitmap(indicator.Width, indicator.Height);

        indicator.DrawToBitmap(bitmap, indicator.ClientRectangle);

        Assert.Equal(ProgressBarStyle.Marquee, indicator.Style);
        Assert.Equal(new Size(72, 72), bitmap.Size);
    }

    [Fact]
    public void SyncSettingsDownloadCard_ContainsEveryDownloadOption()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var form = new SyncSettingsForm(null);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-32000, -32000);
        form.ShowInTaskbar = false;
        form.Show();
        ThemeManager.Apply(form);
        CreateHandles(form);
        form.PerformLayout();
        Application.DoEvents();

        Label speedLimitLabel = FindControls<Label>(form)
            .Single(label => label.Text == "Speed limit (KiB/s)");
        TableLayoutPanel downloadBody = Assert.IsType<TableLayoutPanel>(speedLimitLabel.Parent);
        ModernCard downloadCard = Assert.IsType<ModernCard>(downloadBody.Parent);
        Rectangle visibleCard = downloadCard.ClientRectangle;

        Assert.All(
            downloadBody.Controls.Cast<Control>(),
            control =>
            {
                Rectangle screenBounds = downloadBody.RectangleToScreen(control.Bounds);
                Rectangle cardBounds = downloadCard.RectangleToClient(screenBounds);
                Assert.True(
                    visibleCard.Contains(cardBounds),
                    $"{control.GetType().Name} '{control.Text}' at {cardBounds} is clipped by {visibleCard}.");
            });
    }

    public void Dispose()
    {
        ThemeManager.SetMode(AppThemeMode.System);
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private static Bitmap RenderProgressBar(int value)
    {
        using var progressBar = new ThemedProgressBar
        {
            Size = new Size(202, 18),
            Minimum = 0,
            Maximum = 100,
            Value = value,
            Style = ProgressBarStyle.Continuous
        };
        var bitmap = new Bitmap(progressBar.Width, progressBar.Height);
        progressBar.DrawToBitmap(bitmap, progressBar.ClientRectangle);
        return bitmap;
    }

    private static int CountAccentPixels(Bitmap bitmap)
    {
        int y = bitmap.Height / 2;
        int accent = ThemeManager.Palette.Accent.ToArgb();
        return Enumerable.Range(0, bitmap.Width)
            .Count(x => bitmap.GetPixel(x, y).ToArgb() == accent);
    }

    private static IEnumerable<TControl> FindControls<TControl>(Control root)
        where TControl : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is TControl match)
                yield return match;
            foreach (TControl descendant in FindControls<TControl>(child))
                yield return descendant;
        }
    }

    private static void CreateHandles(Control control)
    {
        control.CreateControl();
        foreach (Control child in control.Controls)
            CreateHandles(child);
    }
}
