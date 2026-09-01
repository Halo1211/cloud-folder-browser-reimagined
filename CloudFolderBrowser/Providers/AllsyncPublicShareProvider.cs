using System.Net;
using System.Web;
using CloudFolderBrowser.Networking;
using Newtonsoft.Json;
using WebDAVClient;

namespace CloudFolderBrowser.Providers;

/// <summary>
/// Portable AllSync public-share browser. Interactive password requests are
/// delegated through the provider credential broker, never through WinForms.
/// </summary>
public sealed class AllsyncPublicShareProvider : ICloudFolderProviderV2
{
    private readonly Func<Dictionary<string, string>> _loadPasswords;
    private readonly Action<Dictionary<string, string>> _savePasswords;

    public AllsyncPublicShareProvider()
        : this(LoadPasswords, SavePasswords)
    {
    }

    internal AllsyncPublicShareProvider(
        Func<Dictionary<string, string>> loadPasswords,
        Action<Dictionary<string, string>> savePasswords)
    {
        _loadPasswords = loadPasswords;
        _savePasswords = savePasswords;
    }

    public string Id => "allsync";
    public string DisplayName => "AllSync";
    public CloudServiceType ServiceType => CloudServiceType.Allsync;
    public CloudProviderCapabilities Capabilities =>
        CloudProviderCapabilities.Browse |
        CloudProviderCapabilities.DirectDownload |
        CloudProviderCapabilities.Resume |
        CloudProviderCapabilities.Authentication |
        CloudProviderCapabilities.HealthCheck;
    public CloudProviderExecutionMode ExecutionMode => CloudProviderExecutionMode.Portable;
    public CloudProviderHealthDefinition? HealthCheck => new(
        "Allsync", "AllSync", new Uri("https://allsync.com/"));

    public bool CanHandle(Uri uri) =>
        uri.Scheme is "http" or "https"
        && (uri.Host.Equals("allsync.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".allsync.com", StringComparison.OrdinalIgnoreCase));

    public async Task<CloudFolder> LoadAsync(
        CloudProviderLoadContext context,
        string url,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ShareAddress share = ParseAddress(url);
        CloudflareSession? cloudflare = await CreateCloudflareSessionAsync(
            share.Source, cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> passwords = _loadPasswords();
        passwords.TryGetValue(share.ShareId, out string? password);
        password ??= string.Empty;
        int attempt = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Client client = CreateClient(share, password, cloudflare);
            try
            {
                WebDAVClient.Model.Item[] items = (await client.ListShared(share.RequestedPath, 999)
                    .ConfigureAwait(false))?.ToArray() ?? Array.Empty<WebDAVClient.Model.Item>();
                cancellationToken.ThrowIfCancellationRequested();
                if (items.Length == 0)
                    throw new InvalidDataException("The AllSync share returned no files or folders.");

                if (!string.IsNullOrEmpty(password))
                {
                    passwords[share.ShareId] = password;
                    _savePasswords(passwords);
                }
                progress?.Report(1);
                CloudFolder root = BuildTree(share, items);
                root.CalculateFolderSize();
                progress?.Report(2);
                return root;
            }
            catch (WebDAVClient.Helpers.WebDAVException ex) when (ex.GetHttpCode() == 401)
            {
                attempt++;
                if (context.CredentialBroker == null)
                {
                    throw new UnauthorizedAccessException(
                        "This AllSync share requires a password and no credential broker was supplied.", ex);
                }
                string? supplied = await context.CredentialBroker.RequestPasswordAsync(
                    new CloudProviderCredentialRequest(
                        Id,
                        DisplayName,
                        share.ShareId,
                        attempt,
                        attempt == 1 && !string.IsNullOrEmpty(password)),
                    cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(supplied))
                    throw new OperationCanceledException("AllSync password entry was cancelled.", cancellationToken);
                password = supplied;
            }
            catch (WebDAVClient.Helpers.WebDAVException ex)
            {
                int status = ex.GetHttpCode();
                throw new HttpRequestException(
                    $"AllSync WebDAV returned HTTP {status}.", ex,
                    Enum.IsDefined(typeof(HttpStatusCode), status) ? (HttpStatusCode)status : null);
            }
        }
    }

    private static ShareAddress ParseAddress(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? source)
            || source.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Invalid AllSync URL.", nameof(url));
        }
        string[] segments = source.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int marker = Array.FindIndex(segments, item => item.Equals("s", StringComparison.OrdinalIgnoreCase));
        if (marker < 0 || marker + 1 >= segments.Length)
            throw new ArgumentException("The AllSync URL has no public share key.", nameof(url));
        string shareId = Uri.UnescapeDataString(segments[marker + 1]);
        string path = HttpUtility.UrlDecode(HttpUtility.ParseQueryString(source.Query)["path"] ?? "/")
            .Replace('\\', '/');
        if (!path.StartsWith('/'))
            path = "/" + path;
        if (!path.EndsWith('/'))
            path += "/";
        return new ShareAddress(
            source,
            source.GetLeftPart(UriPartial.Authority),
            shareId,
            path);
    }

