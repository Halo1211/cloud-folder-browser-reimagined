using System.Collections.Concurrent;
using CloudFolderBrowser.Accounts;
using CloudFolderBrowser.Providers;

namespace CloudFolderBrowser.Networking;

public static class DownloadRouteIds
{
    public const string Automatic = "auto";
    public const string Direct = "direct";
    public const string MegaNative = "mega-native";

    public static string ForAccount(Guid accountId) => $"debrid:{accountId:D}";

    public static bool TryGetAccountId(string? routeId, out Guid accountId)
    {
        const string prefix = "debrid:";
        accountId = Guid.Empty;
        return routeId?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true
            && Guid.TryParse(routeId[prefix.Length..], out accountId);
    }
}

/// <summary>
/// Selects a direct route first when possible, then rotates through active
/// debrid accounts. State is kept per download job so concurrent files do not
/// influence each other's fallback order.
/// </summary>
public sealed class AutomaticDownloadLinkResolver : IAutomaticDownloadLinkResolver, IDisposable
{
    private readonly IReadOnlyList<CloudAccountProfile> _accounts;
    private readonly Func<CloudAccountProfile, IDownloadLinkResolver> _resolverFactory;
    private readonly ConcurrentDictionary<Guid, RouteState> _states = new();
    private readonly ConcurrentDictionary<Guid, Lazy<IDownloadLinkResolver>> _resolvers = new();
    private readonly ConcurrentDictionary<AccountRouteKey, RouteHealth> _routeHealth = new();
    private readonly ConcurrentDictionary<AccountRouteKey, RoutePerformance> _routePerformance = new();
    private readonly Func<DateTime> _utcNow;
    private int _disposed;

    public string DisplayName => "Automatic";

    public AutomaticDownloadLinkResolver(
        IEnumerable<CloudAccountProfile> accounts,
        Guid? preferredAccountId = null)
        : this(accounts, preferredAccountId, profile =>
            CloudProviderRegistry.Default.CreateLinkResolver(profile)
            ?? throw new NotSupportedException(
                $"No download-link resolver is registered for {profile.ProviderName}."))
    {
    }

