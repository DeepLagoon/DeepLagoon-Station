using System.Numerics;
using System.Linq;
using Robust.Shared.Utility;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;

namespace Content.Client._DeepLagoon.Admin;

public sealed class AdminMapUploadWindow : DefaultWindow
{
    private readonly Button _confirm;
    private readonly Button _cancel;
    private bool _ownsUpload;
    private readonly AdminMapUploadSystem _system;
    private readonly BoxContainer _library;
    private readonly ScrollContainer _libraryScroll;

    public AdminMapUploadWindow()
    {
        _system = IoCManager.Resolve<IEntityManager>().System<AdminMapUploadSystem>();
        Title = Loc.GetString("dl-map-upload-title");
        MinSize = new Vector2(460, 160);
        var layout = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, SeparationOverride = 8 };
        var status = new Label { Text = Loc.GetString("dl-map-upload-help") };
        var button = new Button { Text = Loc.GetString("dl-map-upload-pick") };
        var progress = new ProgressBar { MinValue = 0, MaxValue = 1, Value = 0, MinHeight = 22 };
        var missingText = new RichTextLabel { HorizontalExpand = true };
        var missingScroll = new ScrollContainer { MaxHeight = 180, HScrollEnabled = false, Visible = false };
        missingScroll.AddChild(missingText);
        var placement = new BoxContainer();
        _confirm = new Button { Text = Loc.GetString("dl-map-upload-confirm"), Disabled = true, HorizontalExpand = true };
        _cancel = new Button { Text = Loc.GetString("dl-map-upload-cancel"), Disabled = true, HorizontalExpand = true };
        placement.AddChild(_confirm);
        placement.AddChild(_cancel);
        layout.AddChild(status);
        layout.AddChild(progress);
        layout.AddChild(missingScroll);
        layout.AddChild(button);
        layout.AddChild(placement);
        layout.AddChild(new Label { Text = Loc.GetString("dl-map-upload-library") });
        _library = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical };
        _libraryScroll = new ScrollContainer { MinHeight = 80, MaxHeight = 220, HScrollEnabled = false };
        _libraryScroll.AddChild(_library);
        layout.AddChild(_libraryScroll);
        Contents.AddChild(layout);
        _system.LibraryChanged += RebuildLibrary;
        RebuildLibrary();
        _system.RefreshLibrary();
        void Completed(string message)
        {
            if (Disposed)
                return;
            status.Text = Loc.GetString(message);
            var preview = message == "dl-map-upload-preview";
            var system = IoCManager.Resolve<IEntityManager>().System<AdminMapUploadSystem>();
            var missing = system.MissingPrototypes;
            missingScroll.Visible = (preview || message == "dl-map-upload-success") && missing.Count > 0;
            if (missingScroll.Visible)
            {
                var header = Loc.GetString("dl-map-upload-missing", ("count", missing.Values.Sum()));
                var entries = missing.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => $"{entry.Key} × {entry.Value}");
                missingText.SetMessage(FormattedMessage.FromUnformatted(header + "\n" + string.Join("\n", entries)));
            }
            button.Disabled = preview;
            if (!preview)
                _ownsUpload = false;
            _cancel.Disabled = !preview;
            if (message == "dl-map-upload-success")
                progress.Value = 1;

        }
        void PlaceStored(Guid id)
        {
            if (_system.IsBusy)
                return;
            _ownsUpload = true;
            button.Disabled = true;
            missingScroll.Visible = false;
            status.Text = Loc.GetString("dl-map-upload-spawning");
            _system.PlaceFromLibrary(id, Completed);
        }
        _placeStored = PlaceStored;
        button.OnPressed += _ =>
        {
            button.Disabled = true;
            _ownsUpload = true;
            progress.Value = 0;
            missingScroll.Visible = false;
            status.Text = Loc.GetString("dl-map-upload-working");
            _system.PickAndUpload(Completed, (received, total) =>
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
            if (_ownsUpload && _system.IsConnected)
                IoCManager.Resolve<IEntityManager>().System<AdminMapUploadSystem>().CancelPlacement();
            _system.LibraryChanged -= RebuildLibrary;
            Dispose();
        };
        if (_system.ObservePending(Completed, (received, total) =>
            {
                progress.Value = (float) received / total;
                status.Text = Loc.GetString("dl-map-upload-progress", ("percent", (int) ((long) received * 100 / total)),
                    ("received", $"{received / 1048576f:0.0}"), ("total", $"{total / 1048576f:0.0}"));
            }))
        {
            _ownsUpload = true;
            button.Disabled = true;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _system.LibraryChanged -= RebuildLibrary;
        base.Dispose(disposing);
    }

    private readonly Action<Guid> _placeStored;

    private void RebuildLibrary()
    {
        _library.RemoveAllChildren();
        if (_system.Library.Count == 0)
            _library.AddChild(new Label { Text = Loc.GetString("dl-map-upload-library-empty") });
        foreach (var entry in _system.Library.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            var button = new Button
            {
                Text = entry.Name,
                ToolTip = Loc.GetString("dl-map-upload-library-file", ("size", $"{entry.Length / 1048576f:0.00}")),
                ClipText = true,
                HorizontalExpand = true
            };
            button.OnPressed += _ => _placeStored(entry.Id);
            _library.AddChild(button);
        }
    }

    protected override void FrameUpdate(Robust.Shared.Timing.FrameEventArgs args)
    {
        base.FrameUpdate(args);
        _system.PollLibrary();
        foreach (var child in _library.Children.OfType<Button>())
            child.Disabled = _system.IsBusy || !_system.CanUpload;
        _confirm.Disabled = !_ownsUpload || !IoCManager.Resolve<IEntityManager>()
            .EntitySysManager.TryGetEntitySystem<AdminMapUploadSystem>(out var system) || !system.CanConfirmPlacement;
    }
}
