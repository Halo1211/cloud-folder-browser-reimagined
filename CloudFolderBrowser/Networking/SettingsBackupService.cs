using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CloudFolderBrowser.Accounts;
using Newtonsoft.Json;

namespace CloudFolderBrowser.Networking;

public sealed class SettingsBackupDocument
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public SettingsBackupSnapshot Settings { get; set; } = new();
    public List<CloudAccountProfile> Accounts { get; set; } = new();
    public List<PortableProxyRule> ProviderProxyRules { get; set; } = new();
}

public sealed class SettingsBackupSnapshot
{
    public string LastSyncFolderPath { get; set; } = string.Empty;
    public string PublicFoldersJson { get; set; } = string.Empty;
    public bool LoggedInYandex { get; set; }
    public bool LoggedInMega { get; set; }
    public string YandexAccessToken { get; set; } = string.Empty;
    public string SavedPasswordsJson { get; set; } = string.Empty;
    public string MegaLoginToken { get; set; } = string.Empty;
    public string MegaLogin { get; set; } = string.Empty;
    public string MegaPassword { get; set; } = string.Empty;
    public string FogLinkAddress { get; set; } = string.Empty;
    public bool UseProgressBar { get; set; }
    public int OverwriteMode { get; set; }
    public int MaximumDownloads { get; set; }
    public int MaximumSegmentsPerFile { get; set; } = 4;
    public bool SegmentedDownloadsEnabled { get; set; } = true;
    public bool AdaptiveSegmentsEnabled { get; set; }
    public bool PersistentByteMapEnabled { get; set; }
    public bool PortableAllsyncEnabled { get; set; }
    public bool RouteScoringEnabled { get; set; }
    public bool RemoteChecksumValidationEnabled { get; set; }
    public bool SmartSchedulerEnabled { get; set; }
    public int MaximumDownloadsPerHost { get; set; } = 2;
    public bool ArchivePostProcessingEnabled { get; set; }
    public bool Par2RepairEnabled { get; set; }
    public string ArchiveToolPath { get; set; } = string.Empty;
    public string Par2ToolPath { get; set; } = string.Empty;
    public string ArchivePassword { get; set; } = string.Empty;
    public int RetryDelay { get; set; }
    public int RetryMax { get; set; }
    public double CheckFileSizeError { get; set; }
    public bool FolderNewFiles { get; set; }
    public bool CheckDownloadedFileSize { get; set; }
    public bool VerifySha256 { get; set; }
    public bool FlareSolverrEnabled { get; set; }
    public string FlareSolverrUrl { get; set; } = string.Empty;
    public int FlareSolverrTimeoutSeconds { get; set; }
    public int ThemeMode { get; set; }
    public string PreferredDebridAccountId { get; set; } = string.Empty;
    public int DnsProvider { get; set; }
    public int DnsTransport { get; set; }
    public string CustomDnsServer { get; set; } = string.Empty;
    public bool DnsFallbackToSystem { get; set; }
    public bool PreferIpv6 { get; set; }
    public int ProxyMode { get; set; }
    public string ProxyUrl { get; set; } = string.Empty;
    public string ProxyUsername { get; set; } = string.Empty;
    public string ProxyPassword { get; set; } = string.Empty;
    public bool ProxyBypassLocal { get; set; }
    public int ConnectTimeoutSeconds { get; set; }
    public int BandwidthLimitKibPerSecond { get; set; }
}

public sealed class PortableProxyRule
{
    public string RouteKey { get; set; } = string.Empty;
    public ProxyMode Mode { get; set; }
    public string ProxyUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool BypassLocal { get; set; }
}

