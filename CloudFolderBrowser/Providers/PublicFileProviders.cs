using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Web;
using CloudFolderBrowser.Accounts;
using CloudFolderBrowser.Networking;

namespace CloudFolderBrowser.Providers;

public abstract class PublicFileProviderBase : ICloudFolderProvider, ICloudFolderProviderV2
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract CloudServiceType ServiceType { get; }
    public virtual CloudAccountProvider? AccountProvider => null;
    public virtual CloudProviderHealthDefinition? HealthCheck => null;
    public CloudProviderCapabilities Capabilities =>
        CloudProviderCapabilities.Browse |
        CloudProviderCapabilities.DirectDownload |
        CloudProviderCapabilities.Resume |
        CloudProviderCapabilities.Authentication |
        (HealthCheck == null
            ? CloudProviderCapabilities.None
            : CloudProviderCapabilities.HealthCheck);

    public abstract bool CanHandle(Uri uri);
    public abstract Uri GetDownloadUri(Uri source);
    protected abstract string GetFallbackName(Uri source);

    public async Task<CloudFolder> LoadAsync(
        string url,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? source) || !CanHandle(source))
            throw new ArgumentException($"This is not a supported {DisplayName} link.", nameof(url));

        progress?.Report(0);
        Uri downloadUri = GetDownloadUri(source);
        string name = GetFallbackName(source);
        long size = 0;
        bool hasKnownSize = false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CloudFolderBrowser/1.0");
            using HttpClient metadataClient = AppHttpClientFactory.CreateClient(
                TimeSpan.FromSeconds(25), routeKey: ServiceType.ToString());
            using HttpResponseMessage response = await metadataClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            name = GetContentDispositionName(response.Content.Headers.ContentDisposition) ?? name;
            if (response.Content.Headers.ContentLength is long reportedSize)
            {
                size = Math.Max(0, reportedSize);
                hasKnownSize = true;
            }
            downloadUri = response.RequestMessage?.RequestUri ?? downloadUri;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Some public hosts block metadata probes while allowing the actual
            // browser-style download. Keep the normalized URL and let the
            // downloader report a precise error if the transfer is attempted.
        }

        name = SanitizeFileName(name);
        if (string.IsNullOrWhiteSpace(name))
            name = $"{DisplayName} download";
        var root = new CloudFolder(DisplayName + " share", DateTime.MinValue, DateTime.MinValue, 0)
        {
            Path = "/",
            OriginalString = source.AbsoluteUri
        };
        var file = new CloudFile(name, DateTime.MinValue, DateTime.MinValue, Math.Max(0, size))
        {
            Path = "/" + name,
            PublicUrl = downloadUri,
            HasKnownSize = hasKnownSize
        };
        root.AddFile(file);
        root.SizeTopDirectoryOnly = file.Size;
        root.CalculateFolderSize();
        progress?.Report(2);
        return root;
    }

    Task<CloudFolder> ICloudFolderProviderV2.LoadAsync(
        CloudProviderLoadContext context,
        string url,
        IProgress<int>? progress,
        CancellationToken cancellationToken) =>
        LoadAsync(url, progress, cancellationToken);

    private static string? GetContentDispositionName(ContentDispositionHeaderValue? disposition)
    {
        string? name = disposition?.FileNameStar ?? disposition?.FileName;
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim().Trim('"');
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        return name.Trim().TrimEnd('.');
    }
}

public sealed class DropboxPublicFileProvider : PublicFileProviderBase
{
    public override string Id => "dropbox";
    public override string DisplayName => "Dropbox";
    public override CloudServiceType ServiceType => CloudServiceType.Dropbox;
    public override CloudAccountProvider? AccountProvider => CloudAccountProvider.Dropbox;
    public override CloudProviderHealthDefinition? HealthCheck => new(
        "Dropbox", "Dropbox", new Uri("https://api.dropboxapi.com/2/check/user"));

    public override bool CanHandle(Uri uri) =>
        uri.Host.Equals("dropbox.com", StringComparison.OrdinalIgnoreCase)
        || uri.Host.EndsWith(".dropbox.com", StringComparison.OrdinalIgnoreCase)
        || uri.Host.Equals("dropboxusercontent.com", StringComparison.OrdinalIgnoreCase)
        || uri.Host.EndsWith(".dropboxusercontent.com", StringComparison.OrdinalIgnoreCase);

    public override Uri GetDownloadUri(Uri source)
    {
        if (source.Host.EndsWith("dropboxusercontent.com", StringComparison.OrdinalIgnoreCase))
            return source;
        var builder = new UriBuilder(source);
        var query = HttpUtility.ParseQueryString(builder.Query);
        query.Remove("raw");
        query["dl"] = "1";
        builder.Query = query.ToString();
        return builder.Uri;
    }

    protected override string GetFallbackName(Uri source)
    {
        bool folderShare = source.AbsolutePath.Contains("/scl/fo/", StringComparison.OrdinalIgnoreCase)
            || source.AbsolutePath.Contains("/sh/", StringComparison.OrdinalIgnoreCase);
        if (folderShare)
            return "Dropbox share.zip";
        string segment = Uri.UnescapeDataString(source.Segments.LastOrDefault()?.Trim('/') ?? string.Empty);
        return string.IsNullOrWhiteSpace(Path.GetExtension(segment)) ? "Dropbox download" : segment;
    }
}

