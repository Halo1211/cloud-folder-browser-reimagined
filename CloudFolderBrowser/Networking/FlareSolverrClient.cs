using Newtonsoft.Json;
using System.Net.Http.Json;

namespace CloudFolderBrowser.Networking
{
    public sealed class FlareSolverrException : Exception
    {
        public FlareSolverrException(string message) : base(message)
        {
        }

        public FlareSolverrException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    public sealed class FlareSolverrCookie
    {
        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("value")]
        public string Value { get; set; } = string.Empty;

        [JsonProperty("domain")]
        public string Domain { get; set; } = string.Empty;

        [JsonProperty("path")]
        public string Path { get; set; } = "/";

        [JsonProperty("secure")]
        public bool Secure { get; set; }
    }

    public sealed class CloudflareSession
    {
        public string UserAgent { get; }
        public IReadOnlyList<FlareSolverrCookie> Cookies { get; }

        public CloudflareSession(string userAgent, IEnumerable<FlareSolverrCookie> cookies)
        {
            UserAgent = userAgent ?? string.Empty;
            Cookies = (cookies ?? Array.Empty<FlareSolverrCookie>()).ToArray();
        }

        public string GetCookieHeader(Uri requestUri)
        {
            if (requestUri == null)
                return string.Empty;

            return string.Join("; ", Cookies
                .Where(cookie => !string.IsNullOrWhiteSpace(cookie.Name))
                .Where(cookie => DomainMatches(requestUri.Host, cookie.Domain))
                .Where(cookie => PathMatches(requestUri.AbsolutePath, cookie.Path))
                .Where(cookie => !cookie.Secure || requestUri.Scheme == Uri.UriSchemeHttps)
                .Select(cookie => $"{cookie.Name}={cookie.Value}"));
        }

        private static bool DomainMatches(string host, string cookieDomain)
        {
            if (string.IsNullOrWhiteSpace(cookieDomain))
                return true;

            var normalizedDomain = cookieDomain.TrimStart('.');
            return host.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + normalizedDomain, StringComparison.OrdinalIgnoreCase);
        }

        private static bool PathMatches(string requestPath, string cookiePath)
        {
            if (string.IsNullOrWhiteSpace(cookiePath) || cookiePath == "/")
                return true;

            if (!requestPath.StartsWith(cookiePath, StringComparison.Ordinal))
                return false;
            if (requestPath.Length == cookiePath.Length || cookiePath.EndsWith('/'))
                return true;
            return requestPath[cookiePath.Length] == '/';
        }
    }

    public sealed class FlareSolverrClient : IDisposable
    {
        private readonly HttpClient _client;
        private readonly Uri _apiUri;
        private readonly TimeSpan _timeout;

        public FlareSolverrClient(string endpoint, TimeSpan timeout)
        {
            _apiUri = NormalizeEndpoint(endpoint);
            _timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(60) : timeout;
            _client = AppHttpClientFactory.CreateClient(
                _timeout + TimeSpan.FromSeconds(10), routeKey: "FlareSolverr");
        }

        public async Task<CloudflareSession> GetSessionAsync(Uri targetUrl, CancellationToken cancellationToken)
        {
            if (targetUrl == null || !targetUrl.IsAbsoluteUri)
                throw new ArgumentException("A valid absolute target URL is required.", nameof(targetUrl));

            var request = new
            {
                cmd = "request.get",
                url = targetUrl.AbsoluteUri,
                maxTimeout = Math.Clamp((int)_timeout.TotalMilliseconds, 1000, int.MaxValue),
                returnOnlyCookies = true
            };

            HttpResponseMessage response;
            try
            {
                response = await _client.PostAsJsonAsync(_apiUri, request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                throw new FlareSolverrException(
                    $"Cannot connect to FlareSolverr at {_apiUri}. Make sure the service is running.", ex);
            }

            using (response)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new FlareSolverrException(
                        $"FlareSolverr returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
                }

                FlareSolverrResponse? result;
                try
                {
                    result = JsonConvert.DeserializeObject<FlareSolverrResponse>(json);
                }
                catch (JsonException ex)
                {
                    throw new FlareSolverrException("FlareSolverr returned invalid JSON.", ex);
                }

                if (result == null || !string.Equals(result.Status, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new FlareSolverrException(
                        string.IsNullOrWhiteSpace(result?.Message)
                            ? "FlareSolverr could not create a browser session."
                            : result.Message);
                }

                if (result.Solution == null || string.IsNullOrWhiteSpace(result.Solution.UserAgent))
                    throw new FlareSolverrException("FlareSolverr response did not contain a browser User-Agent.");

                return new CloudflareSession(result.Solution.UserAgent, result.Solution.Cookies);
            }
        }

        public void Dispose()
        {
            _client.Dispose();
        }

        private static Uri NormalizeEndpoint(string endpoint)
        {
            endpoint = string.IsNullOrWhiteSpace(endpoint)
                ? "http://127.0.0.1:8191"
                : endpoint.Trim();

            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new FlareSolverrException("FlareSolverr URL must be an absolute HTTP or HTTPS URL.");
            }

            var builder = new UriBuilder(uri);
            var path = builder.Path.TrimEnd('/');
            builder.Path = path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                ? path
                : path + "/v1";
            return builder.Uri;
        }

        private sealed class FlareSolverrResponse
        {
            [JsonProperty("status")]
            public string Status { get; set; } = string.Empty;

            [JsonProperty("message")]
            public string Message { get; set; } = string.Empty;

            [JsonProperty("solution")]
            public FlareSolverrSolution? Solution { get; set; }
        }

        private sealed class FlareSolverrSolution
        {
            [JsonProperty("userAgent")]
            public string UserAgent { get; set; } = string.Empty;

            [JsonProperty("cookies")]
            public List<FlareSolverrCookie> Cookies { get; set; } = new();
        }
    }
}
