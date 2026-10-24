namespace JournalStack.Server;

// Counts calls per key inside a fixed time window. Lives in memory, so it resets on every deploy.
public sealed class RateLimiter
{
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> _windows = new();

    public RateLimiter(int limit, TimeSpan window, TimeProvider clock)
    {
        _limit = limit;
        _window = window;
        _clock = clock;
    }

    public bool TryTake(string key)
    {
        var now = _clock.GetUtcNow();
        lock (_windows)
        {
            if (!_windows.TryGetValue(key, out var current) || now - current.Start >= _window)
            {
                current = (now, 0);
            }
            if (current.Count >= _limit)
            {
                return false;
            }
            _windows[key] = (current.Start, current.Count + 1);
            return true;
        }
    }
}

public sealed class Limits
{
    // One cap for the whole server. There is no per-IP limit, because the client address is not trusted.
    public RateLimiter Registrations { get; }

    // One cap per device across all device endpoints.
    public RateLimiter DeviceRequests { get; }

    // One cap per device on entry posts. Retries and refused requests count too.
    public RateLimiter EntryPosts { get; }

    public Limits(TimeProvider clock)
    {
        Registrations = new RateLimiter(30, TimeSpan.FromMinutes(1), clock);
        DeviceRequests = new RateLimiter(600, TimeSpan.FromHours(1), clock);
        EntryPosts = new RateLimiter(60, TimeSpan.FromHours(1), clock);
    }
}