    internal AutomaticDownloadLinkResolver(
        IEnumerable<CloudAccountProfile> accounts,
        Guid? preferredAccountId,
        Func<CloudAccountProfile, IDownloadLinkResolver> resolverFactory,
        Func<DateTime>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(resolverFactory);
        _resolverFactory = resolverFactory;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _accounts = accounts
            .Where(account => account.IsActive
                && !string.IsNullOrWhiteSpace(account.Secret)
                && CloudProviderRegistry.Default.SupportsLinkResolver(account.Provider))
            .OrderByDescending(account => account.Id == preferredAccountId)
            .ThenBy(account => account.Provider)
            .ThenBy(account => account.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Task<DebridResolvedLink> ResolveAsync(Uri source, CancellationToken cancellationToken)
    {
        var syntheticFile = new CloudFile("Automatic download", DateTime.MinValue, DateTime.MinValue, 0)
        {
            PublicUrl = source,
            HasKnownSize = false,
            DownloadHistoryId = Guid.NewGuid()
        };
        return ResolveAsync(syntheticFile, source, cancellationToken);
    }

    public async Task<DebridResolvedLink> ResolveAsync(
        CloudFile file,
        Uri source,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(source);

        Guid jobId = file.DownloadHistoryId ??= Guid.NewGuid();
        RouteState state = _states.GetOrAdd(jobId, _ => new RouteState());
        var failures = new List<string>();
        lock (state.Gate)
            state.Candidates ??= BuildCandidates(source.Host);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int candidateIndex;
            lock (state.Gate)
                candidateIndex = state.CandidateIndex;

            bool directAvailable = !file.RequiresLinkResolver;
            if (directAvailable && candidateIndex == 0)
            {
                lock (state.Gate)
                {
                    state.CurrentIsDirect = true;
                    state.CurrentRouteName = "Direct";
                }
                return new DebridResolvedLink(
                    source,
                    RouteId: DownloadRouteIds.Direct,
                    RouteDisplayName: "Direct");
            }

            int accountIndex = candidateIndex - (directAvailable ? 1 : 0);
            IReadOnlyList<CloudAccountProfile> candidates;
            lock (state.Gate)
                candidates = state.Candidates!;
            if (accountIndex >= candidates.Count)
            {
                string detail = failures.Count == 0
                    ? "No active compatible debrid account is configured."
                    : string.Join(" | ", failures);
                throw new InvalidOperationException($"Automatic route exhausted all candidates. {detail}");
            }

            CloudAccountProfile account = candidates[accountIndex];
            if (TryGetCooldown(account.Id, source.Host, out TimeSpan remaining))
            {
                failures.Add($"{account.ProviderName}: cooling down for {Math.Ceiling(remaining.TotalSeconds):0}s");
                AdvanceCandidate(state, candidateIndex);
                continue;
            }
            IDownloadLinkResolver resolver = GetResolver(account);
            lock (state.Gate)
            {
                state.CurrentIsDirect = false;
                state.CurrentRouteName = account.ProviderName;
                state.CurrentAccountId = account.Id;
            }

            try
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                DebridResolvedLink resolved = await resolver.ResolveAsync(source, cancellationToken)
                    .ConfigureAwait(false);
                RecordRouteSuccess(account.Id, source.Host);
                RecordPerformanceSuccess(account.Id, source.Host, started.Elapsed);
                return resolved with
                {
                    RouteId = string.IsNullOrWhiteSpace(resolved.RouteId)
                        ? DownloadRouteIds.ForAccount(account.Id)
                        : resolved.RouteId,
                    RouteDisplayName = string.IsNullOrWhiteSpace(resolved.RouteDisplayName)
                        ? account.ProviderName
                        : resolved.RouteDisplayName
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException
                or InvalidOperationException
                or InvalidDataException
                or TimeoutException)
            {
                failures.Add($"{account.ProviderName}: {SanitizeFailure(ex.Message)}");
                RecordRouteFailure(account.Id, source.Host, ex, immediateCooldown: true);
                RecordPerformanceFailure(account.Id, source.Host);
                AdvanceCandidate(state, candidateIndex);
            }
        }
    }

    public void ReportFailure(CloudFile file, Uri source, Exception exception)
    {
        if (file.DownloadHistoryId is not Guid jobId
            || !_states.TryGetValue(jobId, out RouteState? state))
        {
            return;
        }

        lock (state.Gate)
        {
            state.FailuresOnCandidate++;
            bool accessFailure = exception is HttpRequestException http
                && http.StatusCode is System.Net.HttpStatusCode.Unauthorized
                    or System.Net.HttpStatusCode.Forbidden
                    or System.Net.HttpStatusCode.NotFound
                    or System.Net.HttpStatusCode.TooManyRequests
                    or System.Net.HttpStatusCode.ServiceUnavailable;

            // A direct web/page route should fall back immediately on an access
            // failure. A debrid route gets one re-resolution first because its
            // temporary CDN URL may simply have expired.
            if ((state.CurrentIsDirect && accessFailure) || state.FailuresOnCandidate >= 2)
            {
                state.CandidateIndex++;
                state.FailuresOnCandidate = 0;
            }

            if (!state.CurrentIsDirect && state.CurrentAccountId is Guid accountId)
            {
                RecordRouteFailure(
                    accountId,
                    source.Host,
                    exception,
                    immediateCooldown: state.FailuresOnCandidate == 0);
                RecordPerformanceFailure(accountId, source.Host);
            }
        }
    }

    public void ReportSuccess(CloudFile file, Uri source)
    {
        if (file.DownloadHistoryId is not Guid jobId
            || !_states.TryGetValue(jobId, out RouteState? state))
        {
            return;
        }
        lock (state.Gate)
        {
            if (!state.CurrentIsDirect && state.CurrentAccountId is Guid accountId)
                RecordPerformanceSuccess(accountId, source.Host, TimeSpan.Zero);
        }
    }

    private IReadOnlyList<CloudAccountProfile> BuildCandidates(string sourceHost)
    {
        return _accounts
            .Select((account, index) => new
            {
                Account = account,
                Index = index,
                Score = GetPerformanceScore(account.Id, sourceHost)
            })
            .OrderBy(item => item.Score)
            .ThenBy(item => item.Index)
            .Select(item => item.Account)
            .ToArray();
    }

    private double GetPerformanceScore(Guid accountId, string sourceHost)
    {
        if (!_routePerformance.TryGetValue(RouteKey(accountId, sourceHost), out RoutePerformance? performance))
            return 0;
        lock (performance.Gate)
        {
            return (performance.Failures * 10_000)
                + performance.AverageResolutionMilliseconds
                - Math.Min(1_000, performance.Successes * 25);
        }
    }

    private void RecordPerformanceSuccess(Guid accountId, string sourceHost, TimeSpan resolutionTime)
    {
        RoutePerformance performance = _routePerformance.GetOrAdd(
            RouteKey(accountId, sourceHost), _ => new RoutePerformance());
        lock (performance.Gate)
        {
            performance.Successes++;
            performance.Failures = Math.Max(0, performance.Failures - 1);
            if (resolutionTime > TimeSpan.Zero)
            {
                double sample = resolutionTime.TotalMilliseconds;
                performance.AverageResolutionMilliseconds = performance.AverageResolutionMilliseconds <= 0
                    ? sample
                    : (performance.AverageResolutionMilliseconds * 0.75) + (sample * 0.25);
            }
        }
    }

    private void RecordPerformanceFailure(Guid accountId, string sourceHost)
    {
        RoutePerformance performance = _routePerformance.GetOrAdd(
            RouteKey(accountId, sourceHost), _ => new RoutePerformance());
        lock (performance.Gate)
            performance.Failures = Math.Min(100, performance.Failures + 1);
    }

    private bool TryGetCooldown(Guid accountId, string sourceHost, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        if (!_routeHealth.TryGetValue(RouteKey(accountId, sourceHost), out RouteHealth? health))
            return false;
        lock (health.Gate)
        {
            remaining = health.CooldownUntilUtc - _utcNow();
            return remaining > TimeSpan.Zero;
        }
    }

    private void RecordRouteSuccess(Guid accountId, string sourceHost)
    {
        if (!_routeHealth.TryGetValue(RouteKey(accountId, sourceHost), out RouteHealth? health))
            return;
        lock (health.Gate)
        {
            health.ConsecutiveFailures = 0;
            health.CooldownUntilUtc = DateTime.MinValue;
        }
    }

    private void RecordRouteFailure(
        Guid accountId,
        string sourceHost,
        Exception exception,
        bool immediateCooldown)
    {
        RouteHealth health = _routeHealth.GetOrAdd(
            RouteKey(accountId, sourceHost),
            _ => new RouteHealth());
        lock (health.Gate)
        {
            health.ConsecutiveFailures++;
            if (!immediateCooldown && health.ConsecutiveFailures < 2)
                return;

            TimeSpan delay = GetCooldown(exception, health.ConsecutiveFailures);
            DateTime until = _utcNow() + delay;
            if (until > health.CooldownUntilUtc)
                health.CooldownUntilUtc = until;
        }
    }

    private static TimeSpan GetCooldown(Exception exception, int failures)
    {
        if (exception is HttpRequestException http)
        {
            return http.StatusCode switch
            {
                System.Net.HttpStatusCode.TooManyRequests => TimeSpan.FromMinutes(5),
                System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
                    TimeSpan.FromMinutes(2),
                System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.BadGateway
                    or System.Net.HttpStatusCode.GatewayTimeout => TimeSpan.FromMinutes(1),
                System.Net.HttpStatusCode.NotFound => TimeSpan.FromSeconds(30),
                _ => ExponentialCooldown(failures)
            };
        }
        if (exception is InvalidOperationException or InvalidDataException)
            return TimeSpan.FromMinutes(1);
        return ExponentialCooldown(failures);
    }

    private static TimeSpan ExponentialCooldown(int failures) =>
        TimeSpan.FromSeconds(Math.Min(120, 15 * (1 << Math.Min(3, Math.Max(0, failures - 1)))));

    private static AccountRouteKey RouteKey(Guid accountId, string sourceHost) =>
        new(accountId, sourceHost.Trim().ToLowerInvariant());

    private IDownloadLinkResolver GetResolver(CloudAccountProfile account)
    {
        return _resolvers.GetOrAdd(
            account.Id,
            _ => new Lazy<IDownloadLinkResolver>(
                () => _resolverFactory(account),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static void AdvanceCandidate(RouteState state, int observedCandidate)
    {
        lock (state.Gate)
        {
            if (state.CandidateIndex == observedCandidate)
                state.CandidateIndex++;
            state.FailuresOnCandidate = 0;
        }
    }

    private static string SanitizeFailure(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "route failed";
        return message.Length <= 180 ? message : message[..180] + "…";
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        foreach (Lazy<IDownloadLinkResolver> lazy in _resolvers.Values)
        {
            if (lazy.IsValueCreated && lazy.Value is IDisposable disposable)
                disposable.Dispose();
        }
        _resolvers.Clear();
        _states.Clear();
        _routeHealth.Clear();
        _routePerformance.Clear();
    }

    private sealed class RouteState
    {
        public object Gate { get; } = new();
        public int CandidateIndex { get; set; }
        public int FailuresOnCandidate { get; set; }
        public bool CurrentIsDirect { get; set; }
        public Guid? CurrentAccountId { get; set; }
        public string CurrentRouteName { get; set; } = string.Empty;
        public IReadOnlyList<CloudAccountProfile>? Candidates { get; set; }
    }

    private readonly record struct AccountRouteKey(Guid AccountId, string SourceHost);

    private sealed class RouteHealth
    {
        public object Gate { get; } = new();
        public int ConsecutiveFailures { get; set; }
        public DateTime CooldownUntilUtc { get; set; }
    }

    private sealed class RoutePerformance
    {
        public object Gate { get; } = new();
        public int Successes { get; set; }
        public int Failures { get; set; }
        public double AverageResolutionMilliseconds { get; set; }
    }
}
