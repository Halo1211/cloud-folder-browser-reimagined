using System.Diagnostics;

namespace CloudFolderBrowser.Networking;

public sealed class BandwidthLimiter
{
    private readonly object _sync = new();
    private readonly long _bytesPerSecond;
    private long _nextAvailableTimestamp;

    public BandwidthLimiter(long kibibytesPerSecond)
    {
        _bytesPerSecond = Math.Max(0, kibibytesPerSecond) * 1024L;
    }

    public bool IsLimited => _bytesPerSecond > 0;
    public long BytesPerSecond => _bytesPerSecond;

    public async ValueTask ThrottleAsync(int bytes, CancellationToken cancellationToken = default)
    {
        if (_bytesPerSecond <= 0 || bytes <= 0)
            return;

        long delayTicks;
        lock (_sync)
        {
            long now = Stopwatch.GetTimestamp();
            long start = Math.Max(now, _nextAvailableTimestamp);
            long duration = Math.Max(1,
                (long)Math.Ceiling(bytes * (double)Stopwatch.Frequency / _bytesPerSecond));
            _nextAvailableTimestamp = checked(start + duration);
            delayTicks = start - now;
        }

        if (delayTicks > 0)
        {
            TimeSpan delay = TimeSpan.FromSeconds(delayTicks / (double)Stopwatch.Frequency);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }
}
