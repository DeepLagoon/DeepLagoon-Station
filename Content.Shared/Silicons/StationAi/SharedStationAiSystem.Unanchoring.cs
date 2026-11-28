using Content.Shared._CorvaxNext.Silicons.Borgs.Components;
using Content.Shared.Construction.Components;

namespace Content.Shared.Silicons.StationAi;

public abstract partial class SharedStationAiSystem
{
    private void InitializeUnanchoring()
    {
        SubscribeLocalEvent<StationAiCoreComponent, UnanchorAttemptEvent>(OnCoreUnanchorAttempt);
        SubscribeLocalEvent<StationAiCoreComponent, UnanchorCompletionAttemptEvent>(OnCoreUnanchorCompletion);
    }

    private void OnCoreUnanchorAttempt(Entity<StationAiCoreComponent> ent, ref UnanchorAttemptEvent args)
        => CheckCoreUnanchoring(ent, args);

    private void OnCoreUnanchorCompletion(Entity<StationAiCoreComponent> ent, ref UnanchorCompletionAttemptEvent args)
        => CheckCoreUnanchoring(ent, args);

    private void CheckCoreUnanchoring(Entity<StationAiCoreComponent> ent, BaseAnchoredAttemptEvent args)
    {
        if (args.Cancelled || !HasAssignedMind(ent))
            return;
        args.Cancel();
        _popup.PopupClient(Loc.GetString("station-ai-unanchor-occupied"), ent, args.User);
    }

    /// <summary>
    /// A disconnected player still owns their mind. Remote control moves the mind
    /// out of the held brain, so check its connected body as well as the AI eye.
    /// </summary>
    public bool HasAssignedMind(Entity<StationAiCoreComponent> ent)
    {
        if (_mind.TryGetMind(ent.Owner, out _, out _))
            return true;
        if (ent.Comp.RemoteEntity is { } eye && _mind.TryGetMind(eye, out _, out _))
            return true;
        if (!TryGetHeld((ent.Owner, ent.Comp), out var held))
            return false;
        if (_mind.TryGetMind(held, out _, out _))
            return true;
        if (!TryComp<StationAiHeldComponent>(held, out var brain) || brain.CurrentConnectedEntity is not { } body)
            return false;
        return _mind.TryGetMind(body, out _, out _) ||
               TryComp<AiRemoteControllerComponent>(body, out var remote) && remote.AiHolder == held &&
               remote.LinkedMind is { } linked && HasComp<Content.Shared.Mind.MindComponent>(linked);
    }
}
