using Content.Client.UserInterface.Controls;
using Content.Shared._DeepLagoon.Mining;
using Content.Shared.Materials;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Prototypes;

namespace Content.Client._DeepLagoon.Mining;

[UsedImplicitly]
public sealed class LaserDrillBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private LaserDrillWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<LaserDrillWindow>();
        _window.OnSelected += material => SendMessage(new LaserDrillSelectMessage(material));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is LaserDrillState drill)
            _window?.SetState(drill);
    }
}

public sealed class LaserDrillWindow : FancyWindow
{
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    public event Action<ProtoId<MaterialPrototype>>? OnSelected;
    private readonly Label _silo = new();
    private readonly Label _status = new();
    private readonly OptionButton _resource = new();
    private readonly List<ProtoId<MaterialPrototype>> _options = new();

    public LaserDrillWindow()
    {
        IoCManager.InjectDependencies(this);
        Title = Loc.GetString("dl-laser-drill-title");
        MinWidth = 440;
        var box = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, SeparationOverride = 8 };
        box.AddChild(new Label { Text = Loc.GetString("dl-laser-drill-resource") });
        box.AddChild(_resource);
        box.AddChild(_silo);
        box.AddChild(_status);
        box.AddChild(new Label { Text = Loc.GetString("dl-laser-drill-link-hint") });
        ContentsContainer.AddChild(box);
        _resource.OnItemSelected += args =>
        {
            // The server confirms the selection and validates the allowed materials.
            OnSelected?.Invoke(_options[args.Id]);
        };
    }

    public void SetState(LaserDrillState state)
    {
        var rebuild = _options.Count != state.Materials.Count + 1;
        for (var i = 1; !rebuild && i < _options.Count; i++)
            rebuild = !state.Materials.ContainsKey(_options[i]);
        if (rebuild)
        {
            _options.Clear();
            _resource.Clear();
            _resource.AddItem(Loc.GetString("dl-laser-drill-select"), 0);
            // Index zero is a prompt, never a valid material selection.
            _options.Add(default);
            _resource.SetItemDisabled(0, true);
            foreach (var (material, _) in state.Materials)
            {
                var id = _options.Count;
                _options.Add(material);
                _resource.AddItem(Loc.GetString(_prototypes.Index(material).Name), id);
            }
        }
        var selected = 0;
        for (var i = 1; i < _options.Count; i++)
        {
            if (state.SelectedMaterial == _options[i])
                selected = i;
        }
        _resource.SelectId(selected);
        _silo.Text = Loc.GetString("dl-laser-drill-silo", ("silo", string.IsNullOrEmpty(state.SiloName)
            ? Loc.GetString("dl-laser-drill-no-silo") : state.SiloName));
        _status.Text = Loc.GetString($"dl-laser-drill-status-{state.Status}");
    }
}
