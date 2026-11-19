using System.Collections.Concurrent;

namespace Mosaic.Server.RateLimiting;

/// <summary>
/// Per-process fixed-window limiter. The server persists sessions and codes in
/// SQLite, so a single instance is the supported deployment for v1; a shared
/// limiter would be required before running multiple replicas.
/// </summary>
public sealed class WindowLimiter(TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

    public bool TryAcquire(string key, int limit, TimeSpan window)
    {
        if (limit <= 0)
        {
            return true;
        }

        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var seconds = Math.Max(1L, (long)window.TotalSeconds);
        var bucket = _buckets.GetOrAdd(key, _ => new Bucket(now));
        lock (bucket)
        {
            if (now - bucket.WindowStart >= seconds)
            {
                bucket.WindowStart = now;
                bucket.Count = 0;
            }

            if (bucket.Count >= limit)
            {
                return false;
            }

            bucket.Count++;
            return true;
        }
    }

    /// <summary>Checks the window without counting the current request.</summary>
    public bool IsLimited(string key, int limit, TimeSpan window)
    {
        if (limit <= 0 || !_buckets.TryGetValue(key, out var bucket))
        {
            return false;
        }

        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var seconds = Math.Max(1L, (long)window.TotalSeconds);
        lock (bucket)
        {
            if (now - bucket.WindowStart >= seconds)
            {
                bucket.WindowStart = now;
                bucket.Count = 0;
            }

            return bucket.Count >= limit;
        }
    }

    public void Reset() => _buckets.Clear();

    private sealed class Bucket(long windowStart)
    {
        public long WindowStart { get; set; } = windowStart;

        public int Count { get; set; }
    }
}
