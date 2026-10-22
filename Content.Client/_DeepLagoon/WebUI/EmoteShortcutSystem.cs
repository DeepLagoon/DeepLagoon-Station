using System.Linq;
using Content.Shared.Chat;
using Content.Shared.Chat.Prototypes;
using Content.Shared.Speech;
using Content.Shared.Whitelist;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Client._DeepLagoon.WebUI;

/// <summary>One availability check for shortcuts; the server remains authoritative.</summary>
public sealed partial class EmoteShortcutSystem : EntitySystem
{
    [Dependency] private ISharedPlayerManager _player = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public IEnumerable<EmotePrototype> Available() => _prototypes.EnumeratePrototypes<EmotePrototype>()
        .Where(CanPlay).OrderBy(emote => Loc.GetString(emote.Name));

    public bool CanPlay(EmotePrototype emote)
    {
        if (_player.LocalEntity is not { } player || emote.Category == EmoteCategory.Invalid || emote.ChatTriggers.Count == 0) return false;
        var whitelist = EntityManager.System<EntityWhitelistSystem>();
        return whitelist.IsWhitelistPassOrNull(emote.Whitelist, player) && !whitelist.IsBlacklistPass(emote.Blacklist, player) &&
               (emote.Available || (TryComp<SpeechComponent>(player, out var speech) && speech.AllowedEmotes.Contains(emote.ID)));
    }

    public bool TryPlay(string id)
    {
        if (!_prototypes.TryIndex<EmotePrototype>(id, out var emote) || !CanPlay(emote)) return false;
        RaisePredictiveEvent(new PlayEmoteMessage(new ProtoId<EmotePrototype>(id)));
        return true;
    }
}
