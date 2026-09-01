using CloudFolderBrowser.Accounts;
using CloudFolderBrowser.Networking;

namespace CloudFolderBrowser.Providers;

public static class CloudProviderContractVersions
{
    public const int V1 = 1;
    public const int V2 = 2;
    public const int Current = V2;
}

public enum CloudProviderExecutionMode
{
    Portable,
    HostIntegrated
}

public sealed record CloudProviderHealthDefinition(
    string RouteKey,
    string DisplayName,
    Uri Endpoint);

public sealed record CloudProviderCredentialRequest(
    string ProviderId,
    string ProviderDisplayName,
    string ShareId,
    int Attempt,
    bool SavedCredentialRejected);

/// <summary>
/// UI-independent boundary for an interactive provider credential challenge.
/// Portable providers and authentication services can request a secret without
/// referencing WinForms or a particular password dialog.
/// </summary>
public interface ICloudProviderCredentialBroker
{
    Task<string?> RequestPasswordAsync(
        CloudProviderCredentialRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Narrow host boundary used by providers that still depend on application UI state.
/// New plugins should normally use <see cref="CloudProviderExecutionMode.Portable"/>.
/// </summary>
public interface ICloudProviderHost
{
    Task<CloudFolder> LoadHostIntegratedAsync(
        string providerId,
        string url,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class CloudProviderLoadContext
{
    public ICloudProviderHost? Host { get; }
    public ICloudProviderCredentialBroker? CredentialBroker { get; }

    public CloudProviderLoadContext(
        ICloudProviderHost? host = null,
        ICloudProviderCredentialBroker? credentialBroker = null)
    {
        Host = host;
        CredentialBroker = credentialBroker;
    }

    public ICloudProviderHost RequireHost(string providerId) =>
        Host ?? throw new NotSupportedException(
            $"Provider '{providerId}' requires an application host and cannot run portably.");
}

/// <summary>
/// Versioned provider contract. It adds optional account, resolver and health
/// integration while the original descriptor and folder-provider contracts remain supported.
/// </summary>
public interface ICloudProviderV2 : ICloudProviderDescriptor
{
    int ContractVersion => CloudProviderContractVersions.V2;
    CloudProviderExecutionMode ExecutionMode => CloudProviderExecutionMode.Portable;
    CloudAccountProvider? AccountProvider => null;
    CloudProviderHealthDefinition? HealthCheck => null;

    Task ValidateAccountAsync(
        CloudAccountProfile profile,
        CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException(
            $"Provider '{DisplayName}' does not support account validation through Provider API v2."));

    IDownloadLinkResolver? CreateLinkResolver(CloudAccountProfile profile) => null;
}

public interface ICloudFolderProviderV2 : ICloudProviderV2
{
    Task<CloudFolder> LoadAsync(
        CloudProviderLoadContext context,
        string url,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
}
