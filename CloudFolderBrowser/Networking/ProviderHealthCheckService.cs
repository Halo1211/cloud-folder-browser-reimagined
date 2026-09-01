using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using CloudFolderBrowser.Accounts;
using CloudFolderBrowser.Providers;

namespace CloudFolderBrowser.Networking;

public enum ProviderHealthState
{
    Online,
    Degraded,
    Unreachable
}

public sealed record ProviderHealthTarget(string RouteKey, string DisplayName, Uri Endpoint);

public sealed record ProviderHealthResult(
    ProviderHealthTarget Target,
    ProviderHealthState State,
    TimeSpan Elapsed,
    HttpStatusCode? StatusCode,
    string RouteDescription,
    string Detail);

public sealed class ProviderHealthCheckService
{
    public static IReadOnlyList<ProviderHealthTarget> CreateTargets(
        IEnumerable<CloudAccountProfile>? accounts = null,
        string? fogLinkAddress = null,
        bool flareSolverrEnabled = false,
        string? flareSolverrAddress = null)
    {
        var targets = CloudProviderRegistry.Default.GetHealthDefinitions()
            .Select(definition => new ProviderHealthTarget(
                definition.RouteKey,
                definition.DisplayName,
                definition.Endpoint))
            .ToList();
        foreach (CloudAccountProfile account in accounts ?? Array.Empty<CloudAccountProfile>())
        {
            if (account.Provider != CloudAccountProvider.WebDav
                || !TryCreateSafeEndpoint(account.ServerUrl, out Uri? endpoint))
            {
                continue;
            }
            string name = string.IsNullOrWhiteSpace(account.DisplayName)
                ? "WebDAV"
                : $"WebDAV — {account.DisplayName.Trim()}";
            targets.Add(new ProviderHealthTarget("WebDav", name, endpoint!));
        }

        if (TryCreateSafeEndpoint(fogLinkAddress, out Uri? fogLink))
            targets.Add(new ProviderHealthTarget("FogLink", "FogLink", fogLink!));
        if (flareSolverrEnabled && TryCreateSafeEndpoint(flareSolverrAddress, out Uri? flareSolverr))
            targets.Add(new ProviderHealthTarget("FlareSolverr", "FlareSolverr", flareSolverr!));

        return targets
            .GroupBy(target => $"{target.DisplayName}\n{target.Endpoint.AbsoluteUri}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    public async Task<IReadOnlyList<ProviderHealthResult>> CheckAllAsync(
        IReadOnlyList<ProviderHealthTarget> targets,
        IProgress<ProviderHealthResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var results = new ProviderHealthResult[targets.Count];
        using var concurrency = new SemaphoreSlim(4, 4);
        Task[] checks = targets.Select((target, index) => Task.Run(async () =>
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ProviderHealthResult result = await CheckAsync(target, cancellationToken).ConfigureAwait(false);
                results[index] = result;
                progress?.Report(result);
            }
            finally
            {
                concurrency.Release();
            }
        }, cancellationToken)).ToArray();
        await Task.WhenAll(checks).ConfigureAwait(false);
        return results;
    }

    public async Task<ProviderHealthResult> CheckAsync(
        ProviderHealthTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var stopwatch = Stopwatch.StartNew();
        string route = "Unavailable";
        try
        {
            NetworkConfiguration configuration = NetworkConfiguration.FromSettings(target.RouteKey);
            configuration.Validate();
            route = DescribeRoute(configuration);
            using HttpClient client = AppHttpClientFactory.CreateClient(
                TimeSpan.FromSeconds(15),
                allowAutoRedirect: true,
                routeKey: target.RouteKey);
            using var request = new HttpRequestMessage(HttpMethod.Head, target.Endpoint);
            request.Headers.TryAddWithoutValidation("User-Agent", "CloudFolderBrowser provider-health");
            using HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            ProviderHealthState state = Classify(response.StatusCode);
            return new ProviderHealthResult(
                target,
                state,
                stopwatch.Elapsed,
                response.StatusCode,
                route,
                DescribeStatus(response.StatusCode));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new ProviderHealthResult(
                target,
                ProviderHealthState.Unreachable,
                stopwatch.Elapsed,
                null,
                route,
                SanitizeError(ex));
        }
    }

    public static ProviderHealthState Classify(HttpStatusCode statusCode)
    {
        int code = (int)statusCode;
        return code is 404 or 408 or 425 or 429 || code >= 500
            ? ProviderHealthState.Degraded
            : ProviderHealthState.Online;
    }

    private static string DescribeRoute(NetworkConfiguration configuration) => configuration.ProxyMode switch
    {
        ProxyMode.Direct => "Direct",
        ProxyMode.System => "System proxy",
        ProxyMode.Custom => $"Custom proxy ({configuration.GetValidatedProxyUri().Host}:{configuration.GetValidatedProxyUri().Port})",
        _ => "Unknown"
    };

    private static string DescribeStatus(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Reachable; authentication required",
        HttpStatusCode.MethodNotAllowed => "Reachable; HEAD is not supported",
        HttpStatusCode.NotFound => "Reachable; endpoint returned Not Found",
        (HttpStatusCode)429 => "Provider is rate limiting requests",
        _ when (int)statusCode >= 500 => "Provider returned a server error",
        _ when (int)statusCode >= 400 => "Reachable; API request parameters required",
        _ => "Endpoint responded normally"
    };

    private static string SanitizeError(Exception exception)
    {
        string message = exception switch
        {
            TaskCanceledException => "Timed out after 15 seconds",
            HttpRequestException { InnerException: SocketException socket } => $"Connection failed ({socket.SocketErrorCode})",
            HttpRequestException { InnerException: AuthenticationException } => "TLS authentication failed",
            HttpRequestException http when http.StatusCode.HasValue => $"HTTP connection failed ({(int)http.StatusCode.Value})",
            HttpRequestException { InnerException: { } inner } => $"Connection failed ({inner.GetType().Name})",
            HttpRequestException => "HTTP connection failed",
            FormatException or ArgumentException or InvalidOperationException => "Network route configuration is invalid",
            _ => $"Check failed ({exception.GetType().Name})"
        };
        int lineBreak = message.IndexOfAny(new[] { '\r', '\n' });
        if (lineBreak >= 0)
            message = message[..lineBreak];
        return message;
    }

    private static bool TryCreateSafeEndpoint(string? value, out Uri? endpoint)
    {
        endpoint = null;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }
        endpoint = new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty }.Uri;
        return true;
    }

}
