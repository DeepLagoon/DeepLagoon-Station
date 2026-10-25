using System.Linq;
using Content.Shared.Chat;

namespace Content.Client._DeepLagoon.WebUI;

public sealed class WebChatMessageCache
{
    private readonly Dictionary<ChatMessage, (ChatChannel Channel, string Markup, Color? Color, string Payload)> _payloads = new();

    public string GetPayload(ChatMessage message)
    {
        if (_payloads.TryGetValue(message, out var cached) && cached.Channel == message.Channel &&
            cached.Markup == message.WrappedMessage && cached.Color == message.MessageColorOverride)
            return cached.Payload;
        if (_payloads.Count >= 5000) _payloads.Clear();
        var payload = WebChatMessageFormatter.BuildPayload(message);
        _payloads[message] = (message.Channel, message.WrappedMessage, message.MessageColorOverride, payload);
        return payload;
    }

    public void Retain(IReadOnlyCollection<ChatMessage> messages)
    {
        var retained = new HashSet<ChatMessage>(messages);
        foreach (var message in _payloads.Keys.Where(message => !retained.Contains(message)).ToArray())
            _payloads.Remove(message);
    }
}
