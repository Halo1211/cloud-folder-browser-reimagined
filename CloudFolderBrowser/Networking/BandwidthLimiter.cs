using System.Diagnostics;

namespace CloudFolderBrowser.Networking;

public sealed class BandwidthLimiter
{
    private readonly object _sync = new();
    private readonly long _bytesPerSecond;
    private long _nextAvailableTimestamp;

    public BandwidthLimiter(long kibibytesPerSecond)
    {
        if (kibibytesPerSecond > long.MaxValue / 1024)
            throw new ArgumentOutOfRangeException(nameof(kibibytesPerSecond));
        _bytesPerSecond = Math.Max(0, kibibytesPerSecond) * 1024L;
    }

    public bool IsLimited => _bytesPerSecond > 0;
    public long BytesPerSecond => _bytesPerSecond;

    public async ValueTask ThrottleAsync(int bytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_bytesPerSecond <= 0 || bytes <= 0)
            return;

        // Claim a slot only when it becomes available. Reserving far-future slots
        // before waiting leaves cancelled downloads occupying bandwidth.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long delayTicks;
            lock (_sync)
            {
                long now = Stopwatch.GetTimestamp();
                delayTicks = _nextAvailableTimestamp - now;
                if (delayTicks <= 0)
                {
                    long duration = Math.Max(1,
                        (long)Math.Ceiling(bytes * (double)Stopwatch.Frequency / _bytesPerSecond));
                    _nextAvailableTimestamp = checked(now + duration);
                    return;
                }
            }

            TimeSpan delay = TimeSpan.FromSeconds(delayTicks / (double)Stopwatch.Frequency);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }
}