public sealed class GoogleDrivePublicFileProvider : PublicFileProviderBase
{
    private static readonly Regex IdPath = new(@"/(?:file|document|spreadsheets|presentation)/d/([^/]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public override string Id => "google-drive";
    public override string DisplayName => "Google Drive";
    public override CloudServiceType ServiceType => CloudServiceType.GoogleDrive;
    public override CloudAccountProvider? AccountProvider => CloudAccountProvider.GoogleDrive;
    public override CloudProviderHealthDefinition? HealthCheck => new(
        "GoogleDrive", "Google Drive", new Uri("https://www.googleapis.com/drive/v3/about"));

    public override bool CanHandle(Uri uri) =>
        uri.Host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase)
        || uri.Host.Equals("docs.google.com", StringComparison.OrdinalIgnoreCase)
        || uri.Host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase);

    public override Uri GetDownloadUri(Uri source)
    {
        if (source.AbsolutePath.Contains("/folders/", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "Google Drive folder browsing needs a configured Google OAuth account. Public file links are supported now.");
        }

        Match match = IdPath.Match(source.AbsolutePath);
        string? id = match.Success ? match.Groups[1].Value : HttpUtility.ParseQueryString(source.Query)["id"];
        if (string.IsNullOrWhiteSpace(id))
        {
            if (source.Host.EndsWith("googleusercontent.com", StringComparison.OrdinalIgnoreCase))
                return source;
            throw new NotSupportedException("Could not find a Google Drive file ID in this link.");
        }

        if (source.Host.Equals("docs.google.com", StringComparison.OrdinalIgnoreCase))
        {
            string kind = match.Success ? match.Groups[0].Value.Split('/', StringSplitOptions.RemoveEmptyEntries)[0] : string.Empty;
            string format = kind.ToLowerInvariant() switch
            {
                "document" => "docx",
                "spreadsheets" => "xlsx",
                "presentation" => "pptx",
                _ => "pdf"
            };
            return new Uri($"https://docs.google.com/{kind}/d/{Uri.EscapeDataString(id)}/export?format={format}");
        }

        return new Uri($"https://drive.google.com/uc?export=download&id={Uri.EscapeDataString(id)}");
    }

    protected override string GetFallbackName(Uri source)
    {
        Match match = IdPath.Match(source.AbsolutePath);
        if (source.Host.Equals("docs.google.com", StringComparison.OrdinalIgnoreCase) && match.Success)
        {
            string kind = match.Groups[0].Value.Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
            return kind.ToLowerInvariant() switch
            {
                "document" => "Google document.docx",
                "spreadsheets" => "Google spreadsheet.xlsx",
                "presentation" => "Google presentation.pptx",
                _ => "Google Drive download"
            };
        }
        return "Google Drive download";
    }
}

public sealed class TeraBoxPublicShareProvider : ICloudFolderProvider, ICloudFolderProviderV2
{
    private static readonly HashSet<string> SupportedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "terabox.com",
        "teraboxapp.com",
        "terabox.app",
        "1024tera.com",
        "1024terabox.com",
        "dubox.com",
        "4funbox.com",
        "mirrobox.com",
        "gibibox.com"
    };

    public string Id => "terabox";
    public string DisplayName => "TeraBox";
    public CloudServiceType ServiceType => CloudServiceType.TeraBox;
    public CloudProviderCapabilities Capabilities =>
        CloudProviderCapabilities.Browse |
        CloudProviderCapabilities.Resume |
        CloudProviderCapabilities.HealthCheck;
    public CloudProviderHealthDefinition? HealthCheck => new(
        "TeraBox", "TeraBox", new Uri("https://www.terabox.com/"));

    public bool CanHandle(Uri uri) =>
        uri.Scheme is "http" or "https"
        && SupportedHosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));

    public Task<CloudFolder> LoadAsync(
        string url,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? source) || !CanHandle(source))
            throw new ArgumentException("This is not a supported TeraBox share link.", nameof(url));

        progress?.Report(0);
        const string name = "TeraBox share.zip";
        var root = new CloudFolder("TeraBox share", DateTime.MinValue, DateTime.MinValue, 0)
        {
            Path = "/",
            OriginalString = source.AbsoluteUri
        };
        root.AddFile(new CloudFile(name, DateTime.MinValue, DateTime.MinValue, 0)
        {
            Path = "/" + name,
            PublicUrl = source,
            RequiresLinkResolver = true,
            HasKnownSize = false
        });
        root.CalculateFolderSize();
        progress?.Report(2);
        return Task.FromResult(root);
    }

    Task<CloudFolder> ICloudFolderProviderV2.LoadAsync(
        CloudProviderLoadContext context,
        string url,
        IProgress<int>? progress,
        CancellationToken cancellationToken) =>
        LoadAsync(url, progress, cancellationToken);
}

public sealed class GenericHttpFileProvider : PublicFileProviderBase
{
    public override string Id => "generic-http";
    public override string DisplayName => "Web link";
    public override CloudServiceType ServiceType => CloudServiceType.Other;

    public override bool CanHandle(Uri uri) => uri.Scheme is "http" or "https";

    public override Uri GetDownloadUri(Uri source) => source;

    protected override string GetFallbackName(Uri source)
    {
        string segment = Uri.UnescapeDataString(source.Segments.LastOrDefault()?.Trim('/') ?? string.Empty);
        return string.IsNullOrWhiteSpace(segment) ? "Web download" : segment;
    }
}
