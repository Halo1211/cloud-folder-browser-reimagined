using CloudFolderBrowser.Providers;

namespace CloudFolderBrowser.Tests;

public sealed class AllsyncAuthenticationServiceTests
{
    [Fact]
    public async Task RejectedSavedPassword_IsChallengedAndRetriedOutsideTheUi()
    {
        var loadedPasswords = new List<string>();
        var broker = new RecordingCredentialBroker("correct-password");
        var service = CreateService(
            savedPassword: "stale-password",
            load: password =>
            {
                loadedPasswords.Add(password);
                return password == "correct-password" ? 200 : 401;
            });

        AllsyncLoadResult result = await service.LoadAsync(
            "https://example.allsync.com/s/share",
            onlyCheck: false,
            broker);

        Assert.True(result.Success);
        Assert.Equal(new[] { "stale-password", "correct-password" }, loadedPasswords);
        CloudProviderCredentialRequest request = Assert.Single(broker.Requests);
        Assert.Equal("share-id", request.ShareId);
        Assert.Equal(1, request.Attempt);
        Assert.True(request.SavedCredentialRejected);
    }

    [Fact]
    public async Task RepeatedUnauthorizedResponses_RequestAnotherCredential()
    {
        var broker = new RecordingCredentialBroker("wrong", "correct");
        var service = CreateService(
            savedPassword: string.Empty,
            load: password => password == "correct" ? 200 : 401);

        AllsyncLoadResult result = await service.LoadAsync(
            "https://example.allsync.com/s/share",
            onlyCheck: false,
            broker);

        Assert.True(result.Success);
        Assert.Equal(2, broker.Requests.Count);
        Assert.Equal(new[] { 1, 2 }, broker.Requests.Select(item => item.Attempt));
        Assert.All(broker.Requests, request => Assert.False(request.SavedCredentialRejected));
    }

    [Fact]
    public async Task CancelledCredentialChallenge_StopsWithoutAnotherRequest()
    {
        int loadCount = 0;
        var service = CreateService(
            savedPassword: string.Empty,
            load: _ =>
            {
                loadCount++;
                return 401;
            });

        AllsyncLoadResult result = await service.LoadAsync(
            "https://example.allsync.com/s/share",
            onlyCheck: false,
            new RecordingCredentialBroker((string?)null));

        Assert.False(result.Success);
        Assert.True(result.Cancelled);
        Assert.Equal(1, loadCount);
    }

    [Theory]
    [InlineData(403, "Forbidden")]
    [InlineData(500, "Server Error")]
    [InlineData(504, "Connection Timeout")]
    public async Task ProviderStatus_IsMappedWithoutAWinFormsDependency(
        int statusCode,
        string expectedMessage)
    {
        var service = CreateService(string.Empty, _ => statusCode);

        AllsyncLoadResult result = await service.LoadAsync(
            "https://example.allsync.com/s/share",
            onlyCheck: false,
            new RecordingCredentialBroker());

        Assert.False(result.Success);
        Assert.Contains(expectedMessage, result.ErrorMessage);
    }

    private static AllsyncAuthenticationService CreateService(
        string savedPassword,
        Func<string, int> load) =>
        new(
            (_, _) => Task.FromResult(true),
            (_, password, _) => Task.FromResult(load(password)),
            () => "share-id",
            () => savedPassword);

    private sealed class RecordingCredentialBroker : ICloudProviderCredentialBroker
    {
        private readonly Queue<string?> _passwords;

        public RecordingCredentialBroker(params string?[] passwords) =>
            _passwords = new Queue<string?>(passwords);

        public List<CloudProviderCredentialRequest> Requests { get; } = new();

        public Task<string?> RequestPasswordAsync(
            CloudProviderCredentialRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(_passwords.Count == 0 ? null : _passwords.Dequeue());
        }
    }
}