public static class SettingsBackupService
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("CFBACKUP");
    private const byte FormatVersion = 1;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int DerivationIterations = 210_000;

    public static SettingsBackupDocument Capture()
    {
        var value = Properties.Settings.Default;
        return new SettingsBackupDocument
        {
            Settings = new SettingsBackupSnapshot
            {
                LastSyncFolderPath = value.lastSyncFolderPath ?? string.Empty,
                PublicFoldersJson = value.publicFoldersJson ?? string.Empty,
                LoggedInYandex = value.loginedYandex,
                LoggedInMega = value.loginedMega,
                YandexAccessToken = value.accessTokenYandex ?? string.Empty,
                SavedPasswordsJson = value.savedPasswordsJson ?? string.Empty,
                MegaLoginToken = value.loginTokenMega ?? string.Empty,
                MegaLogin = value.megaLogin ?? string.Empty,
                MegaPassword = value.megaPassword ?? string.Empty,
                FogLinkAddress = value.fogLinkAddress ?? string.Empty,
                UseProgressBar = value.useProgressBar,
                OverwriteMode = value.overwriteMode,
                MaximumDownloads = value.maximumDownloads,
                MaximumSegmentsPerFile = value.maximumSegmentsPerFile,
                SegmentedDownloadsEnabled = value.segmentedDownloadsEnabled,
                AdaptiveSegmentsEnabled = value.adaptiveSegmentsEnabled,
                PersistentByteMapEnabled = value.persistentByteMapEnabled,
                PortableAllsyncEnabled = value.portableAllsyncEnabled,
                RouteScoringEnabled = value.routeScoringEnabled,
                RemoteChecksumValidationEnabled = value.remoteChecksumValidationEnabled,
                SmartSchedulerEnabled = value.smartSchedulerEnabled,
                MaximumDownloadsPerHost = value.maximumDownloadsPerHost,
                ArchivePostProcessingEnabled = value.archivePostProcessingEnabled,
                Par2RepairEnabled = value.par2RepairEnabled,
                ArchiveToolPath = value.archiveToolPath ?? string.Empty,
                Par2ToolPath = value.par2ToolPath ?? string.Empty,
                ArchivePassword = ProxySecretProtector.Unprotect(value.protectedArchivePassword),
                RetryDelay = value.retryDelay,
                RetryMax = value.retryMax,
                CheckFileSizeError = value.checkFileSizeError,
                FolderNewFiles = value.folderNewFiles,
                CheckDownloadedFileSize = value.checkDownloadedFileSize,
                VerifySha256 = value.verifySha256,
                FlareSolverrEnabled = value.flareSolverrEnabled,
                FlareSolverrUrl = value.flareSolverrUrl ?? string.Empty,
                FlareSolverrTimeoutSeconds = value.flareSolverrTimeoutSeconds,
                ThemeMode = value.themeMode,
                PreferredDebridAccountId = value.preferredDebridAccountId ?? string.Empty,
                DnsProvider = value.dnsProvider,
                DnsTransport = value.dnsTransport,
                CustomDnsServer = value.customDnsServer ?? string.Empty,
                DnsFallbackToSystem = value.dnsFallbackToSystem,
                PreferIpv6 = value.preferIpv6,
                ProxyMode = value.proxyMode,
                ProxyUrl = value.proxyUrl ?? string.Empty,
                ProxyUsername = value.proxyUsername ?? string.Empty,
                ProxyPassword = ProxySecretProtector.Unprotect(value.protectedProxyPassword),
                ProxyBypassLocal = value.proxyBypassLocal,
                ConnectTimeoutSeconds = value.connectTimeoutSeconds,
                BandwidthLimitKibPerSecond = value.bandwidthLimitKibPerSecond
            },
            Accounts = CloudAccountStore.Default.GetAll().ToList(),
            ProviderProxyRules = ProviderProxyRuleStore.Load().Select(rule => new PortableProxyRule
            {
                RouteKey = rule.RouteKey,
                Mode = rule.Mode,
                ProxyUrl = rule.ProxyUrl,
                Username = rule.Username,
                Password = rule.Password,
                BypassLocal = rule.BypassLocal
            }).ToList()
        };
    }

    public static void Apply(SettingsBackupDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != 1)
            throw new InvalidDataException($"Unsupported backup schema version {document.SchemaVersion}.");
        SettingsBackupSnapshot source = document.Settings
            ?? throw new InvalidDataException("The backup does not contain settings.");
        var value = Properties.Settings.Default;
        value.lastSyncFolderPath = source.LastSyncFolderPath ?? string.Empty;
        value.publicFoldersJson = source.PublicFoldersJson ?? string.Empty;
        value.loginedYandex = source.LoggedInYandex;
        value.loginedMega = source.LoggedInMega;
        value.accessTokenYandex = source.YandexAccessToken ?? string.Empty;
        value.savedPasswordsJson = source.SavedPasswordsJson ?? string.Empty;
        value.loginTokenMega = source.MegaLoginToken ?? string.Empty;
        value.megaLogin = source.MegaLogin ?? string.Empty;
        value.megaPassword = source.MegaPassword ?? string.Empty;
        value.fogLinkAddress = source.FogLinkAddress ?? string.Empty;
        value.useProgressBar = source.UseProgressBar;
        value.overwriteMode = Math.Clamp(source.OverwriteMode, 0, 3);
        value.maximumDownloads = Math.Clamp(source.MaximumDownloads, 1, 128);
        value.maximumSegmentsPerFile = Math.Clamp(source.MaximumSegmentsPerFile, 1, 8);
        value.segmentedDownloadsEnabled = source.SegmentedDownloadsEnabled;
        value.adaptiveSegmentsEnabled = source.AdaptiveSegmentsEnabled;
        value.persistentByteMapEnabled = source.PersistentByteMapEnabled;
        value.portableAllsyncEnabled = source.PortableAllsyncEnabled;
        value.routeScoringEnabled = source.RouteScoringEnabled;
        value.remoteChecksumValidationEnabled = source.RemoteChecksumValidationEnabled;
        value.smartSchedulerEnabled = source.SmartSchedulerEnabled;
        value.maximumDownloadsPerHost = Math.Clamp(source.MaximumDownloadsPerHost, 1, 32);
        value.archivePostProcessingEnabled = source.ArchivePostProcessingEnabled;
        value.par2RepairEnabled = source.Par2RepairEnabled;
        value.archiveToolPath = source.ArchiveToolPath ?? string.Empty;
        value.par2ToolPath = source.Par2ToolPath ?? string.Empty;
        value.protectedArchivePassword = ProxySecretProtector.Protect(source.ArchivePassword);
        value.retryDelay = Math.Clamp(source.RetryDelay, 0, 600_000);
        value.retryMax = Math.Clamp(source.RetryMax, 0, 100);
        value.checkFileSizeError = Math.Clamp(source.CheckFileSizeError, 0, 1);
        value.folderNewFiles = source.FolderNewFiles;
        value.checkDownloadedFileSize = source.CheckDownloadedFileSize;
        value.verifySha256 = source.VerifySha256;
        value.flareSolverrEnabled = source.FlareSolverrEnabled;
        value.flareSolverrUrl = source.FlareSolverrUrl ?? string.Empty;
        value.flareSolverrTimeoutSeconds = Math.Clamp(source.FlareSolverrTimeoutSeconds, 5, 600);
        value.themeMode = Enum.IsDefined(typeof(Theming.AppThemeMode), source.ThemeMode) ? source.ThemeMode : 0;
        value.preferredDebridAccountId = source.PreferredDebridAccountId ?? string.Empty;
        value.dnsProvider = Enum.IsDefined(typeof(DnsProviderMode), source.DnsProvider) ? source.DnsProvider : 0;
        value.dnsTransport = Enum.IsDefined(typeof(DnsTransport), source.DnsTransport) ? source.DnsTransport : 0;
        value.customDnsServer = source.CustomDnsServer ?? string.Empty;
        value.dnsFallbackToSystem = source.DnsFallbackToSystem;
        value.preferIpv6 = source.PreferIpv6;
        value.proxyMode = Enum.IsDefined(typeof(ProxyMode), source.ProxyMode) ? source.ProxyMode : 1;
        value.proxyUrl = source.ProxyUrl ?? string.Empty;
        value.proxyUsername = source.ProxyUsername ?? string.Empty;
        value.protectedProxyPassword = ProxySecretProtector.Protect(source.ProxyPassword);
        value.proxyBypassLocal = source.ProxyBypassLocal;
        value.connectTimeoutSeconds = Math.Clamp(source.ConnectTimeoutSeconds, 5, 120);
        value.bandwidthLimitKibPerSecond = Math.Clamp(source.BandwidthLimitKibPerSecond, 0, 1_000_000);

        var rules = (document.ProviderProxyRules ?? new List<PortableProxyRule>()).Select(sourceRule =>
        {
            var rule = new ProviderProxyRule
            {
                RouteKey = sourceRule.RouteKey,
                Mode = sourceRule.Mode,
                ProxyUrl = sourceRule.ProxyUrl,
                Username = sourceRule.Username,
                BypassLocal = sourceRule.BypassLocal
            };
            rule.Password = sourceRule.Password;
            return rule;
        }).ToArray();
        ProviderProxyRuleStore.Save(rules);
        value.Save();

        foreach (CloudAccountProfile account in document.Accounts ?? new List<CloudAccountProfile>())
        {
            if (account.Id == Guid.Empty
                || !Enum.IsDefined(typeof(CloudAccountProvider), account.Provider)
                || string.IsNullOrWhiteSpace(account.DisplayName))
                continue;
            CloudAccountStore.Default.Upsert(account, account.IsActive);
        }
    }

    public static byte[] Encrypt(SettingsBackupDocument document, string password)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidatePassword(password);
        byte[] clear = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(document));
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, DerivationIterations, HashAlgorithmName.SHA256, KeySize);
        byte[] cipher = new byte[clear.Length];
        byte[] tag = new byte[TagSize];
        byte[] associatedData = Magic.Append(FormatVersion).ToArray();
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, clear, cipher, tag, associatedData);
            byte[] output = new byte[Magic.Length + 1 + SaltSize + NonceSize + TagSize + sizeof(int) + cipher.Length];
            int offset = 0;
            Magic.CopyTo(output, offset); offset += Magic.Length;
            output[offset++] = FormatVersion;
            salt.CopyTo(output, offset); offset += SaltSize;
            nonce.CopyTo(output, offset); offset += NonceSize;
            tag.CopyTo(output, offset); offset += TagSize;
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(offset, sizeof(int)), cipher.Length); offset += sizeof(int);
            cipher.CopyTo(output, offset);
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static SettingsBackupDocument Decrypt(ReadOnlySpan<byte> data, string password)
    {
        ValidatePassword(password);
        int minimumLength = Magic.Length + 1 + SaltSize + NonceSize + TagSize + sizeof(int) + 1;
        if (data.Length < minimumLength || !data[..Magic.Length].SequenceEqual(Magic))
            throw new InvalidDataException("This is not a Cloud Folder Browser encrypted backup.");
        int offset = Magic.Length;
        byte version = data[offset++];
        if (version != FormatVersion)
            throw new InvalidDataException($"Unsupported backup format version {version}.");
        ReadOnlySpan<byte> salt = data.Slice(offset, SaltSize); offset += SaltSize;
        ReadOnlySpan<byte> nonce = data.Slice(offset, NonceSize); offset += NonceSize;
        ReadOnlySpan<byte> tag = data.Slice(offset, TagSize); offset += TagSize;
        int cipherLength = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, sizeof(int))); offset += sizeof(int);
        if (cipherLength < 1 || cipherLength != data.Length - offset)
            throw new InvalidDataException("The backup is truncated or malformed.");
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, DerivationIterations, HashAlgorithmName.SHA256, KeySize);
        byte[] clear = new byte[cipherLength];
        byte[] associatedData = Magic.Append(version).ToArray();
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, data[offset..], tag, clear, associatedData);
            return JsonConvert.DeserializeObject<SettingsBackupDocument>(Encoding.UTF8.GetString(clear))
                ?? throw new InvalidDataException("The backup payload is empty.");
        }
        catch (CryptographicException ex)
        {
            throw new CryptographicException("The backup password is incorrect or the file has been modified.", ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The backup payload is invalid.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            throw new ArgumentException("Use a backup password with at least 8 characters.", nameof(password));
    }
}
