using System.Collections.Concurrent;

namespace AChat.Api.Hubs;

public sealed class ChatConnectionRegistry : IChatConnectionRegistry
{
    // userId → set of active connectionIds
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, bool>> _connections = new();

    public void Register(Guid userId, string connectionId)
    {
        var set = _connections.GetOrAdd(userId, _ => new ConcurrentDictionary<string, bool>());
        set[connectionId] = true;
    }

    public void Unregister(Guid userId, string connectionId)
    {
        if (_connections.TryGetValue(userId, out var set))
        {
            set.TryRemove(connectionId, out _);
            if (set.IsEmpty)
                _connections.TryRemove(userId, out _);
        }
    }

    public IReadOnlyList<string> GetConnections(Guid userId) =>
        _connections.TryGetValue(userId, out var set)
            ? set.Keys.ToList()
            : [];
}
