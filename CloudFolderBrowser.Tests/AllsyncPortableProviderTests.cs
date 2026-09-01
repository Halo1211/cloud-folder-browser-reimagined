using System.Net;
using System.Text;
using CloudFolderBrowser.Providers;

namespace CloudFolderBrowser.Tests;

[Collection("Real download integration")]
public sealed class AllsyncPortableProviderTests : IDisposable
{
    private readonly bool _originalEnabled = Properties.Settings.Default.portableAllsyncEnabled;
    private readonly string _originalPasswords = Properties.Settings.Default.savedPasswordsJson;

    [Fact]
    public async Task PortableProvider_ChallengesForPasswordAndBuildsTreeWithoutMainForm()
    {
        Properties.Settings.Default.portableAllsyncEnabled = true;
        Properties.Settings.Default.savedPasswordsJson = "{}";
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response>
                <d:href>/public.php/webdav/Library/</d:href>
                <d:propstat><d:prop>
                  <d:displayname>Library</d:displayname>
                  <d:resourcetype><d:collection /></d:resourcetype>
                </d:prop></d:propstat>
              </d:response>
              <d:response>
                <d:href>/public.php/webdav/Library/manual.pdf</d:href>
                <d:propstat><d:prop>
                  <d:displayname>manual.pdf</d:displayname>
                  <d:getcontentlength>1234</d:getcontentlength>
                  <d:getlastmodified>Mon, 31 Aug 2026 10:00:00 GMT</d:getlastmodified>
                  <d:resourcetype />
                </d:prop></d:propstat>
              </d:response>
            </d:multistatus>
            """;
        string validAuthorization = "Basic " + Convert.ToBase64String(
            Encoding.ASCII.GetBytes("share-id:correct-password"));
        await using var server = new LoopbackDownloadServer((request, _) =>
        {
            if (request.Headers.GetValueOrDefault("Authorization") != validAuthorization)
                return new LoopbackResponse { StatusCode = HttpStatusCode.Unauthorized };
            return new LoopbackResponse
            {
                StatusCode = (HttpStatusCode)207,
                ContentType = "application/xml; charset=utf-8",
                Body = Encoding.UTF8.GetBytes(xml)
            };
        });
        var broker = new PasswordBroker("correct-password");
        var provider = new AllsyncPublicShareProvider(
            () => new Dictionary<string, string>(),
            _ => { });

        CloudFolder root = await provider.LoadAsync(
            new CloudProviderLoadContext(credentialBroker: broker),
            server.Url("s/share-id").AbsoluteUri);

        Assert.Equal("share-id", root.Name);
        CloudFolder library = Assert.IsType<CloudFolder>(Assert.Single(root.Subfolders));
        CloudFile file = Assert.Single(library.Files);
        Assert.Equal("manual.pdf", file.Name);
        Assert.Equal(1234, file.Size);
        Assert.Contains("/s/share-id/download?path=", file.PublicUrl.AbsoluteUri);
        CloudProviderCredentialRequest request = Assert.Single(broker.Requests);
        Assert.Equal("share-id", request.ShareId);
        Assert.Equal(2, server.Requests.Count);
    }

    public void Dispose()
    {
        Properties.Settings.Default.portableAllsyncEnabled = _originalEnabled;
        Properties.Settings.Default.savedPasswordsJson = _originalPasswords;
    }

    private sealed class PasswordBroker : ICloudProviderCredentialBroker
    {
        private readonly string _password;
        public List<CloudProviderCredentialRequest> Requests { get; } = new();

        public PasswordBroker(string password) => _password = password;

        public Task<string?> RequestPasswordAsync(
            CloudProviderCredentialRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult<string?>(_password);
        }
    }
}
