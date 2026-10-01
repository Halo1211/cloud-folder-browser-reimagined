using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using CloudFolderBrowser.Networking;

namespace CloudFolderBrowser.Tests;

[Collection("Real download integration")]
public sealed class SixthPassAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cfb-sixth-audit-" + Guid.NewGuid().ToString("N"));
    public SixthPassAuditTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ExternalProcess_CancelWhileWritingInputStopsOwnedChild()
    {
        string pidPath = Path.Combine(_root, "child.pid");
        var start = Shell("[System.IO.File]::WriteAllText($env:CFB_AUDIT_PID_PATH, [string]$PID); Start-Sleep -Seconds 30", true);
        start.Environment["CFB_AUDIT_PID_PATH"] = pidPath;
        using var cancellation = new CancellationTokenSource();
        Task operation = DownloadPostProcessor.RunProcessAsync(start, "fixture", cancellation.Token, new string('x', 1024 * 1024));
        Process? ownedChild = null;
        try
        {
            await WaitUntilAsync(() => File.Exists(pidPath) && new FileInfo(pidPath).Length > 0);
            ownedChild = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pidPath)));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
            await Task.Delay(150);
            Assert.True(ownedChild.HasExited, "Cancellation left the fixture process running.");
        }
        finally
        {
            cancellation.Cancel();
            if (ownedChild != null)
            {
                if (!ownedChild.HasExited) ownedChild.Kill(entireProcessTree: true);
                await ownedChild.WaitForExitAsync();
                ownedChild.Dispose();
            }
        }
    }

    [Fact]
    public async Task ExternalProcess_AlreadyCancelledDoesNotLaunchChild()
    {
        string pidPath = Path.Combine(_root, "not-started.pid");
        var start = Shell("[System.IO.File]::WriteAllText($env:CFB_AUDIT_PID_PATH, [string]$PID)", false);
        start.Environment["CFB_AUDIT_PID_PATH"] = pidPath;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DownloadPostProcessor.RunProcessAsync(start, "fixture", cancellation.Token));
        await Task.Delay(600);
        Assert.False(File.Exists(pidPath));
    }

    [Fact]
    public async Task ExternalProcess_SuccessAndNonzeroExitAreHandled()
    {
        await DownloadPostProcessor.RunProcessAsync(Shell("[Console]::WriteLine('fixture output')", false), "fixture", CancellationToken.None);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => DownloadPostProcessor.RunProcessAsync(
            Shell("[Console]::Error.WriteLine('fixture failure'); exit 7", false), "fixture", CancellationToken.None));
        Assert.Contains("exit code 7", error.Message);
        Assert.Contains("fixture failure", error.Message);
    }

    [Fact]
    public void LocalFolder_DefaultInstanceHasUsableEmptyCollections()
    {
        var folder = new LocalFolder();
        Assert.Empty(folder.Files);
        Assert.Empty(folder.Subfolders);
        folder.CalculateFolderSize();
        Assert.Equal(0, folder.Size);
        Assert.Equal(0, folder.FilesNumber);
    }

    [Fact]
    public async Task BandwidthLimiter_PreCancelledCallDoesNotReserveBandwidth()
    {
        var limiter = new BandwidthLimiter(1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await limiter.ThrottleAsync(4096, cancellation.Token));
        await limiter.ThrottleAsync(1).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task BandwidthLimiter_CancelledWaitDoesNotDelayFollowingTransfers()
    {
        var limiter = new BandwidthLimiter(1);
        await limiter.ThrottleAsync(1024);
        using var cancellation = new CancellationTokenSource();
        Task cancelledWait = limiter.ThrottleAsync(4096, cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWait);
        await limiter.ThrottleAsync(1).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task BandwidthLimiter_ActiveTransfersStillRespectAggregateLimit()
    {
        var limiter = new BandwidthLimiter(1);
        var elapsed = Stopwatch.StartNew();
        await limiter.ThrottleAsync(1024);
        await Task.WhenAll(limiter.ThrottleAsync(1024).AsTask(), limiter.ThrottleAsync(1024).AsTask());
        Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(1800));
    }

    [Fact]
    public async Task ExternalProcess_CancelWhileWaitingForExitStopsOwnedChild()
    {
        string pidPath = Path.Combine(_root, "waiting.pid");
        var start = Shell("[System.IO.File]::WriteAllText($env:CFB_AUDIT_PID_PATH, [string]$PID); Start-Sleep -Seconds 30", false);
        start.Environment["CFB_AUDIT_PID_PATH"] = pidPath;
        using var cancellation = new CancellationTokenSource();
        Task operation = DownloadPostProcessor.RunProcessAsync(start, "fixture", cancellation.Token);
        await WaitUntilAsync(() => File.Exists(pidPath) && new FileInfo(pidPath).Length > 0);
        using var ownedChild = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pidPath)));
        try
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(ownedChild.HasExited);
        }
        finally
        {
            if (!ownedChild.HasExited) ownedChild.Kill(entireProcessTree: true);
            await ownedChild.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task ExternalProcess_InputIsWrittenAndClosedForNormalCompletion()
    {
        await DownloadPostProcessor.RunProcessAsync(
            Shell("if ([Console]::ReadLine() -ne 'fixture input') { exit 4 }; if ([Console]::ReadLine() -ne $null) { exit 5 }", true),
            "fixture", CancellationToken.None, "fixture input").WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MaxValue / 1024 + 1)]
    public void BandwidthLimiter_OverflowCannotSilentlyDisableLimit(long limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BandwidthLimiter(limit));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZipExtraction_RejectsExistingJunctionBeforeWritingOutside(bool junctionIsRoot)
    {
        string archive = CreateArchive("fixture.zip", junctionIsRoot ? "keep.txt" : "linked/keep.txt");
        string outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        string sentinel = Path.Combine(outside, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "original");
        string destination = Path.Combine(_root, "fixture");
        string junction = junctionIsRoot ? destination : Path.Combine(Directory.CreateDirectory(destination).FullName, "linked");
        var start = Shell("New-Item -ItemType Junction -Path $env:CFB_AUDIT_LINK_PATH -Target $env:CFB_AUDIT_LINK_TARGET -ErrorAction Stop | Out-Null", false);
        start.Environment["CFB_AUDIT_LINK_PATH"] = junction;
        start.Environment["CFB_AUDIT_LINK_TARGET"] = outside;
        await DownloadPostProcessor.RunProcessAsync(start, "fixture junction", CancellationToken.None);
        try
        {
            Assert.True(File.GetAttributes(junction).HasFlag(FileAttributes.ReparsePoint));
            await Assert.ThrowsAsync<InvalidDataException>(() => Extract(archive));
            Assert.Equal("original", await File.ReadAllTextAsync(sentinel));
        }
        finally { Directory.Delete(junction); }
    }

    [Theory]
    [InlineData("keep.txt:hidden")]
    [InlineData("keep.txt:zone:$DATA")]
    public async Task ZipExtraction_RejectsAlternateDataStreams(string entry)
    {
        string archive = CreateArchive("stream.zip", entry);
        await Assert.ThrowsAsync<InvalidDataException>(() => Extract(archive));
    }

    private async Task Extract(string archive)
    {
        bool originalEnabled = Properties.Settings.Default.archivePostProcessingEnabled;
        bool originalPar2 = Properties.Settings.Default.par2RepairEnabled;
        string originalTool = Properties.Settings.Default.archiveToolPath;
        string originalPassword = Properties.Settings.Default.protectedArchivePassword;
        try
        {
            Properties.Settings.Default.archivePostProcessingEnabled = true;
            Properties.Settings.Default.par2RepairEnabled = false;
            Properties.Settings.Default.archiveToolPath = string.Empty;
            Properties.Settings.Default.protectedArchivePassword = string.Empty;
            await new DownloadPostProcessor().ProcessAsync(archive, CancellationToken.None);
        }
        finally
        {
            Properties.Settings.Default.archivePostProcessingEnabled = originalEnabled;
            Properties.Settings.Default.par2RepairEnabled = originalPar2;
            Properties.Settings.Default.archiveToolPath = originalTool;
            Properties.Settings.Default.protectedArchivePassword = originalPassword;
        }
    }

    private string CreateArchive(string name, string entryName)
    {
        string path = Path.Combine(_root, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry(entryName).Open());
        writer.Write("replacement fixture");
        return path;
    }

    private static ProcessStartInfo Shell(string script, bool input)
    {
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = input, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        return start;
    }

    private static async Task WaitUntilAsync(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!ready()) await Task.Delay(20, timeout.Token);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
