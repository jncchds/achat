using System.Collections.Concurrent;

namespace AChat.Infrastructure.Telegram;

public class TelegramRateLimiter(int globalLimitPerMinute, int perBotLimitPerMinute)
{
    private readonly SlidingWindowCounter _global = new(globalLimitPerMinute, TimeSpan.FromMinutes(1));
    private readonly ConcurrentDictionary<Guid, SlidingWindowCounter> _perBot = new();

    public bool AllowGlobal() => _global.TryIncrement();

    public bool AllowBot(Guid botId)
    {
        var counter = _perBot.GetOrAdd(botId, _ => new SlidingWindowCounter(perBotLimitPerMinute, TimeSpan.FromMinutes(1)));
        return counter.TryIncrement();
    }
}

internal sealed class SlidingWindowCounter(int limit, TimeSpan window)
{
    private readonly Queue<DateTime> _timestamps = new();
    private readonly Lock _lock = new();

    public bool TryIncrement()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var cutoff = now - window;

            while (_timestamps.Count > 0 && _timestamps.Peek() < cutoff)
                _timestamps.Dequeue();

            if (_timestamps.Count >= limit) return false;

            _timestamps.Enqueue(now);
            return true;
        }
    }
}
