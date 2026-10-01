using CloudFolderBrowser.Networking;
using System.Security.Cryptography;

namespace CloudFolderBrowser.Tests;

[Collection("Real download integration")]
public sealed class ShareCredentialAuditTests
{
    [Fact]
    public void SavedShareCredentials_AreEncryptedAtRestAndRoundTrip()
    {
        var values = new Dictionary<string, string> { ["https://one.example/s/key"] = "fixture-secret-value" };
        string saved = ShareCredentialCacheCodec.Serialize(values);
        Assert.DoesNotContain("fixture-secret-value", saved);
        Assert.Equal(values, ShareCredentialCacheCodec.Deserialize(saved));
    }

    [Theory]
    [InlineData("https://two.example/s/key")]
    [InlineData("http://one.example/s/key")]
    [InlineData("https://one.example:444/s/key")]
    public void SavedShareCredentials_AreScopedToAuthority(string other)
    {
        var model = new MainFormModel();
        model.RememberSharePassword(new Uri("https://one.example/s/key"), "key", "fixture");
        Assert.Empty(model.GetSavedSharePassword(new Uri(other), "key"));
    }

    [Fact]
    public void LegacyUnscopedCredentials_AreNotAutomaticallySentToAnotherHost()
    {
        var model = new MainFormModel { savedPasswords = ShareCredentialCacheCodec.Deserialize("{\"key\":\"fixture\"}") };
        Assert.Empty(model.GetSavedSharePassword(new Uri("https://new.example/s/key"), "key"));
    }

    [Fact]
    public void SavedShareCredentials_IgnoreNullValues()
    {
        Assert.Empty(ShareCredentialCacheCodec.Deserialize("{\"key\":null}"));
    }

    [Fact]
    public void SavedShareCredentials_PreserveSameAuthorityAndShareIdentity()
    {
        var model = new MainFormModel();
        model.RememberSharePassword(new Uri("https://ONE.example/s/Key?path=/a"), "Key", "fixture");
        Assert.Equal("fixture", model.GetSavedSharePassword(new Uri("https://one.example/s/Key?path=/b"), "Key"));
        Assert.Empty(model.GetSavedSharePassword(new Uri("https://one.example/s/key"), "key"));
    }

    [Fact]
    public void PortableCache_EncryptsForTheCurrentUserAfterRestore()
    {
        var values = new Dictionary<string, string> { ["https://one.example/s/key"] = "fixture" };
        string portable = ShareCredentialCacheCodec.ToPortableJson(ShareCredentialCacheCodec.Serialize(values));
        Assert.StartsWith("{", portable);
        string restored = ShareCredentialCacheCodec.Serialize(ShareCredentialCacheCodec.Deserialize(portable));
        Assert.True(ShareCredentialCacheCodec.IsProtected(restored));
        Assert.Equal(values, ShareCredentialCacheCodec.Deserialize(restored));
    }

    [Fact]
    public void SavedShareCredentials_RejectTamperedCiphertext()
    {
        string cache = ShareCredentialCacheCodec.Serialize(new() { ["key"] = "fixture" });
        int separator = cache.LastIndexOf(':') + 1;
        byte[] cipher = Convert.FromBase64String(cache[separator..]);
        cipher[^1] ^= 1;
        Assert.Throws<CryptographicException>(() => ShareCredentialCacheCodec.Deserialize(cache[..separator] + Convert.ToBase64String(cipher)));
    }

    [Fact]
    public void BackupCapture_ExportsPortableCacheInsideEncryptedBackup()
    {
        string original = Properties.Settings.Default.savedPasswordsJson;
        try
        {
            Properties.Settings.Default.savedPasswordsJson = ShareCredentialCacheCodec.Serialize(
                new() { ["https://one.example/s/key"] = "fixture" });
            SettingsBackupDocument captured = SettingsBackupService.Capture();
            Assert.StartsWith("{", captured.Settings.SavedPasswordsJson);
            byte[] encrypted = SettingsBackupService.Encrypt(captured, "fixture-backup-password");
            var restored = SettingsBackupService.Decrypt(encrypted, "fixture-backup-password");
            Assert.Equal("fixture", ShareCredentialCacheCodec.Deserialize(restored.Settings.SavedPasswordsJson)["https://one.example/s/key"]);
        }
        finally { Properties.Settings.Default.savedPasswordsJson = original; }
    }

    [Fact]
    public void BackupApply_InvalidCacheIsRejectedBeforeChangingSettings()
    {
        string original = Properties.Settings.Default.lastSyncFolderPath;
        try
        {
            Properties.Settings.Default.lastSyncFolderPath = "fixture-before";
            var document = new SettingsBackupDocument
            { Settings = new SettingsBackupSnapshot { LastSyncFolderPath = "fixture-after", SavedPasswordsJson = "not-json" } };
            Assert.Throws<InvalidDataException>(() => SettingsBackupService.Apply(document));
            Assert.Equal("fixture-before", Properties.Settings.Default.lastSyncFolderPath);
        }
        finally { Properties.Settings.Default.lastSyncFolderPath = original; }
    }
}
