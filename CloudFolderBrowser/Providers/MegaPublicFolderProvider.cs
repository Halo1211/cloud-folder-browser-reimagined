using CG.Web.MegaApiClient;
using CloudFolderBrowser.Accounts;
using CloudFolderBrowser.Networking;
using Newtonsoft.Json;

namespace CloudFolderBrowser.Providers;

public sealed class MegaPublicFolderProvider : ICloudFolderProviderV2
{
    public string Id => "mega";
    public string DisplayName => "MEGA";
    public CloudServiceType ServiceType => CloudServiceType.Mega;
    public CloudProviderCapabilities Capabilities =>
        CloudProviderCapabilities.Browse |
        CloudProviderCapabilities.DirectDownload |
        CloudProviderCapabilities.Import |
        CloudProviderCapabilities.Authentication |
        CloudProviderCapabilities.HealthCheck;
    public CloudAccountProvider? AccountProvider => CloudAccountProvider.Mega;
    public CloudProviderHealthDefinition? HealthCheck => new(
        "Mega", "MEGA", new Uri("https://g.api.mega.co.nz/"));

    public bool CanHandle(Uri uri) =>
        uri.Scheme is "http" or "https"
        && (uri.Host.Equals("mega.nz", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".mega.nz", StringComparison.OrdinalIgnoreCase));

