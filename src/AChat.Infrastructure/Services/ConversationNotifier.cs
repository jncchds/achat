using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AChat.Core.DTOs.Conversations;
using AChat.Core.Interfaces.Services;

namespace AChat.Infrastructure.Services;

public sealed class ConversationNotifier : IConversationNotifier
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Channel<MessageDto>>> _subs = new();

    public void Notify(Guid conversationId, MessageDto message)
    {
        if (!_subs.TryGetValue(conversationId, out var subscribers)) return;
        foreach (var (_, channel) in subscribers)
            channel.Writer.TryWrite(message);
    }

    public async IAsyncEnumerable<MessageDto> SubscribeAsync(
        Guid conversationId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var subId = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<MessageDto>(new UnboundedChannelOptions { SingleReader = true });
        var subscribers = _subs.GetOrAdd(conversationId, _ => new ConcurrentDictionary<Guid, Channel<MessageDto>>());
        subscribers[subId] = channel;

        try
        {
            await foreach (var msg in channel.Reader.ReadAllAsync(ct))
                yield return msg;
        }
        finally
        {
            subscribers.TryRemove(subId, out _);
            if (subscribers.IsEmpty)
                _subs.TryRemove(conversationId, out _);
        }
    }
}
