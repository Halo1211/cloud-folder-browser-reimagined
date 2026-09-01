using System.Globalization;
using System.Net;
using HtmlAgilityPack;
using CloudFolderBrowser.Accounts;
using CloudFolderBrowser.Networking;
using YandexDiskSharp;
using YandexDiskSharp.Models;
using HtmlDocument = HtmlAgilityPack.HtmlDocument;

namespace CloudFolderBrowser.Providers;

public sealed class YandexPublicFolderProvider : ICloudFolderProviderV2
{
    private const int PageSize = 200;

    public string Id => "yandex";
    public string DisplayName => "Yandex Disk";
    public CloudServiceType ServiceType => CloudServiceType.Yadisk;
    public CloudProviderCapabilities Capabilities =>
        CloudProviderCapabilities.Browse |
        CloudProviderCapabilities.DirectDownload |
        CloudProviderCapabilities.Import |
        CloudProviderCapabilities.Authentication |
        CloudProviderCapabilities.HealthCheck;
    public CloudAccountProvider? AccountProvider => CloudAccountProvider.YandexDisk;
    public CloudProviderHealthDefinition? HealthCheck => new(
        "Yadisk", "Yandex Disk", new Uri("https://cloud-api.yandex.net/v1/disk"));

    public bool CanHandle(Uri uri) =>
        uri.Scheme is "http" or "https"
        && (uri.Host.Equals("yadi.sk", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("disk.yandex.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".yandex.ru", StringComparison.OrdinalIgnoreCase));

    public async Task<CloudFolder> LoadAsync(
        CloudProviderLoadContext context,
        string url,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? source) || !CanHandle(source))
            throw new ArgumentException("This is not a supported Yandex Disk public link.", nameof(url));

        progress?.Report(0);
        RestClient client = NetworkClientAdapters.CreateYandexClient();
        ResourceList rootPage = await GetPageAsync(
            client, source.AbsoluteUri, path: null, offset: 0, cancellationToken).ConfigureAwait(false);
        var root = new CloudFolder(rootPage) { OriginalString = source.AbsoluteUri };
        await AppendRemainingPagesAsync(
            client, rootPage, root, source.AbsoluteUri, path: null, cancellationToken).ConfigureAwait(false);

        foreach (CloudFolder subfolder in root.Subfolders.Cast<CloudFolder>().ToArray())
        {
            await LoadFolderAsync(
                client, source.AbsoluteUri, subfolder, cancellationToken).ConfigureAwait(false);
        }

        root.CalculateFolderSize();
        progress?.Report(2);
        return root;
    }

    private static async Task LoadFolderAsync(
        RestClient client,
        string publicKey,
        CloudFolder folder,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResourceList firstPage = await GetPageAsync(
            client, publicKey, folder.Path, 0, cancellationToken).ConfigureAwait(false);

        folder.Subfolders.Clear();
        folder.Files.Clear();
        folder.SizeTopDirectoryOnly = 0;
        AddResources(folder, firstPage.Items);
        await AppendRemainingPagesAsync(
            client, firstPage, folder, publicKey, folder.Path, cancellationToken).ConfigureAwait(false);

        foreach (CloudFolder child in folder.Subfolders.Cast<CloudFolder>().ToArray())
            await LoadFolderAsync(client, publicKey, child, cancellationToken).ConfigureAwait(false);
    }

    private static async Task AppendRemainingPagesAsync(
        RestClient client,
        ResourceList firstPage,
        CloudFolder folder,
        string publicKey,
        string? path,
        CancellationToken cancellationToken)
    {
        int offset = firstPage.Items.Count;
        while (offset < firstPage.Total)
        {
            ResourceList page = await GetPageAsync(
                client, publicKey, path, offset, cancellationToken).ConfigureAwait(false);
            if (page.Items.Count == 0)
                break;
            AddResources(folder, page.Items);
            offset += page.Items.Count;
        }
    }

    private static void AddResources(CloudFolder folder, IEnumerable<Resource> resources)
    {
        foreach (Resource item in resources)
        {
            if (item.Type == YandexDiskSharp.Type.dir)
            {
                folder.AddSubfolder(new CloudFolder(item));
                continue;
            }

            var file = new CloudFile(item);
            folder.AddFile(file);
            folder.SizeTopDirectoryOnly += file.Size;
        }
    }

    private static Task<ResourceList> GetPageAsync(
        RestClient client,
        string publicKey,
        string? path,
        int offset,
        CancellationToken cancellationToken) =>
        Task.Run(
            () => client.GetPublicResource(
                publicKey,
                path: path,
                limit: PageSize,
                offset: offset),
            cancellationToken);
}

public sealed class H5AiIndexProvider : ICloudFolderProviderV2
{
    private const int MaximumPages = 2000;

