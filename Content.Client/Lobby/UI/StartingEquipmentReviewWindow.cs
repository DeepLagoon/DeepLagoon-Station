using System.Linq;
using System.Numerics;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.Lobby.UI.Loadouts;
using Content.Shared._DeepLagoon.Loadouts;
using Content.Shared.Clothing;
using Content.Shared.GameTicking;
using Content.Shared.Preferences;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Roles;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client.Lobby.UI;

public sealed class StartingEquipmentReviewWindow : DefaultWindow
{
    private readonly PersonalLoadoutEditor _equipment = new() { TguiMode = true };
    private readonly TguiPanel _panel = new(inheritChatAppearance: false, showWindowControls: false, forceTgui: true);
    private readonly HumanoidCharacterProfile _profile;
    private HumanoidCharacterProfile _draft;
    private readonly JobPrototype _job;
    private readonly ICommonSession? _session;
    private readonly Func<HumanoidCharacterProfile, EntityUid> _createPreview;
    private readonly int _request;
    private readonly Action<StartingEquipmentReviewReply> _reply;
    private bool _answered;

    public StartingEquipmentReviewWindow(StartingEquipmentReviewEvent review, HumanoidCharacterProfile profile, JobPrototype job,
        ICommonSession? session, Func<HumanoidCharacterProfile, EntityUid> createPreview, Action<StartingEquipmentReviewReply> reply)
    {
        _request = review.Request;
        _profile = profile;
        _draft = profile.Clone();
        _job = job;
        _session = session;
        _createPreview = createPreview;
        _reply = reply;
        Title = job.LocalizedName;
        var available = UserInterfaceManager.WindowRoot.Size;
        MinSize = Vector2.Min(new Vector2(360, 320), available);
        SetSize = Vector2.Min(new Vector2(1060, 650), Vector2.Max(MinSize, available - new Vector2(24)));

        var body = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, SeparationOverride = 8 };
        var message = new RichTextLabel { HorizontalExpand = true };
        message.SetMessage(FormattedMessage.FromUnformatted(Loc.GetString("starting-equipment-review-message")));
        body.AddChild(message);
        body.AddChild(_panel);
        var buttons = new BoxContainer { HorizontalAlignment = HAlignment.Right, SeparationOverride = 8 };
        var apply = new Button { Text = Loc.GetString("starting-equipment-review-apply") };
        var proceed = new Button { Text = Loc.GetString("starting-equipment-review-continue") };
        apply.OnPressed += _ => Finish(apply: true);
        proceed.OnPressed += _ => Finish(apply: false);
        buttons.AddChild(apply);
        buttons.AddChild(proceed);
        body.AddChild(buttons);
        ContentsContainer.AddChild(body);

        _equipment.ProfileChanged += UpdateDraft;
        _panel.OnAction += OnAction;
        OnClose += () =>
        {
            if (!_answered)
                _reply(new StartingEquipmentReviewReply(_request, join: false));
            _equipment.CloseBackpackPreview();
            _equipment.Dispose();
            Dispose();
        };
        UpdateDraft(_draft);
    }

    public void Dismiss()
    {
        _answered = true;
        Close();
    }

    private void Finish(bool apply)
    {
        _answered = true;
        _reply(new StartingEquipmentReviewReply(_request, join: true,
            job: apply ? Changes(LoadoutSystem.GetJobPrototype(_job.ID)) : null,
            personal: apply ? Changes(PersonalLoadoutSystem.Role) : null));
        Close();
    }

    private RoleLoadout? Changes(string role) => _draft.Loadouts.TryGetValue(role, out var changed) &&
        (!_profile.Loadouts.TryGetValue(role, out var original) || !changed.Equals(original)) ? changed : null;

    private void UpdateDraft(HumanoidCharacterProfile draft)
    {
        _draft = draft;
        _equipment.Refresh(draft, _job.ID, _session);
        Publish();
    }

    private void Publish()
    {
        var data = _equipment.CreateTguiBackpack(() => _createPreview(_draft));
        _panel.SetState("BackpackReview", new TguiData().Object("backpack", data).ToString(), _job.LocalizedName);
    }

    private void OnAction(string action, string payload)
    {
        if (!TguiActionData.TryParse(payload, out var args))
            return;
        if (action == "icons" && args!.String("ids") is { } ids)
        {
            var prototypes = IoCManager.Resolve<IPrototypeManager>();
            var images = IoCManager.Resolve<TguiSpriteImages>();
            var icons = ids.Split(',').Distinct().Take(100).Where(id => id.StartsWith("item/", StringComparison.Ordinal))
                .Select(id => new TguiData().String("id", id).Array("layers", prototypes.TryIndex<EntityPrototype>(id[5..], out _)
                    ? images.Item(id[5..]) : Enumerable.Empty<TguiData>()));
            _panel.Web.Send("update", new TguiData().Object("data", new TguiData().Object("iconBatch", new TguiData().Array("icons", icons))).ToString());
            return;
        }
        if (action is "backpack-add" or "backpack-move" or "backpack-remove" or "backpack-container")
        {
            _equipment.HandleTguiBackpackAction(action, args!);
            Publish();
        }
    }
}
