using Content.Client.UserInterface.Controls;
using Content.Shared._DeepLagoon.Apartments;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._DeepLagoon.Apartments;

[UsedImplicitly]
public sealed class ApartmentAtmosphereBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private ApartmentAtmosphereWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<ApartmentAtmosphereWindow>();
        _window.Apply.OnPressed += _ => SendMessage(new ApartmentAtmosphereSetMessage(_window.Oxygen.Value));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is ApartmentAtmosphereState atmosphere)
            _window?.SetState(atmosphere);
    }
}

public sealed class ApartmentAtmosphereWindow : FancyWindow
{
    public readonly SpinBox Oxygen = new() { IsValid = value => value is >= 0 and <= 100 };
    public readonly Button Apply = new() { Text = Loc.GetString("dl-apartment-atmosphere-apply") };
    private readonly Label _nitrogen = new();
    private readonly Label _status = new();
    private int? _appliedPercent;

    public ApartmentAtmosphereWindow()
    {
        Title = Loc.GetString("dl-apartment-atmosphere-title");
        MinWidth = 390;
        var contents = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, SeparationOverride = 8 };
        contents.AddChild(_status);
        contents.AddChild(new Label { Text = Loc.GetString("dl-apartment-atmosphere-target") });
        contents.AddChild(new Label { Text = Loc.GetString("dl-apartment-atmosphere-oxygen") });
        Oxygen.InitDefaultButtons();
        contents.AddChild(Oxygen);
        contents.AddChild(_nitrogen);
        contents.AddChild(Apply);
        ContentsContainer.AddChild(contents);
        Oxygen.ValueChanged += _ => UpdateNitrogen();
    }

    public void SetState(ApartmentAtmosphereState state)
    {
        _status.Text = Loc.GetString(!state.Saved ? "dl-apartment-atmosphere-save-failed"
            : state.Active ? "dl-apartment-atmosphere-active" : "dl-apartment-atmosphere-inactive");
        if (_appliedPercent != state.OxygenPercent)
        {
            Oxygen.OverrideValue(state.OxygenPercent);
            _appliedPercent = state.OxygenPercent;
        }
        Oxygen.LineEditDisabled = Apply.Disabled = !state.Active;
        Oxygen.SetButtonDisabled(!state.Active);
        Oxygen.OverrideValue(Oxygen.Value);
        UpdateNitrogen();
    }

    private void UpdateNitrogen() =>
        _nitrogen.Text = Loc.GetString("dl-apartment-atmosphere-nitrogen", ("percent", 100 - Oxygen.Value));
}