    private static Client CreateClient(
        ShareAddress share,
        string password,
        CloudflareSession? cloudflare)
    {
        var credential = new NetworkCredential(share.ShareId, string.IsNullOrEmpty(password) ? "null" : password);
        Client client = NetworkClientAdapters.CreateWebDavClient(credential);
        client.Server = share.Authority;
        client.BasePath = "/public.php/webdav/";
        client.UserAgent = cloudflare?.UserAgent
            ?? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CloudFolderBrowser/1.0";
        var headers = new Dictionary<string, string>
        {
            ["X-Requested-With"] = "XMLHttpRequest",
            ["Accept"] = "*/*",
            ["Accept-Language"] = "en-US,en;q=0.5"
        };
        string? cookie = cloudflare?.GetCookieHeader(new Uri(share.Authority));
        if (!string.IsNullOrWhiteSpace(cookie))
            headers["Cookie"] = cookie;
        client.CustomHeaders = headers;
        return client;
    }

    private static CloudFolder BuildTree(
        ShareAddress share,
        IReadOnlyList<WebDAVClient.Model.Item> items)
    {
        string rootName = share.RequestedPath == "/"
            ? share.ShareId
            : Utility.GetSafePathName(share.RequestedPath.Trim('/').Split('/').Last());
        var root = new CloudFolder(rootName, DateTime.MinValue, DateTime.MinValue, 0)
        {
            Path = share.RequestedPath,
            OriginalString = share.Source.AbsoluteUri,
            PublicKey = share.ShareId
        };
        var folders = new Dictionary<string, CloudFolder>(StringComparer.OrdinalIgnoreCase)
        {
            [NormalizeFolderPath(root.Path)] = root
        };

        foreach (WebDAVClient.Model.Item item in items.Where(item => item.IsCollection))
        {
            string path = NormalizeFolderPath(DecodeItemPath(item.Href));
            if (path == root.Path || folders.ContainsKey(path))
                continue;
            folders[path] = new CloudFolder(
                Utility.GetSafePathName(item.DisplayName),
                DateTime.MinValue,
                item.LastModified ?? DateTime.MinValue,
                0) { Path = path };
        }
        foreach ((string path, CloudFolder folder) in folders
            .Where(pair => !ReferenceEquals(pair.Value, root))
            .OrderBy(pair => pair.Key.Count(character => character == '/')))
        {
            string parentPath = ParentPath(path);
            if (folders.TryGetValue(parentPath, out CloudFolder? parent))
                parent.AddSubfolder(folder);
        }

        foreach (WebDAVClient.Model.Item item in items.Where(item => !item.IsCollection))
        {
            string path = DecodeItemPath(item.Href);
            string parentPath = ParentPath(path);
            if (!folders.TryGetValue(parentPath, out CloudFolder? parent))
                continue;
            string encodedPath = item.Href.Replace("/public.php/webdav", "/download?path=", StringComparison.OrdinalIgnoreCase);
            string directUrl = $"{share.Authority}/s/{Uri.EscapeDataString(share.ShareId)}{encodedPath}";
            var file = new CloudFile(
                Utility.GetSafePathName(item.DisplayName),
                DateTime.MinValue,
                item.LastModified ?? DateTime.MinValue,
                item.ContentLength ?? 0)
            {
                Path = path,
                PublicUrl = new Uri(directUrl)
            };
            parent.AddFile(file);
            parent.SizeTopDirectoryOnly += file.Size;
        }
        return root;
    }

    private static string DecodeItemPath(string href) =>
        HttpUtility.UrlDecode(href).Replace("/public.php/webdav", "", StringComparison.OrdinalIgnoreCase)
            .Replace('\\', '/');

    private static string NormalizeFolderPath(string path) =>
        "/" + path.Trim('/').Trim() + (path.Trim('/').Length == 0 ? string.Empty : "/");

    private static string ParentPath(string path)
    {
        string normalized = "/" + path.Trim('/');
        int slash = normalized.LastIndexOf('/');
        return slash <= 0 ? "/" : normalized[..(slash + 1)];
    }

    private static Dictionary<string, string> LoadPasswords()
    {
        try
        {
            return JsonConvert.DeserializeObject<Dictionary<string, string>>(
                Properties.Settings.Default.savedPasswordsJson ?? string.Empty)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static void SavePasswords(Dictionary<string, string> passwords)
    {
        Properties.Settings.Default.savedPasswordsJson = JsonConvert.SerializeObject(passwords);
        Properties.Settings.Default.Save();
    }

    private static async Task<CloudflareSession?> CreateCloudflareSessionAsync(
        Uri source,
        CancellationToken cancellationToken)
    {
        if (!Properties.Settings.Default.flareSolverrEnabled)
            return null;
        using var solver = new FlareSolverrClient(
            Properties.Settings.Default.flareSolverrUrl,
            TimeSpan.FromSeconds(Math.Clamp(
                Properties.Settings.Default.flareSolverrTimeoutSeconds, 10, 300)));
        return await solver.GetSessionAsync(source, cancellationToken).ConfigureAwait(false);
    }

    private sealed record ShareAddress(Uri Source, string Authority, string ShareId, string RequestedPath);
}