    public string Id => "h5ai";
    public string DisplayName => "h5ai";
    public CloudServiceType ServiceType => CloudServiceType.h5ai;
    public CloudProviderCapabilities Capabilities =>
        CloudProviderCapabilities.Browse | CloudProviderCapabilities.DirectDownload;

    public bool CanHandle(Uri uri) =>
        uri.Scheme is "http" or "https"
        && uri.AbsoluteUri.Contains("h5ailink", StringComparison.OrdinalIgnoreCase);

    public async Task<CloudFolder> LoadAsync(
        CloudProviderLoadContext context,
        string url,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? source) || !CanHandle(source))
            throw new ArgumentException("This is not a supported h5ai index link.", nameof(url));

        progress?.Report(0);
        using HttpClient client = WebIndexProviderUtilities.CreateClient("h5ai");
        var root = new CloudFolder(
            WebIndexProviderUtilities.GetFolderName(source, "h5ai index"),
            DateTime.MinValue,
            DateTime.MinValue,
            0)
        {
            Path = "/",
            OriginalString = source.AbsoluteUri
        };
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await LoadPageAsync(client, source, source, root, visited, cancellationToken)
            .ConfigureAwait(false);
        root.CalculateFolderSize();
        progress?.Report(2);
        return root;
    }

    private static async Task LoadPageAsync(
        HttpClient client,
        Uri rootUri,
        Uri pageUri,
        CloudFolder folder,
        ISet<string> visited,
        CancellationToken cancellationToken)
    {
        WebIndexProviderUtilities.RegisterPage(pageUri, visited, MaximumPages);
        HtmlDocument document = await WebIndexProviderUtilities.DownloadHtmlAsync(
            client, pageUri, cancellationToken).ConfigureAwait(false);
        HtmlNode? table = document.DocumentNode.SelectSingleNode("//body//table");
        if (table == null)
            throw new InvalidDataException($"The h5ai page '{pageUri}' does not contain a file table.");

        foreach (HtmlNode row in table.SelectNodes(".//tr") ?? Enumerable.Empty<HtmlNode>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            HtmlNodeCollection? cells = row.SelectNodes("./td");
            if (cells == null || cells.Count < 4)
                continue;

            string itemType = cells[0].SelectSingleNode(".//*[@alt]")
                ?.GetAttributeValue("alt", string.Empty) ?? string.Empty;
            HtmlNode? linkNode = cells[1].SelectSingleNode(".//a");
            string href = linkNode?.GetAttributeValue("href", string.Empty) ?? string.Empty;
            if (linkNode == null || string.IsNullOrWhiteSpace(href) || href is "../" or "./")
                continue;

            Uri itemUri = new(pageUri, href);
            if (!WebIndexProviderUtilities.IsWithinRoot(rootUri, itemUri))
                continue;

            string name = WebIndexProviderUtilities.GetLinkName(linkNode, itemUri);
            DateTime.TryParse(cells[2].InnerText, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out DateTime modified);
            if (itemType.Equals("folder", StringComparison.OrdinalIgnoreCase))
            {
                var subfolder = new CloudFolder(name, modified, modified, 0)
                {
                    Path = WebIndexProviderUtilities.CombineCloudPath(folder.Path, name)
                };
                folder.AddSubfolder(subfolder);
                await LoadPageAsync(client, rootUri, itemUri, subfolder, visited, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (itemType.Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                long size = WebIndexProviderUtilities.ParseSizeToBytes(cells[3].InnerText);
                var file = new CloudFile(name, modified, modified, Math.Max(0, size))
                {
                    Path = WebIndexProviderUtilities.CombineCloudPath(folder.Path, name),
                    PublicUrl = itemUri,
                    HasKnownSize = size >= 0
                };
                folder.AddFile(file);
                folder.SizeTopDirectoryOnly += file.Size;
            }
        }
    }
}

public sealed class TheTroveIndexProvider : ICloudFolderProviderV2
{
    private const int MaximumPages = 5000;
    private static readonly HashSet<string> OversizedRootFolders =
        new(StringComparer.OrdinalIgnoreCase) { "Browse", "Books", "Assets" };

    public string Id => "thetrove";
    public string DisplayName => "The Trove";
    public CloudServiceType ServiceType => CloudServiceType.TheTrove;
    public CloudProviderCapabilities Capabilities =>
        CloudProviderCapabilities.Browse |
        CloudProviderCapabilities.DirectDownload |
        CloudProviderCapabilities.HealthCheck;
    public CloudProviderHealthDefinition? HealthCheck => new(
        "TheTrove", "The Trove", new Uri("https://thetrove.is/"));

    public bool CanHandle(Uri uri) =>
        uri.Scheme is "http" or "https"
        && uri.Host.Equals("thetrove.is", StringComparison.OrdinalIgnoreCase);

    public async Task<CloudFolder> LoadAsync(
        CloudProviderLoadContext context,
        string url,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? source) || !CanHandle(source))
            throw new ArgumentException("This is not a supported The Trove link.", nameof(url));

        Uri rootUri = NormalizeRootUri(source);
        string folderName = WebIndexProviderUtilities.GetFolderName(rootUri, string.Empty);
        if (string.IsNullOrWhiteSpace(folderName))
            throw new NotSupportedException("Use a path to a specific The Trove folder.");
        if (OversizedRootFolders.Contains(folderName))
            throw new NotSupportedException(
                "This folder is too large to load. Use a more specific The Trove subfolder.");

        progress?.Report(0);
        using HttpClient client = WebIndexProviderUtilities.CreateClient("TheTrove");
        var root = new CloudFolder(folderName, DateTime.MinValue, DateTime.MinValue, 0)
        {
            Path = "/",
            OriginalString = source.AbsoluteUri
        };
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await LoadPageAsync(client, rootUri, rootUri, root, visited, cancellationToken)
            .ConfigureAwait(false);
        root.CalculateFolderSize();
        progress?.Report(2);
        return root;
    }

    private static async Task LoadPageAsync(
        HttpClient client,
        Uri rootUri,
        Uri pageUri,
        CloudFolder folder,
        ISet<string> visited,
        CancellationToken cancellationToken)
    {
        WebIndexProviderUtilities.RegisterPage(pageUri, visited, MaximumPages);
        HtmlDocument document = await WebIndexProviderUtilities.DownloadHtmlAsync(
            client, pageUri, cancellationToken).ConfigureAwait(false);
        HtmlNodeCollection? rows = document.DocumentNode.SelectNodes("//*[@id='list']/tbody/tr");
        if (rows == null)
            throw new InvalidDataException($"The Trove page '{pageUri}' does not contain a file list.");

        foreach (HtmlNode row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HtmlNodeCollection? cells = row.SelectNodes("./td");
            HtmlNode? link = cells?.Count >= 3 ? cells[0].SelectSingleNode("./a") : null;
            string href = link?.GetAttributeValue("href", string.Empty) ?? string.Empty;
            if (cells == null || cells.Count < 3 || link == null
                || string.IsNullOrWhiteSpace(href) || href is "../" or "./")
            {
                continue;
            }

            Uri itemUri = new(pageUri, href);
            if (!WebIndexProviderUtilities.IsWithinRoot(rootUri, itemUri))
                continue;
            string name = WebIndexProviderUtilities.GetLinkName(link, itemUri);
            DateTime.TryParse(cells[2].InnerText, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out DateTime modified);
            if (href.EndsWith("/", StringComparison.Ordinal))
            {
                var subfolder = new CloudFolder(name, DateTime.MinValue, modified, 0)
                {
                    Path = WebIndexProviderUtilities.CombineCloudPath(folder.Path, name)
                };
                folder.AddSubfolder(subfolder);
                await LoadPageAsync(client, rootUri, itemUri, subfolder, visited, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                long size = WebIndexProviderUtilities.ParseSizeToBytes(cells[1].InnerText);
                var file = new CloudFile(name, DateTime.MinValue, modified, Math.Max(0, size))
                {
                    Path = WebIndexProviderUtilities.CombineCloudPath(folder.Path, name),
                    PublicUrl = itemUri,
                    HasKnownSize = size >= 0
                };
                folder.AddFile(file);
                folder.SizeTopDirectoryOnly += file.Size;
            }
        }
    }

    private static Uri NormalizeRootUri(Uri source)
    {
        var builder = new UriBuilder(source) { Fragment = string.Empty };
        if (builder.Path.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase))
            builder.Path = builder.Path[..^"index.html".Length];
        else if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
            builder.Path += "/";
        return builder.Uri;
    }
}

