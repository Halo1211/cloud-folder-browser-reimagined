using Newtonsoft.Json;
using System.Security.Cryptography;
using System.Text;
using CloudFolderBrowser.Networking;

namespace CloudFolderBrowser;

public enum DownloadJobStatus
{
    Queued,
    Downloading,
    Paused,
    Completed,
    Skipped,
    Failed
}

public sealed class DownloadHistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public CloudServiceType CloudService { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string CloudPath { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string SavePath { get; set; } = string.Empty;
    public long ExpectedSize { get; set; }
    public long BytesOnDisk { get; set; }
    public DownloadJobStatus Status { get; set; }
    public string Error { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string RequestedRouteId { get; set; } = DownloadRouteIds.Automatic;
    public string EffectiveRouteId { get; set; } = string.Empty;
    public string EffectiveRouteName { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class DownloadHistoryStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CloudFolderBrowser.DownloadHistory.v1");
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<DownloadHistoryEntry>? _entries;

    public static DownloadHistoryStore Default { get; } = new(
        Path.Combine(Utility.GetApplicationDataDirectory(), "download-history.json"));

    public DownloadHistoryStore(string filePath)
    {
        _filePath = filePath;
    }

    public async Task<IReadOnlyList<DownloadHistoryEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            return _entries!
                .OrderByDescending(entry => entry.UpdatedUtc)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertAsync(DownloadHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        await UpsertManyAsync(new[] { entry }, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertManyAsync(
        IEnumerable<DownloadHistoryEntry> entries,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            foreach (DownloadHistoryEntry entry in entries)
            {
                int index = _entries!.FindIndex(existing => existing.Id == entry.Id);
                entry.UpdatedUtc = DateTime.UtcNow;
                if (index >= 0)
                    _entries[index] = Clone(entry);
                else
                    _entries.Add(Clone(entry));
            }
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveCompletedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            _entries!.RemoveAll(entry => entry.Status is DownloadJobStatus.Completed or DownloadJobStatus.Skipped);
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_entries != null)
            return;

        if (!File.Exists(_filePath))
        {
            _entries = new List<DownloadHistoryEntry>();
            return;
        }

        try
        {
            string json = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
            List<PersistedHistoryEntry> persisted =
                JsonConvert.DeserializeObject<List<PersistedHistoryEntry>>(json) ?? new();
            _entries = persisted.Select(ToEntry).ToList();
        }
        catch (JsonException)
        {
            PreserveUnreadableFile();
            _entries = new List<DownloadHistoryEntry>();
        }

        foreach (DownloadHistoryEntry entry in _entries.Where(entry => entry.Status == DownloadJobStatus.Downloading))
            entry.Status = DownloadJobStatus.Paused;
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(_filePath));
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        string temporaryPath = _filePath + ".tmp";
        string json = JsonConvert.SerializeObject(
            _entries!.Select(FromEntry).ToArray(),
            Formatting.Indented);
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
                System.Diagnostics.Debug.WriteLine($"Unable to remove temporary history file: {ex.Message}");
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
            System.Diagnostics.Debug.WriteLine($"Unable to preserve corrupt download history: {ex.Message}");
        }
    }

    private static DownloadHistoryEntry Clone(DownloadHistoryEntry entry) => new()
    {
        Id = entry.Id,
        CloudService = entry.CloudService,
        FileName = entry.FileName,
        CloudPath = entry.CloudPath,
        SourceUrl = entry.SourceUrl,
        SavePath = entry.SavePath,
        ExpectedSize = entry.ExpectedSize,
        BytesOnDisk = entry.BytesOnDisk,
        Status = entry.Status,
        Error = entry.Error,
        Sha256 = entry.Sha256,
        RequestedRouteId = entry.RequestedRouteId,
        EffectiveRouteId = entry.EffectiveRouteId,
        EffectiveRouteName = entry.EffectiveRouteName,
        UpdatedUtc = entry.UpdatedUtc
    };

    private static PersistedHistoryEntry FromEntry(DownloadHistoryEntry entry)
    {
        byte[] clear = Encoding.UTF8.GetBytes(entry.SourceUrl ?? string.Empty);
        try
        {
            byte[] protectedBytes = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
            return new PersistedHistoryEntry
            {
                Id = entry.Id,
                CloudService = entry.CloudService,
                FileName = entry.FileName,
                CloudPath = entry.CloudPath,
                ProtectedSourceUrl = Convert.ToBase64String(protectedBytes),
                SavePath = entry.SavePath,
                ExpectedSize = entry.ExpectedSize,
                BytesOnDisk = entry.BytesOnDisk,
                Status = entry.Status,
                Error = entry.Error,
                Sha256 = entry.Sha256,
                RequestedRouteId = entry.RequestedRouteId,
                EffectiveRouteId = entry.EffectiveRouteId,
                EffectiveRouteName = entry.EffectiveRouteName,
                UpdatedUtc = entry.UpdatedUtc
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    private static DownloadHistoryEntry ToEntry(PersistedHistoryEntry persisted)
    {
        string sourceUrl = persisted.SourceUrl ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(persisted.ProtectedSourceUrl))
        {
            try
            {
                byte[] protectedBytes = Convert.FromBase64String(persisted.ProtectedSourceUrl);
                byte[] clear = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
                try
                {
                    sourceUrl = Encoding.UTF8.GetString(clear);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(clear);
                }
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                sourceUrl = string.Empty;
            }
        }

        return new DownloadHistoryEntry
        {
            Id = persisted.Id,
            CloudService = persisted.CloudService,
            FileName = persisted.FileName,
            CloudPath = persisted.CloudPath,
            SourceUrl = sourceUrl,
            SavePath = persisted.SavePath,
            ExpectedSize = persisted.ExpectedSize,
            BytesOnDisk = persisted.BytesOnDisk,
            Status = persisted.Status,
            Error = persisted.Error,
            Sha256 = persisted.Sha256,
            RequestedRouteId = string.IsNullOrWhiteSpace(persisted.RequestedRouteId)
                ? DownloadRouteIds.Automatic
                : persisted.RequestedRouteId,
            EffectiveRouteId = persisted.EffectiveRouteId,
            EffectiveRouteName = persisted.EffectiveRouteName,
            UpdatedUtc = persisted.UpdatedUtc
        };
    }

    private sealed class PersistedHistoryEntry
    {
        public Guid Id { get; set; }
        public CloudServiceType CloudService { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string CloudPath { get; set; } = string.Empty;
        public string ProtectedSourceUrl { get; set; } = string.Empty;

        // Read-only migration field for history files created before v0.11.0.
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? SourceUrl { get; set; }

        public string SavePath { get; set; } = string.Empty;
        public long ExpectedSize { get; set; }
        public long BytesOnDisk { get; set; }
        public DownloadJobStatus Status { get; set; }
        public string Error { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public string RequestedRouteId { get; set; } = string.Empty;
        public string EffectiveRouteId { get; set; } = string.Empty;
        public string EffectiveRouteName { get; set; } = string.Empty;
        public DateTime UpdatedUtc { get; set; }
    }
}
