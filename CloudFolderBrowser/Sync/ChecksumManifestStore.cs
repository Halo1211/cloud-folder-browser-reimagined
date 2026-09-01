using Newtonsoft.Json;
using System.Security.Cryptography;

namespace CloudFolderBrowser.Sync;

public sealed class ChecksumManifestEntry
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
}

public sealed class ChecksumManifestStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, ChecksumManifestEntry>? _entries;

    public static ChecksumManifestStore Default { get; } = new(
        Path.Combine(Utility.GetApplicationDataDirectory(), "checksums.json"));

    public ChecksumManifestStore(string filePath)
    {
        _filePath = filePath;
    }

    public async Task<ChecksumManifestEntry?> FindAsync(string path, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            _entries!.TryGetValue(Normalize(path), out ChecksumManifestEntry? entry);
            return entry == null ? null : Clone(entry);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ChecksumManifestEntry> RecordFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        string hash = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
        var info = new FileInfo(path);
        var entry = new ChecksumManifestEntry
        {
            Path = info.FullName,
            Size = info.Length,
            Sha256 = hash,
            UpdatedUtc = DateTime.UtcNow
        };

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            _entries![Normalize(path)] = entry;
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            return entry;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool?> VerifyFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ChecksumManifestEntry? entry = await FindAsync(path, cancellationToken).ConfigureAwait(false);
        if (entry == null || !File.Exists(path))
            return null;

        var info = new FileInfo(path);
        if (info.Length != entry.Size)
            return false;

        string current = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
        return string.Equals(current, entry.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha256 = SHA256.Create();
        byte[] hash = await sha256.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_entries != null)
            return;

        if (!File.Exists(_filePath))
        {
            _entries = new Dictionary<string, ChecksumManifestEntry>(StringComparer.OrdinalIgnoreCase);
            return;
        }

        try
        {
            string json = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
            var values = JsonConvert.DeserializeObject<List<ChecksumManifestEntry>>(json) ?? new();
            _entries = values
                .Where(value => !string.IsNullOrWhiteSpace(value.Path))
                .GroupBy(value => Normalize(value.Path), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(x => x.UpdatedUtc).First(), StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            PreserveUnreadableFile();
            _entries = new Dictionary<string, ChecksumManifestEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(_filePath));
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        string temporaryPath = _filePath + ".tmp";
        string json = JsonConvert.SerializeObject(_entries!.Values.OrderBy(x => x.Path), Formatting.Indented);
        try
        {
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, _filePath, true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"Unable to remove temporary checksum file: {ex.Message}");
            }
        }
    }

    private void PreserveUnreadableFile()
    {
        try
        {
            string backupPath = _filePath + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            File.Copy(_filePath, backupPath, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Unable to preserve corrupt checksum manifest: {ex.Message}");
        }
    }

    private static ChecksumManifestEntry Clone(ChecksumManifestEntry entry) => new()
    {
        Path = entry.Path,
        Size = entry.Size,
        Sha256 = entry.Sha256,
        UpdatedUtc = entry.UpdatedUtc
    };

    private static string Normalize(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
