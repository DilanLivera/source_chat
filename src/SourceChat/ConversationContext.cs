using Microsoft.Extensions.AI;

namespace SourceChat;

internal class ConversationContext
{
    public List<ChatMessage> History { get; } = [];

    public List<string> RetrievedChunks { get; } = [];

    public void AddUserMessage(string message)
    {
        History.Add(new ChatMessage(ChatRole.User, message));
    }

    public void AddAssistantMessage(string message)
    {
        History.Add(new ChatMessage(ChatRole.Assistant, message));
    }

    public void AddRetrievedChunks(IEnumerable<string> chunks)
    {
        RetrievedChunks.AddRange(chunks);
    }

    public void Clear()
    {
        History.Clear();
        RetrievedChunks.Clear();
    }
}