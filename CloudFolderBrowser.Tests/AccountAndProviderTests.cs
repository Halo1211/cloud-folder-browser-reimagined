using CloudFolderBrowser.Accounts;
using CloudFolderBrowser.Providers;

namespace CloudFolderBrowser.Tests;

public sealed class AccountAndProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "cfb-account-tests-" + Guid.NewGuid().ToString("N"));

    public AccountAndProviderTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void AccountStore_EncryptsSecretsAndSwitchesActiveProfile()
    {
        string path = Path.Combine(_root, "accounts.json");
        var store = new CloudAccountStore(path);
        var first = new CloudAccountProfile
        {
            Provider = CloudAccountProvider.YandexDisk,
            DisplayName = "Personal",
            Secret = "token-that-must-not-be-plaintext"
        };
        var second = new CloudAccountProfile
        {
            Provider = CloudAccountProvider.YandexDisk,
            DisplayName = "Archive",
            Secret = "another-secret"
        };

        store.Upsert(first);
        store.Upsert(second);

        string persisted = File.ReadAllText(path);
        Assert.DoesNotContain(first.Secret, persisted);
        Assert.DoesNotContain(second.Secret, persisted);
        Assert.Equal(second.Id, store.GetActive(CloudAccountProvider.YandexDisk)?.Id);
        Assert.Equal(first.Secret, store.GetAll().Single(profile => profile.Id == first.Id).Secret);

        store.SetActive(CloudAccountProvider.YandexDisk, first.Id);
        Assert.Equal(first.Id, store.GetActive(CloudAccountProvider.YandexDisk)?.Id);
    }

    [Fact]
    public void AccountStore_RemovingActiveProfilePromotesNewestReplacement()
    {
        var store = new CloudAccountStore(Path.Combine(_root, "accounts.json"));
        var first = new CloudAccountProfile { Provider = CloudAccountProvider.Mega, DisplayName = "A", Secret = "a" };
        var second = new CloudAccountProfile { Provider = CloudAccountProvider.Mega, DisplayName = "B", Secret = "b" };
        store.Upsert(first);
        store.Upsert(second);

        store.Remove(second.Id);

        Assert.Equal(first.Id, store.GetActive(CloudAccountProvider.Mega)?.Id);
    }

    [Fact]
    public void AccountStore_PreservesCorruptFileBeforeNextWrite()
    {
        string path = Path.Combine(_root, "accounts.json");
        File.WriteAllText(path, "{not-json");
        var store = new CloudAccountStore(path);

        Assert.Empty(store.GetAll());
        Assert.Single(Directory.GetFiles(_root, "accounts.json.corrupt-*"));

        store.Upsert(new CloudAccountProfile
        {
            Provider = CloudAccountProvider.WebDav,
            DisplayName = "Recovered account",
            Secret = "password"
        });

        Assert.Single(store.GetAll());
        Assert.Single(Directory.GetFiles(_root, "accounts.json.corrupt-*"));
    }

    [Theory]
    [InlineData("https://www.dropbox.com/scl/fi/abc/report.pdf?rlkey=key&dl=0", "dl=1")]
    [InlineData("https://www.dropbox.com/s/example/archive.zip?raw=1", "dl=1")]
    public void DropboxProvider_NormalizesPublicLinks(string input, string expectedQuery)
    {
        var provider = new DropboxPublicFileProvider();
        Uri result = provider.GetDownloadUri(new Uri(input));

        Assert.Contains(expectedQuery, result.Query);
        Assert.DoesNotContain("raw=", result.Query);
    }

    [Theory]
    [InlineData("https://drive.google.com/file/d/abc123/view", "https://drive.google.com/uc?export=download&id=abc123")]
    [InlineData("https://docs.google.com/document/d/doc123/edit", "https://docs.google.com/document/d/doc123/export?format=docx")]
    public void GoogleDriveProvider_NormalizesPublicFileLinks(string input, string expected)
    {
        var provider = new GoogleDrivePublicFileProvider();
        Assert.Equal(expected, provider.GetDownloadUri(new Uri(input)).AbsoluteUri);
    }

    [Fact]
    public void GoogleDriveProvider_RejectsPublicFolderWithoutOauth()
    {
        var provider = new GoogleDrivePublicFileProvider();
        Assert.Throws<NotSupportedException>(() =>
            provider.GetDownloadUri(new Uri("https://drive.google.com/drive/folders/abc123")));
    }

    [Theory]
    [InlineData("https://www.dropbox.com/s/example/file.zip", CloudServiceType.Dropbox)]
    [InlineData("https://drive.google.com/file/d/abc/view", CloudServiceType.GoogleDrive)]
    [InlineData("https://www.terabox.com/s/1example", CloudServiceType.TeraBox)]
    [InlineData("https://mediafire.com/file/example", CloudServiceType.Other)]
    public void Registry_RecognizesNewPublicProviders(string url, CloudServiceType expected)
    {
        Assert.Equal(expected, CloudProviderRegistry.Default.Resolve(url)?.ServiceType);
    }

    [Theory]
    [InlineData("https://www.terabox.com/s/1example")]
    [InlineData("https://1024terabox.com/s/1example")]
    [InlineData("https://www.dubox.com/s/1example")]
    public async Task TeraBoxProvider_PreservesShareForDebridRouting(string url)
    {
        var provider = new TeraBoxPublicShareProvider();

        CloudFolder folder = await provider.LoadAsync(url);

        CloudFile file = Assert.Single(folder.Files);
        Assert.True(file.RequiresLinkResolver);
        Assert.Equal(url, file.PublicUrl.AbsoluteUri);
        Assert.EndsWith(".zip", file.Name, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }
}