internal static class WebIndexProviderUtilities
{
    public static HttpClient CreateClient(string routeKey)
    {
        HttpClient client = AppHttpClientFactory.CreateClient(
            TimeSpan.FromSeconds(45), routeKey: routeKey);
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CloudFolderBrowser/1.0");
        return client;
    }

    public static async Task<HtmlDocument> DownloadHtmlAsync(
        HttpClient client,
        Uri uri,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync(uri, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var document = new HtmlDocument();
        document.LoadHtml(html);
        return document;
    }

    public static void RegisterPage(Uri pageUri, ISet<string> visited, int maximumPages)
    {
        string key = pageUri.GetComponents(
            UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
            UriFormat.SafeUnescaped);
        if (!visited.Add(key))
            throw new InvalidDataException($"The web index contains a folder loop at '{pageUri}'.");
        if (visited.Count > maximumPages)
            throw new InvalidDataException(
                $"The web index exceeds the safety limit of {maximumPages} pages.");
    }

    public static bool IsWithinRoot(Uri rootUri, Uri itemUri)
    {
        if (!rootUri.Scheme.Equals(itemUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !rootUri.Host.Equals(itemUri.Host, StringComparison.OrdinalIgnoreCase)
            || rootUri.Port != itemUri.Port)
        {
            return false;
        }

        string rootPath = Uri.UnescapeDataString(rootUri.AbsolutePath);
        if (!rootPath.EndsWith("/", StringComparison.Ordinal))
            rootPath += "/";
        string itemPath = Uri.UnescapeDataString(itemUri.AbsolutePath);
        return itemPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase);
    }

    public static string GetFolderName(Uri uri, string fallback)
    {
        string path = uri.AbsolutePath.TrimEnd('/');
        string segment = path.Length == 0
            ? string.Empty
            : path[(path.LastIndexOf('/') + 1)..];
        string name = Uri.UnescapeDataString(segment);
        return string.IsNullOrWhiteSpace(name) ? fallback : name;
    }

    public static string GetLinkName(HtmlNode link, Uri itemUri)
    {
        string name = link.GetAttributeValue("title", string.Empty);
        if (string.IsNullOrWhiteSpace(name))
            name = HtmlEntity.DeEntitize(link.InnerText).Trim();
        return string.IsNullOrWhiteSpace(name)
            ? GetFolderName(itemUri, "Unnamed item")
            : name;
    }

    public static string CombineCloudPath(string parentPath, string name)
    {
        string safeName = name.Replace('\\', '_').Replace('/', '_').Trim();
        return parentPath.TrimEnd('/') + "/" + safeName;
    }

    public static long ParseSizeToBytes(string? size)
    {
        string normalized = HtmlEntity.DeEntitize(size ?? string.Empty).Trim();
        string[] parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0
            || !double.TryParse(parts[0], NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out double value)
            || value < 0)
        {
            return -1;
        }

        long multiplier = parts.Length < 2 ? 1 : parts[1].ToUpperInvariant() switch
        {
            "B" => 1L,
            "KIB" => 1024L,
            "MIB" => 1024L * 1024L,
            "GIB" => 1024L * 1024L * 1024L,
            "TIB" => 1024L * 1024L * 1024L * 1024L,
            "KB" => 1000L,
            "MB" => 1000L * 1000L,
            "GB" => 1000L * 1000L * 1000L,
            "TB" => 1000L * 1000L * 1000L * 1000L,
            _ => 1L
        };
        try
        {
            return checked((long)Math.Round(value * multiplier, MidpointRounding.AwayFromZero));
        }
        catch (OverflowException)
        {
            return -1;
        }
    }
}