    public async Task<CloudFolder> LoadAsync(
        CloudProviderLoadContext context,
        string url,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string normalizedUrl = NormalizeLegacyUrl(url);
        if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out Uri? source) || !CanHandle(source))
            throw new ArgumentException("This is not a supported MEGA public link.", nameof(url));

        progress?.Report(0);
        MegaApiClient client = NetworkClientAdapters.CreateMegaClient();
        await LoginForPublicLinkAsync(client, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        INode[] nodes = await Task.Run(
            () => client.GetNodesFromLink(source, out _).ToArray(),
            cancellationToken).ConfigureAwait(false);
        if (nodes.Length == 0)
            throw new InvalidDataException("The MEGA share did not return any nodes.");

        CloudFolder root = IsFileLink(source)
            ? CreateFileShare(nodes[0], source)
            : CreateFolderShare(nodes, source);
        root.CalculateFolderSize();
        progress?.Report(2);
        return root;
    }

    private static async Task LoginForPublicLinkAsync(
        MegaApiClient client,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string serializedToken = Properties.Settings.Default.loginTokenMega ?? string.Empty;
        bool useSavedSession = Properties.Settings.Default.loginedMega
            && !string.IsNullOrWhiteSpace(serializedToken);
        if (useSavedSession)
        {
            try
            {
                MegaApiClient.LogonSessionToken? token =
                    JsonConvert.DeserializeObject<MegaApiClient.LogonSessionToken>(
                        serializedToken,
                        new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto });
                if (token != null)
                {
                    await client.LoginAsync(token).ConfigureAwait(false);
                    return;
                }
            }
            catch (Exception ex) when (ex is JsonException or ApiException or HttpRequestException)
            {
                // A public link remains usable anonymously when the saved account
                // session is stale. Account activation is handled separately.
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        await client.LoginAnonymousAsync().ConfigureAwait(false);
    }

    private static CloudFolder CreateFileShare(INode node, Uri source)
    {
        (string publicKey, string decryptionKey) = ParseShareKeys(source);
        var root = new CloudFolder(node.Name, node.CreationDate, DateTime.MinValue, 0)
        {
            Path = "/",
            OriginalString = source.AbsoluteUri,
            PublicKey = publicKey,
            PublicDecryptionKey = decryptionKey
        };
        var file = new CloudFile(
            Utility.GetSafePathName(node.Name),
            node.CreationDate,
            node.ModificationDate ?? DateTime.MinValue,
            node.Size)
        {
            Path = "/" + Utility.GetSafePathName(node.Name),
            MegaNode = node,
            PublicUrl = BuildFileLink(source.AbsoluteUri, node.Id)
        };
        root.AddFile(file);
        root.SizeTopDirectoryOnly = file.Size;
        return root;
    }

    private static CloudFolder CreateFolderShare(IReadOnlyList<INode> nodes, Uri source)
    {
        var nodeIds = nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        INode rootNode = nodes.FirstOrDefault(node =>
            node.Type == NodeType.Directory
            && (node.ParentId == null || !nodeIds.Contains(node.ParentId)))
            ?? nodes.First();
        (string publicKey, string decryptionKey) = ParseShareKeys(source);
        var root = new CloudFolder(
            Utility.GetSafePathName(rootNode.Name),
            rootNode.CreationDate,
            DateTime.MinValue,
            0)
        {
            Path = "/",
            MegaNode = rootNode,
            OriginalString = source.AbsoluteUri,
            PublicKey = publicKey,
            PublicDecryptionKey = decryptionKey
        };

        var folders = new Dictionary<string, CloudFolder>(StringComparer.Ordinal)
        {
            [rootNode.Id] = root
        };
        foreach (INode node in nodes.Where(node =>
            node.Type == NodeType.Directory && node.Id != rootNode.Id))
        {
            folders[node.Id] = new CloudFolder(
                Utility.GetSafePathName(node.Name),
                node.CreationDate,
                DateTime.MinValue,
                node.Size)
            {
                MegaNode = node
            };
        }

        foreach (INode node in nodes.Where(node =>
            node.Type == NodeType.Directory && node.Id != rootNode.Id))
        {
            if (node.ParentId != null
                && folders.TryGetValue(node.ParentId, out CloudFolder? parent)
                && folders.TryGetValue(node.Id, out CloudFolder? child))
            {
                parent.AddSubfolder(child);
            }
        }
        AssignFolderPaths(root);

        foreach (INode node in nodes.Where(node => node.Type == NodeType.File))
        {
            if (node.ParentId == null || !folders.TryGetValue(node.ParentId, out CloudFolder? parent))
                continue;
            string name = Utility.GetSafePathName(node.Name);
            var file = new CloudFile(
                name,
                node.CreationDate,
                node.ModificationDate ?? DateTime.MinValue,
                node.Size)
            {
                Path = parent.Path + name,
                MegaNode = node,
                PublicUrl = BuildFileLink(source.AbsoluteUri, node.Id)
            };
            parent.AddFile(file);
            parent.SizeTopDirectoryOnly += file.Size;
        }
        return root;
    }

    private static void AssignFolderPaths(CloudFolder folder)
    {
        foreach (CloudFolder child in folder.Subfolders.Cast<CloudFolder>())
        {
            child.Path = folder.Path + child.Name + "/";
            AssignFolderPaths(child);
        }
    }

    internal static Uri BuildFileLink(string shareUrl, string nodeId)
    {
        if (!Uri.TryCreate(shareUrl, UriKind.Absolute, out Uri? shareUri))
            throw new ArgumentException("Invalid MEGA share URL.", nameof(shareUrl));
        if (IsFileLink(shareUri))
            return shareUri;

        int fragmentIndex = shareUrl.IndexOf('#');
        if (fragmentIndex < 0)
            throw new ArgumentException("The MEGA folder link has no decryption fragment.", nameof(shareUrl));
        string beforeFragment = shareUrl[..fragmentIndex];
        string fragment = shareUrl[(fragmentIndex + 1)..].TrimEnd('/');
        return new Uri($"{beforeFragment}#{fragment}/file/{Uri.EscapeDataString(nodeId)}");
    }

    internal static string NormalizeLegacyUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;
        return url.Replace("/#F!", "/folder/", StringComparison.OrdinalIgnoreCase)
            .Replace("#F!", "folder/", StringComparison.OrdinalIgnoreCase)
            .Replace("!", "#", StringComparison.Ordinal);
    }

    private static bool IsFileLink(Uri source) =>
        source.AbsolutePath.Contains("/file/", StringComparison.OrdinalIgnoreCase);

    private static (string PublicKey, string DecryptionKey) ParseShareKeys(Uri source)
    {
        string[] segments = source.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int kindIndex = Array.FindIndex(segments, segment =>
            segment.Equals("folder", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("file", StringComparison.OrdinalIgnoreCase));
        string publicKey = kindIndex >= 0 && kindIndex + 1 < segments.Length
            ? Uri.UnescapeDataString(segments[kindIndex + 1])
            : string.Empty;
        string decryptionKey = source.Fragment.TrimStart('#').Split('/', 2)[0];
        return (publicKey, decryptionKey);
    }
}
