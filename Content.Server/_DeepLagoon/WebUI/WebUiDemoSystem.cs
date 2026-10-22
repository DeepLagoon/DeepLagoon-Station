using Content.Shared._DeepLagoon.WebUI;
using Robust.Server.GameObjects;

namespace Content.Server._DeepLagoon.WebUI;

public sealed partial class WebUiDemoSystem : EntitySystem
{
    [Dependency] private UserInterfaceSystem _ui = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<WebUiDemoComponent, BoundUIOpenedEvent>(OnOpen);
        SubscribeLocalEvent<WebUiDemoComponent, WebUiActionMessage>(OnAction);
    }

    private void OnOpen(EntityUid uid, WebUiDemoComponent component, BoundUIOpenedEvent args) => Publish(uid, component);

    private void OnAction(EntityUid uid, WebUiDemoComponent component, WebUiActionMessage args)
    {
        // Bound UI already verifies the sender is subscribed and may interact with this entity.
        // Each system must still whitelist its own actions and validate its arguments.
        if (args.UiKey is not WebUiKey.Key || args.PayloadJson.Length > WebUiActionMessage.MaxPayloadLength)
            return;
        if (args.Action == "increment" && component.Count < 1000)
            component.Count++;
        else if (args.Action == "reset")
            component.Count = 0;
        else
            return;
        Publish(uid, component);
    }

    private void Publish(EntityUid uid, WebUiDemoComponent component) =>
        _ui.SetUiState(uid, WebUiKey.Key, new WebUiState("DeepLagoonDemo", "{\"count\":" + component.Count + "}"));
}
