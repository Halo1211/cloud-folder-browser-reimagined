using System.Configuration;
using CloudFolderBrowser.Accounts;
using CloudFolderBrowser.Networking;
using CloudFolderBrowser.Sync;
using Newtonsoft.Json;

namespace CloudFolderBrowser.Tests;

[Collection("Real download integration")]
public sealed class SecondPassAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cfb-second-audit-" + Guid.NewGuid().ToString("N"));

    public SecondPassAuditTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ProxyRules_LoadSkipsNullEntries()
    {
        string original = Properties.Settings.Default.providerProxyRulesJson;
        try
        {
            Properties.Settings.Default.providerProxyRulesJson = "[null,{\"RouteKey\":\"Mega\",\"Mode\":0}]";
            Assert.Equal("Mega", Assert.Single(ProviderProxyRuleStore.Load()).RouteKey);
        }
        finally { Properties.Settings.Default.providerProxyRulesJson = original; }
    }

    [Fact]
    public void Backup_InvalidProxyRuleDoesNotChangeSettings()
    {
        var settings = Properties.Settings.Default;
        var original = settings.PropertyValues.Cast<SettingsPropertyValue>()
            .ToDictionary(value => value.Name, value => value.PropertyValue);
        string before = settings.lastSyncFolderPath;
        try
        {
            var document = new SettingsBackupDocument
            {
                Settings = new SettingsBackupSnapshot { LastSyncFolderPath = "must-not-be-applied" },
                ProviderProxyRules = new() { new PortableProxyRule { RouteKey = "Mega", Mode = ProxyMode.Custom, ProxyUrl = "invalid://proxy" } }
            };

            Assert.Throws<FormatException>(() => SettingsBackupService.Apply(document));
            Assert.Equal(before, settings.lastSyncFolderPath);
        }
        finally
        {
            foreach (var (name, value) in original)
                settings[name] = value;
        }
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("ratio")]
    public void Backup_DecryptRejectsInvalidPayloadBeforeApply(string kind)
    {
        var document = new SettingsBackupDocument();
        if (kind == "settings")
            document.Settings = null!;
        else
            document.Settings.CheckFileSizeError = double.NaN;
        byte[] encrypted = SettingsBackupService.Encrypt(document, "test password");

        Assert.Throws<InvalidDataException>(() => SettingsBackupService.Decrypt(encrypted, "test password"));
    }

    [Fact]
    public void AccountStore_NullEncryptedSecretDoesNotCrashLoading()
    {
        string path = Path.Combine(_root, "accounts.json");
        File.WriteAllText(path, "[{\"Provider\":0,\"DisplayName\":\"Recovered account\",\"ProtectedSecret\":null}]");

        Assert.Equal(string.Empty, Assert.Single(new CloudAccountStore(path).GetAll()).Secret);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Backup_DecryptRejectsInvalidGlobalNetworkConfiguration(bool proxy)
    {
        var document = new SettingsBackupDocument();
        if (proxy)
        {
            document.Settings.ProxyMode = (int)ProxyMode.Custom;
            document.Settings.ProxyUrl = "invalid://proxy";
        }
        else
        {
            document.Settings.DnsProvider = (int)DnsProviderMode.Custom;
            document.Settings.CustomDnsServer = "invalid DNS server";
        }
        byte[] encrypted = SettingsBackupService.Encrypt(document, "test password");

        Assert.Throws<FormatException>(() => SettingsBackupService.Decrypt(encrypted, "test password"));
    }

    [Theory]
    [InlineData("file.bin", "file.bin.backup")]
    [InlineData("file.bin.part", "other.bin")]
    [InlineData("one/file.bin", "two/file.bin")]
    public void DownloadTargets_AllowIndependentPaths(string first, string second)
    {
        Utility.ValidateDownloadTargets(new[] { Path.Combine(_root, first), Path.Combine(_root, second) });
    }

    [Theory]
    [InlineData("FILE.bin.part/child.bin")]
    [InlineData("file.bin.part.segments.json.tmp")]
    [InlineData("file.bin.part.segment.anything/child.bin")]
    [InlineData("FILE.bin")]
    public void DownloadTargets_RejectOverlapsRegardlessOfOrder(string conflicting)
    {
        string first = Path.Combine(_root, "file.bin");
        string second = Path.Combine(_root, conflicting);
        Assert.Throws<InvalidDataException>(() => Utility.ValidateDownloadTargets(new[] { second, first }));
    }

    [Fact]
    public void AccountStore_NullOptionalFieldsAreNormalized()
    {
        var store = new CloudAccountStore(Path.Combine(_root, "accounts.json"));
        store.Upsert(new CloudAccountProfile
        {
            DisplayName = "Account", UserName = null!, ServerUrl = null!, SecretKind = null!
        });

        var account = Assert.Single(store.GetAll());
        Assert.Equal(string.Empty, account.UserName);
        Assert.Equal(string.Empty, account.ServerUrl);
        Assert.Equal("token", account.SecretKind);
    }

    [Fact]
    public async Task ChecksumManifest_InvalidPathDoesNotHideValidEntries()
    {
        string file = Path.Combine(_root, "payload.bin");
        await File.WriteAllBytesAsync(file, new byte[] { 1, 2, 3 });
        string state = Path.Combine(_root, "checksums.json");
        var store = new ChecksumManifestStore(state);
        ChecksumManifestEntry valid = await store.RecordFileAsync(file);
        await File.WriteAllTextAsync(state, JsonConvert.SerializeObject(new[]
        {
            new ChecksumManifestEntry { Path = "bad\0path", Sha256 = "bad" }, valid
        }));

        Assert.True(await new ChecksumManifestStore(state).VerifyFileAsync(file));
    }

    [Theory]
    [InlineData("COM¹.txt")]
    [InlineData("LPT².txt")]
    [InlineData("COM³")]
    public void SafePath_ProtectsAdditionalWindowsDeviceNames(string name)
    {
        Assert.Equal("_" + name, Utility.GetSafePathName(name));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }
}
