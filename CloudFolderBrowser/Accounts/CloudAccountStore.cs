using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace CloudFolderBrowser.Accounts;

public enum CloudAccountProvider
{
    Mega = 0,
    YandexDisk = 1,
    WebDav = 2,
    Dropbox = 3,
    GoogleDrive = 4,
    AllDebrid = 5,
    RealDebrid = 6,
    DebridLink = 7,
    Premiumize = 8,
    TorBox = 9
}

public sealed class CloudAccountProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public CloudAccountProvider Provider { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string ServerUrl { get; set; } = string.Empty;
    public string Secret { get; set; } = string.Empty;
    public string SecretKind { get; set; } = "token";
    public bool IsActive { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public string ProviderName => Provider switch
    {
        CloudAccountProvider.Mega => "MEGA",
        CloudAccountProvider.YandexDisk => "Yandex Disk",
        CloudAccountProvider.WebDav => "WebDAV / Nextcloud",
        CloudAccountProvider.Dropbox => "Dropbox",
        CloudAccountProvider.GoogleDrive => "Google Drive",
        CloudAccountProvider.AllDebrid => "AllDebrid",
        CloudAccountProvider.RealDebrid => "Real-Debrid",
        CloudAccountProvider.DebridLink => "Debrid-Link",
        CloudAccountProvider.Premiumize => "Premiumize.me",
        CloudAccountProvider.TorBox => "TorBox",
        _ => Provider.ToString()
    };
}

public sealed class CloudAccountStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CloudFolderBrowser.Accounts.v1");
    private readonly string _filePath;
    private readonly object _gate = new();
    private bool _preservedUnreadableFile;

    public static CloudAccountStore Default { get; } = new(
        Path.Combine(Utility.GetApplicationDataDirectory(), "accounts.json"));

    public CloudAccountStore(string filePath)
    {
        _filePath = Path.GetFullPath(filePath);
    }

    public IReadOnlyList<CloudAccountProfile> GetAll()
    {
        lock (_gate)
            return LoadPersisted().Select(ToProfile).ToArray();
    }

    public CloudAccountProfile? GetActive(CloudAccountProvider provider)
    {
        return GetAll().FirstOrDefault(profile => profile.Provider == provider && profile.IsActive);
    }

    public void Upsert(CloudAccountProfile profile, bool makeActive = true)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.DisplayName))
            throw new ArgumentException("An account display name is required.", nameof(profile));

        lock (_gate)
        {
            List<PersistedAccount> accounts = LoadPersisted();
            if (makeActive)
            {
                foreach (PersistedAccount account in accounts.Where(item => item.Provider == profile.Provider))
                    account.IsActive = false;
                profile.IsActive = true;
            }

            profile.UpdatedUtc = DateTime.UtcNow;
            PersistedAccount persisted = FromProfile(profile);
            int index = accounts.FindIndex(account => account.Id == profile.Id);
            if (index >= 0)
                accounts[index] = persisted;
            else
                accounts.Add(persisted);
            SavePersisted(accounts);
        }
    }

    public void SetActive(CloudAccountProvider provider, Guid id)
    {
        lock (_gate)
        {
            List<PersistedAccount> accounts = LoadPersisted();
            if (!accounts.Any(account => account.Provider == provider && account.Id == id))
                throw new InvalidOperationException("The selected account no longer exists.");
            foreach (PersistedAccount account in accounts.Where(item => item.Provider == provider))
                account.IsActive = account.Id == id;
            SavePersisted(accounts);
        }
    }

    public void Remove(Guid id)
    {
        lock (_gate)
        {
            List<PersistedAccount> accounts = LoadPersisted();
            PersistedAccount? removed = accounts.FirstOrDefault(account => account.Id == id);
            if (removed == null)
                return;
            accounts.Remove(removed);
            if (removed.IsActive)
            {
                PersistedAccount? replacement = accounts
                    .Where(account => account.Provider == removed.Provider)
                    .OrderByDescending(account => account.UpdatedUtc)
                    .FirstOrDefault();
                if (replacement != null)
                    replacement.IsActive = true;
            }
            SavePersisted(accounts);
        }
    }

    private List<PersistedAccount> LoadPersisted()
    {
        if (!File.Exists(_filePath))
            return new List<PersistedAccount>();
        try
        {
            string json = File.ReadAllText(_filePath);
            return JsonConvert.DeserializeObject<List<PersistedAccount>>(json)
                ?? new List<PersistedAccount>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            PreserveUnreadableFile();
            return new List<PersistedAccount>();
        }
    }

    private void SavePersisted(List<PersistedAccount> accounts)
    {
        string? directory = Path.GetDirectoryName(_filePath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("The account store path has no directory.");
        Directory.CreateDirectory(directory);
        string temporaryPath = _filePath + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(accounts, Formatting.Indented));
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
                System.Diagnostics.Debug.WriteLine($"Unable to remove temporary account file: {ex.Message}");
            }
        }
    }

    private void PreserveUnreadableFile()
    {
        if (_preservedUnreadableFile)
            return;

        try
        {
            string backupPath = _filePath + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            File.Copy(_filePath, backupPath, false);
            _preservedUnreadableFile = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Unable to preserve corrupt account store: {ex.Message}");
        }
    }

    private static PersistedAccount FromProfile(CloudAccountProfile profile)
    {
        byte[] clear = Encoding.UTF8.GetBytes(profile.Secret ?? string.Empty);
        try
        {
            byte[] protectedBytes = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
            return new PersistedAccount
            {
                Id = profile.Id,
                Provider = profile.Provider,
                DisplayName = profile.DisplayName.Trim(),
                UserName = profile.UserName.Trim(),
                ServerUrl = profile.ServerUrl.Trim(),
                ProtectedSecret = Convert.ToBase64String(protectedBytes),
                SecretKind = profile.SecretKind,
                IsActive = profile.IsActive,
                UpdatedUtc = profile.UpdatedUtc
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    private static CloudAccountProfile ToProfile(PersistedAccount account)
    {
        string secret = string.Empty;
        try
        {
            byte[] protectedBytes = Convert.FromBase64String(account.ProtectedSecret);
            byte[] clear = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                secret = Encoding.UTF8.GetString(clear);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            secret = string.Empty;
        }

        return new CloudAccountProfile
        {
            Id = account.Id,
            Provider = account.Provider,
            DisplayName = account.DisplayName,
            UserName = account.UserName,
            ServerUrl = account.ServerUrl,
            Secret = secret,
            SecretKind = account.SecretKind,
            IsActive = account.IsActive,
            UpdatedUtc = account.UpdatedUtc
        };
    }

    private sealed class PersistedAccount
    {
        public Guid Id { get; set; }
        public CloudAccountProvider Provider { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public string ServerUrl { get; set; } = string.Empty;
        public string ProtectedSecret { get; set; } = string.Empty;
        public string SecretKind { get; set; } = "token";
        public bool IsActive { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }
}
