using System.Linq;
using System.Text.Json;
using Content.Server.Administration.Logs;
using Content.Server.Database;
using Content.Shared._DeepLagoon.InteractionPanel;
using Content.Shared.Database;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._DeepLagoon.InteractionPanel;

public sealed partial class InteractionPanelSystem
{
    [Dependency] private readonly IServerDbManager _db = default!;
    [Dependency] private readonly IAdminLogManager _adminLog = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    private readonly Dictionary<NetUserId, InteractionPanelCustomAction[]> _libraries = new();
    private readonly HashSet<NetUserId> _libraryBusy = new();
    private readonly Dictionary<NetUserId, TimeSpan> _nextLibraryWrite = new();

    private InteractionPanelActionDefinition? FindAction(EntityUid user, string id)
    {
        var standard = InteractionPanelActionCatalog.Load(_prototypes).FirstOrDefault(a => a.Id == id);
        if (standard != null) return standard;
        return TryComp<ActorComponent>(user, out var actor) && _libraries.TryGetValue(actor.PlayerSession.UserId, out var actions)
            ? actions.FirstOrDefault(a => a.Id == id)?.Definition() : null;
    }

    private void SendLibrary(ICommonSession session, string message = "")
    {
        if (session.Status == Robust.Shared.Enums.SessionStatus.Disconnected) return;
        var actions = _libraries.GetValueOrDefault(session.UserId) ?? Array.Empty<InteractionPanelCustomAction>();
        var available = session.AttachedEntity is { } user && _panels.TryGetValue(user, out var panel)
            ? actions.Where(a => Allowed(user, panel.Target, a.Definition())).Select(a => a.Id).ToArray()
            : Array.Empty<string>();
        RaiseNetworkEvent(new InteractionPanelLibraryEvent(actions, available, message), session);
    }

    private void OnLibraryRequest(InteractionPanelLibraryRequestEvent args, EntitySessionEventArgs ev) => ChangeLibrary(ev.SenderSession);
    private void OnCustomSave(InteractionPanelCustomSaveEvent args, EntitySessionEventArgs ev) => ChangeLibrary(ev.SenderSession, args.Action);
    private void OnCustomDelete(InteractionPanelCustomDeleteEvent args, EntitySessionEventArgs ev) => ChangeLibrary(ev.SenderSession, delete: args.Id);

    // Операции аккаунта сериализованы; ответ "сохранено" отправляется только после успешного commit.
    private async void ChangeLibrary(ICommonSession session, InteractionPanelCustomAction? save = null, string? delete = null)
    {
        var owner = session.UserId;
        if (save != null || delete != null)
        {
            if (_nextLibraryWrite.GetValueOrDefault(owner) > _timing.CurTime) { SendLibrary(session, "dl-interaction-panel-library-busy"); return; }
            _nextLibraryWrite[owner] = _timing.CurTime + TimeSpan.FromSeconds(1);
        }
        if (!_libraryBusy.Add(owner)) { SendLibrary(session, "dl-interaction-panel-library-busy"); return; }
        try
        {
            if (!_libraries.TryGetValue(owner, out var old))
            {
                var json = await _db.GetInteractionPanelActionsAsync(owner);
                old = JsonSerializer.Deserialize<InteractionPanelCustomAction[]>(json) ?? Array.Empty<InteractionPanelCustomAction>();
                _libraries[owner] = old;
            }
            if (save == null && delete == null) { SendLibrary(session); return; }
            if (save != null && (!InteractionPanelCustomRules.Valid(save) ||
                (save.Sound != null && !_prototypes.HasIndex<InteractionPanelSoundPrototype>(save.Sound))))
            { SendLibrary(session, "dl-interaction-panel-library-invalid"); return; }

            var actions = old.ToList();
            var id = save?.Id ?? delete!;
            var previous = actions.FirstOrDefault(a => a.Id == id);
            if (save != null && id == "")
            {
                if (actions.Count >= InteractionPanelCustomRules.MaxActions) { SendLibrary(session, "dl-interaction-panel-library-full"); return; }
                id = "custom:" + Guid.NewGuid().ToString("N");
                save = save with { Id = id };
            }
            else if (previous == null) { SendLibrary(session, "dl-interaction-panel-library-invalid"); return; }
            actions.RemoveAll(a => a.Id == id);
            if (save != null) actions.Add(save);
            if (!await _db.SaveInteractionPanelActionsAsync(owner, JsonSerializer.Serialize(actions)))
            { SendLibrary(session, "dl-interaction-panel-library-error"); return; }

            // Старое согласие нельзя использовать для изменённого текста/звука/преференса.
            foreach (var (user, panel) in _panels.ToArray())
                if (panel.Session.UserId == owner && previous != null) Stop(user, previous.Category);
            _libraries[owner] = actions.ToArray();
            _adminLog.Add(LogType.Chat, LogImpact.Low,
                $"InteractionPanelCustom {(save == null ? "delete" : previous == null ? "create" : "edit")} by {owner}: id={id}; before={JsonSerializer.Serialize(previous)}; after={JsonSerializer.Serialize(save)}");
            SendLibrary(session, "dl-interaction-panel-library-saved");
            if (session.AttachedEntity is { } attached) Status(attached, "dl-interaction-panel-ready");
        }
        catch (Exception exception)
        {
            Log.Error($"InteractionPanel library database operation failed: {exception}");
            SendLibrary(session, "dl-interaction-panel-library-error");
        }
        finally { _libraryBusy.Remove(owner); }
    }
}
