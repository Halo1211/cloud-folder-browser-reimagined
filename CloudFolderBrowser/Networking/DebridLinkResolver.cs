using System.Net.Http.Headers;
using System.Net;
using System.Text;
using CloudFolderBrowser.Accounts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CloudFolderBrowser.Networking;

public sealed record DebridResolvedLink(
    Uri DownloadUri,
    string? FileName = null,
    long? FileSize = null,
    string RouteId = "",
    string RouteDisplayName = "");

public interface IDownloadLinkResolver
{
    string DisplayName { get; }
    Task<DebridResolvedLink> ResolveAsync(Uri source, CancellationToken cancellationToken);
}

public interface IAutomaticDownloadLinkResolver : IDownloadLinkResolver
{
    Task<DebridResolvedLink> ResolveAsync(
        CloudFile file,
        Uri source,
        CancellationToken cancellationToken);

    void ReportFailure(CloudFile file, Uri source, Exception exception);

    void ReportSuccess(CloudFile file, Uri source);
}

public sealed class DebridLinkResolver : IDownloadLinkResolver, IDisposable
{
    private readonly CloudAccountProfile _profile;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public string DisplayName => _profile.ProviderName;

    public DebridLinkResolver(CloudAccountProfile profile, HttpClient? client = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!IsSupportedProvider(profile.Provider))
            throw new ArgumentException("The selected profile is not a debrid account.", nameof(profile));
        if (string.IsNullOrWhiteSpace(profile.Secret))
            throw new ArgumentException("The selected debrid account has no API key.", nameof(profile));
        _profile = profile;
        _client = client ?? AppHttpClientFactory.CreateClient(
            TimeSpan.FromSeconds(45), routeKey: profile.Provider.ToString());
        _ownsClient = client == null;
    }

    public static bool IsSupportedProvider(CloudAccountProvider provider) => provider is
        CloudAccountProvider.AllDebrid or
        CloudAccountProvider.RealDebrid or
        CloudAccountProvider.DebridLink or
        CloudAccountProvider.Premiumize or
        CloudAccountProvider.TorBox;

    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }

    public async Task<DebridResolvedLink> ResolveAsync(Uri source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        DebridResolvedLink resolved = _profile.Provider switch
        {
            CloudAccountProvider.AllDebrid => await ResolveAllDebridAsync(source, cancellationToken),
            CloudAccountProvider.RealDebrid => await ResolveRealDebridAsync(source, cancellationToken),
            CloudAccountProvider.DebridLink => await ResolveDebridLinkAsync(source, cancellationToken),
            CloudAccountProvider.Premiumize => await ResolvePremiumizeAsync(source, cancellationToken),
            CloudAccountProvider.TorBox => await ResolveTorBoxAsync(source, cancellationToken),
            _ => throw new NotSupportedException("Unsupported debrid provider.")
        };
        return resolved with
        {
            RouteId = DownloadRouteIds.ForAccount(_profile.Id),
            RouteDisplayName = _profile.ProviderName
        };
    }

    public async Task ValidateAccountAsync(CancellationToken cancellationToken = default)
    {
        string url;
        Func<JToken, string?> errorSelector;
        switch (_profile.Provider)
        {
            case CloudAccountProvider.AllDebrid:
                url = "https://api.alldebrid.com/v4/user";
                errorSelector = json => json["status"]?.Value<string>() == "success" ? null : GetError(json);
                break;
            case CloudAccountProvider.RealDebrid:
                url = "https://api.real-debrid.com/rest/1.0/user";
                errorSelector = _ => null;
                break;
            case CloudAccountProvider.DebridLink:
                url = "https://debrid-link.com/api/v2/account/infos";
                errorSelector = json => json["success"]?.Value<bool>() == true ? null : GetError(json);
                break;
            case CloudAccountProvider.Premiumize:
                url = "https://www.premiumize.me/api/account/info";
                errorSelector = json => json["status"]?.Value<string>() == "success" ? null : GetError(json);
                break;
            case CloudAccountProvider.TorBox:
                url = "https://api.torbox.app/v1/api/user/me";
                errorSelector = json => json["success"]?.Value<bool>() == true ? null : GetError(json);
                break;
            default:
                throw new NotSupportedException("Unsupported debrid provider.");
        }

        using var request = CreateRequest(HttpMethod.Get, url);
        JToken json = await SendAsync(request, cancellationToken);
        string? apiError = errorSelector(json);
        if (!string.IsNullOrWhiteSpace(apiError))
            throw new InvalidOperationException($"{DisplayName}: {apiError}");
    }

    private async Task<DebridResolvedLink> ResolveAllDebridAsync(Uri source, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, "https://api.alldebrid.com/v4/link/unlock");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["link"] = source.AbsoluteUri
        });
        JToken json = await SendAsync(request, cancellationToken);
        if (json["status"]?.Value<string>() != "success")
            throw new InvalidOperationException($"AllDebrid: {GetError(json)}");
        JToken data = json["data"] ?? throw new InvalidDataException("AllDebrid returned no link data.");

        string? direct = data["link"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(direct) && data["delayed"]?.Value<long?>() is long delayedId)
            direct = await PollAllDebridDelayedAsync(delayedId, cancellationToken);
        return CreateResolvedLink(
            direct,
            data["filename"]?.Value<string>(),
            data["filesize"]?.Value<long?>(),
            "AllDebrid");
    }

    private async Task<string> PollAllDebridDelayedAsync(long delayedId, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 12; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            using var request = CreateRequest(HttpMethod.Post, "https://api.alldebrid.com/v4/link/delayed");
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["id"] = delayedId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
            JToken json = await SendAsync(request, cancellationToken);
            if (json["status"]?.Value<string>() != "success")
                throw new InvalidOperationException($"AllDebrid: {GetError(json)}");
            JToken? data = json["data"];
            int status = data?["status"]?.Value<int>() ?? 0;
            string? link = data?["link"]?.Value<string>();
            if (status == 2 && !string.IsNullOrWhiteSpace(link))
                return link;
            if (status == 3)
                throw new InvalidOperationException("AllDebrid could not generate the delayed link.");
        }
        throw new TimeoutException("AllDebrid did not finish generating the link within one minute.");
    }

    private async Task<DebridResolvedLink> ResolveRealDebridAsync(Uri source, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, "https://api.real-debrid.com/rest/1.0/unrestrict/link");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["link"] = source.AbsoluteUri
        });
        JToken json = await SendAsync(request, cancellationToken);
        JToken data = json is JArray array ? array.FirstOrDefault() ?? json : json;
        return CreateResolvedLink(
            data["download"]?.Value<string>(),
            data["filename"]?.Value<string>(),
            data["filesize"]?.Value<long?>(),
            "Real-Debrid");
    }

    private async Task<DebridResolvedLink> ResolveDebridLinkAsync(Uri source, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, "https://debrid-link.com/api/v2/downloader/add");
        request.Content = new StringContent(
            JsonConvert.SerializeObject(new { url = source.AbsoluteUri }),
            Encoding.UTF8,
            "application/json");
        JToken json = await SendAsync(request, cancellationToken);
        if (json["success"]?.Value<bool>() != true)
            throw new InvalidOperationException($"Debrid-Link: {GetError(json)}");
        JToken value = json["value"] ?? throw new InvalidDataException("Debrid-Link returned no link data.");
        JToken data = value is JArray array ? array.FirstOrDefault() ?? value : value;
        return CreateResolvedLink(
            data["downloadUrl"]?.Value<string>(),
            data["name"]?.Value<string>(),
            data["size"]?.Value<long?>(),
            "Debrid-Link");
    }

    private async Task<DebridResolvedLink> ResolvePremiumizeAsync(Uri source, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, "https://www.premiumize.me/api/transfer/directdl");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["src"] = source.AbsoluteUri
        });
        JToken json = await SendAsync(request, cancellationToken);
        if (json["status"]?.Value<string>() != "success")
            throw new InvalidOperationException($"Premiumize.me: {GetError(json)}");

        JArray content = json["content"] as JArray
            ?? throw new InvalidDataException("Premiumize.me returned no direct-download entries.");
        if (content.Count == 0)
            throw new InvalidDataException("Premiumize.me returned an empty direct-download list.");
        if (content.Count > 1)
        {
            throw new InvalidDataException(
                "Premiumize.me expanded this share into multiple files. Open a single-file link, or use a debrid route that can return a ZIP archive.");
        }

        JToken data = content[0];
        string? path = data["path"]?.Value<string>();
        return CreateResolvedLink(
            data["link"]?.Value<string>(),
            string.IsNullOrWhiteSpace(path) ? null : Path.GetFileName(path),
            data["size"]?.Value<long?>(),
            "Premiumize.me");
    }

    private async Task<DebridResolvedLink> ResolveTorBoxAsync(Uri source, CancellationToken cancellationToken)
    {
        await EnsureTorBoxTeraBoxAvailableAsync(source, cancellationToken);

        using var createRequest = CreateRequest(
            HttpMethod.Post,
            "https://api.torbox.app/v1/api/webdl/createwebdownload");
        createRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["link"] = source.AbsoluteUri,
            ["as_queued"] = "false"
        });
        JToken createJson = await SendAsync(createRequest, cancellationToken);
        if (createJson["success"]?.Value<bool>() != true)
            throw new InvalidOperationException($"TorBox: {GetError(createJson)}");

        long? webDownloadId = FindLong(createJson,
            "webdownload_id", "web_download_id", "web_id", "id");
        if (webDownloadId is null)
            throw new InvalidDataException("TorBox did not return a web-download ID.");

        // The official requestdl endpoint requires the token in the query. For
        // TeraBox folder shares, request a ZIP so a single sync item never drops
        // all but the first file.
        bool zipLink = IsTeraBoxHost(source.Host);
        for (int attempt = 0; attempt < 60; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

            string url = "https://api.torbox.app/v1/api/webdl/requestdl"
                + $"?token={Uri.EscapeDataString(_profile.Secret.Trim())}"
                + $"&web_id={webDownloadId.Value}"
                + "&file_id=0"
                + $"&zip_link={zipLink.ToString().ToLowerInvariant()}"
                + "&redirect=false&append_name=true";
            using var request = CreateRequest(HttpMethod.Get, url);
            (JToken json, HttpStatusCode statusCode) = await SendWithStatusAsync(request, cancellationToken);
            if (json["success"]?.Value<bool>() == true)
            {
                string? link = FindString(json, "download_url", "downloadUrl", "link", "url");
                if (!string.IsNullOrWhiteSpace(link))
                {
                    string? name = FindString(createJson, "name", "filename");
                    if (zipLink && !string.IsNullOrWhiteSpace(name) && !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        name += ".zip";
                    return CreateResolvedLink(link, name, null, "TorBox");
                }
            }

            string error = GetError(json);
            if (!IsTorBoxPending(error))
            {
                throw new HttpRequestException(
                    $"TorBox: HTTP {(int)statusCode} — {error}",
                    null,
                    statusCode);
            }
        }

        throw new TimeoutException("TorBox did not finish preparing the download within five minutes.");
    }

    private async Task EnsureTorBoxTeraBoxAvailableAsync(Uri source, CancellationToken cancellationToken)
    {
        if (!IsTeraBoxHost(source.Host))
            return;

        using var request = CreateRequest(HttpMethod.Get, "https://api.torbox.app/v1/api/webdl/hosters");
        JToken json = await SendAsync(request, cancellationToken);
        JToken? hoster = (json["data"] as JArray)?
            .FirstOrDefault(item => item["domains"] is JArray domains
                && domains.Values<string>().Any(domain => !string.IsNullOrWhiteSpace(domain)
                    && (source.Host.Equals(domain, StringComparison.OrdinalIgnoreCase)
                        || source.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))));

        if (hoster == null)
        {
            throw new NotSupportedException(
                "TorBox does not currently list this TeraBox domain as a supported Web Download host.");
        }
        if (hoster["status"]?.Value<bool>() != true)
        {
            throw new InvalidOperationException(
                "TorBox currently reports TeraBox as unavailable. Choose another debrid route or try again after the host status recovers.");
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _profile.Secret.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", "CloudFolderBrowser/1.0");
        return request;
    }

    private async Task<JToken> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        (JToken? json, HttpStatusCode statusCode, string? reasonPhrase) =
            await SendAndParseAsync(request, cancellationToken);
        if ((int)statusCode is < 200 or >= 300)
        {
            string detail = json == null ? reasonPhrase ?? "request failed" : GetError(json);
            throw new HttpRequestException(
                $"{DisplayName}: HTTP {(int)statusCode} — {detail}",
                null,
                statusCode);
        }
        return json ?? throw new InvalidDataException($"{DisplayName} returned an empty or invalid response.");
    }

    private async Task<(JToken Json, HttpStatusCode StatusCode)> SendWithStatusAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        (JToken? json, HttpStatusCode statusCode, string? reasonPhrase) =
            await SendAndParseAsync(request, cancellationToken);
        if (json == null)
        {
            throw new HttpRequestException(
                $"{DisplayName}: HTTP {(int)statusCode} — {reasonPhrase ?? "request failed"}",
                null,
                statusCode);
        }
        return (json, statusCode);
    }

    private async Task<(JToken? Json, HttpStatusCode StatusCode, string? ReasonPhrase)> SendAndParseAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken);
        string content = await response.Content.ReadAsStringAsync(cancellationToken);
        JToken? json = null;
        if (!string.IsNullOrWhiteSpace(content))
        {
            try
            {
                json = JToken.Parse(content);
            }
            catch (JsonReaderException) when (!response.IsSuccessStatusCode)
            {
            }
        }
        return (json, response.StatusCode, response.ReasonPhrase);
    }

    private static DebridResolvedLink CreateResolvedLink(
        string? link,
        string? fileName,
        long? fileSize,
        string providerName)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidDataException($"{providerName} did not return a valid direct download link.");
        }
        return new DebridResolvedLink(uri, fileName, fileSize);
    }

    private static long? FindLong(JToken token, params string[] names)
    {
        foreach (JProperty property in EnumerateProperties(token))
        {
            if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase)
                && property.Value.Type == JTokenType.Integer)
            {
                return property.Value.Value<long>();
            }
        }
        return token["data"]?.Type == JTokenType.Integer ? token["data"]!.Value<long>() : null;
    }

    private static string? FindString(JToken token, params string[] names)
    {
        if (token["data"]?.Type == JTokenType.String)
            return token["data"]!.Value<string>();
        foreach (JProperty property in EnumerateProperties(token))
        {
            if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase)
                && property.Value.Type == JTokenType.String)
            {
                return property.Value.Value<string>();
            }
        }
        return null;
    }

    private static IEnumerable<JProperty> EnumerateProperties(JToken token)
    {
        if (token is JObject obj)
        {
            foreach (JProperty property in obj.Properties())
            {
                yield return property;
                foreach (JProperty child in EnumerateProperties(property.Value))
                    yield return child;
            }
        }
        else if (token is JArray array)
        {
            foreach (JToken item in array)
            {
                foreach (JProperty child in EnumerateProperties(item))
                    yield return child;
            }
        }
    }

    private static bool IsTeraBoxHost(string host)
    {
        string[] domains =
        {
            "terabox.com", "teraboxapp.com", "terabox.app", "1024tera.com",
            "1024terabox.com", "dubox.com", "4funbox.com", "mirrobox.com", "gibibox.com"
        };
        return domains.Any(domain => host.Equals(domain, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTorBoxPending(string error) =>
        error.Contains("not ready", StringComparison.OrdinalIgnoreCase)
        || error.Contains("processing", StringComparison.OrdinalIgnoreCase)
        || error.Contains("queued", StringComparison.OrdinalIgnoreCase)
        || error.Contains("not finished", StringComparison.OrdinalIgnoreCase)
        || error.Contains("still downloading", StringComparison.OrdinalIgnoreCase)
        || error.Contains("download is not", StringComparison.OrdinalIgnoreCase)
        || error.Contains("try again", StringComparison.OrdinalIgnoreCase);

    private static string GetError(JToken json) =>
        json["error"]?["message"]?.Value<string>()
        ?? json["error"]?.Value<string>()
        ?? json["detail"]?.Value<string>()
        ?? json["message"]?.Value<string>()
        ?? json["ERR"]?.Value<string>()
        ?? "the service rejected the request";
}
