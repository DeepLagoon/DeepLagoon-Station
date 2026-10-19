using System.Numerics;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;

namespace Content.Client._DeepLagoon.Admin;

public sealed class AdminMapUploadWindow : DefaultWindow
{
    private readonly Button _confirm;
    private readonly Button _cancel;
    private bool _ownsUpload;

    public AdminMapUploadWindow()
    {
        Title = Loc.GetString("dl-map-upload-title");
        MinSize = new Vector2(460, 160);
        var layout = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, SeparationOverride = 8 };
        var status = new Label { Text = Loc.GetString("dl-map-upload-help") };
        var button = new Button { Text = Loc.GetString("dl-map-upload-pick") };
        var progress = new ProgressBar { MinValue = 0, MaxValue = 1, Value = 0, MinHeight = 22 };
        var placement = new BoxContainer();
        _confirm = new Button { Text = Loc.GetString("dl-map-upload-confirm"), Disabled = true, HorizontalExpand = true };
        _cancel = new Button { Text = Loc.GetString("dl-map-upload-cancel"), Disabled = true, HorizontalExpand = true };
        placement.AddChild(_confirm);
        placement.AddChild(_cancel);
        layout.AddChild(status);
        layout.AddChild(progress);
        layout.AddChild(button);
        layout.AddChild(placement);
        Contents.AddChild(layout);
        button.OnPressed += _ =>
        {
            button.Disabled = true;
            _ownsUpload = true;
            progress.Value = 0;
            status.Text = Loc.GetString("dl-map-upload-working");
            IoCManager.Resolve<IEntityManager>().System<AdminMapUploadSystem>().PickAndUpload(message =>
            {
                if (Disposed)
                    return;
                status.Text = Loc.GetString(message);
                var preview = message == "dl-map-upload-preview";
                button.Disabled = preview;
                if (!preview)
                    _ownsUpload = false;
                _cancel.Disabled = !preview;
                if (message == "dl-map-upload-success")
                    progress.Value = 1;
            }, (received, total) =>
            {
                if (Disposed)
                    return;
                progress.Value = (float) received / total;
                status.Text = received == total
                    ? Loc.GetString("dl-map-upload-spawning")
                    : Loc.GetString("dl-map-upload-progress", ("percent", (int) ((long) received * 100 / total)),
                        ("received", $"{received / 1048576f:0.0}"), ("total", $"{total / 1048576f:0.0}"));
            });
        };
        _confirm.OnPressed += _ => IoCManager.Resolve<IEntityManager>().System<AdminMapUploadSystem>().ConfirmPlacement();
        _cancel.OnPressed += _ => IoCManager.Resolve<IEntityManager>().System<AdminMapUploadSystem>().CancelPlacement();
        OnClose += () =>
        {
            if (_ownsUpload)
                IoCManager.Resolve<IEntityManager>().System<AdminMapUploadSystem>().CancelPlacement();
            Dispose();
        };
    }

    protected override void FrameUpdate(Robust.Shared.Timing.FrameEventArgs args)
    {
        base.FrameUpdate(args);
        _confirm.Disabled = !_ownsUpload || !IoCManager.Resolve<IEntityManager>()
            .EntitySysManager.TryGetEntitySystem<AdminMapUploadSystem>(out var system) || !system.CanConfirmPlacement;
    }
}
