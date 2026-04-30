namespace AChat.Api.Hubs;

public interface IChatConnectionRegistry
{
    void Register(Guid userId, string connectionId);
    void Unregister(Guid userId, string connectionId);
    IReadOnlyList<string> GetConnections(Guid userId);
}
