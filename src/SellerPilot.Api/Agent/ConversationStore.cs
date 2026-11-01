using System.Collections.Concurrent;
using SellerPilot.Api.Llm;

namespace SellerPilot.Api.Agent;

public sealed class Conversation(Guid id)
{
    public Guid Id { get; } = id;

    /// <summary>Append-only history; only AgentService writes it, while holding <see cref="Gate"/>.</summary>
    public List<LlmMessage> Messages { get; } = [];

    /// <summary>Serializes turns: two messages to the same conversation never run at once.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public DateTimeOffset LastActivity { get; set; }
}

public interface IConversationStore
{
    /// <summary>Returns the conversation with this ID, or a new one (with a new server-chosen ID) if it is unknown.</summary>
    Conversation GetOrCreate(Guid? id);
}

/// <summary>Single demo seller, so conversations live in memory and are lost on restart.</summary>
public sealed class InMemoryConversationStore(TimeProvider time) : IConversationStore
{
    private const int MaxConversations = 200;
    private readonly ConcurrentDictionary<Guid, Conversation> _conversations = new();

    public Conversation GetOrCreate(Guid? id)
    {
        var now = time.GetUtcNow();
        if (id is { } existingId && _conversations.TryGetValue(existingId, out var existing))
        {
            existing.LastActivity = now;
            return existing;
        }

        if (_conversations.Count >= MaxConversations)
        {
            var oldest = _conversations.Values.OrderBy(c => c.LastActivity).First();
            _conversations.TryRemove(oldest.Id, out _);
        }

        var conversation = new Conversation(Guid.NewGuid()) { LastActivity = now };
        _conversations[conversation.Id] = conversation;
        return conversation;
    }
}
