using System.Numerics;
using System.IO;
using Content.Client.Eui;
using Content.Shared._DeepLagoon.Art;
using Content.Shared.Eui;
using Robust.Client.Graphics;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Graphics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Client._DeepLagoon.Art;

public sealed class ArtModerationEui : BaseEui
{
    private readonly DefaultWindow _window;
    private readonly BoxContainer _list = new() { Orientation = BoxContainer.LayoutOrientation.Vertical };
    private readonly BoxContainer _details = new() { Orientation = BoxContainer.LayoutOrientation.Vertical };
    private readonly TextureRect _image = new() { Stretch = TextureRect.StretchMode.KeepAspectCentered, CanShrink = true, VerticalExpand = true };
    private readonly Label _status = new();
    private readonly Label _page = new();
    private readonly LineEdit _search = new() { HorizontalExpand = true };
    private readonly Button _previous;
    private readonly Button _next;
    private readonly Button _delete;
    private readonly Button _confirm;
    private ArtModerationEuiState _state = new();
    private OwnedTexture? _texture;
    private string _png = "";

    private static Button Button(string key) => new() { Text = Loc.GetString("dl-art-moderation-" + key) };

    public ArtModerationEui()
    {
        _window = new DefaultWindow { Title = Loc.GetString("dl-art-moderation-title"), MinSize = new Vector2(760, 560) };
        _window.SetSize = new Vector2(1060, 800);
        var root = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical };
        _window.Contents.AddChild(root);
        var searchRow = new BoxContainer();
        root.AddChild(searchRow);
        _search.PlaceHolder = Loc.GetString("dl-art-moderation-search");
        searchRow.AddChild(_search);
        var find = Button("refresh");
        searchRow.AddChild(find);
        find.OnPressed += _ => Load(0);
        _search.OnTextEntered += _ => Load(0);
        var body = new BoxContainer { VerticalExpand = true };
        root.AddChild(body);
        var sidebar = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, MinWidth = 260, MaxWidth = 320 };
        body.AddChild(sidebar);
        var scroll = new ScrollContainer { VerticalExpand = true };
        scroll.AddChild(_list);
        sidebar.AddChild(scroll);
        var pages = new BoxContainer();
        sidebar.AddChild(pages);
        _previous = Button("previous"); _next = Button("next");
        pages.AddChild(_previous); pages.AddChild(_page); pages.AddChild(_next);
        _previous.OnPressed += _ => Load(_state.Page - 1);
        _next.OnPressed += _ => Load(_state.Page + 1);
        var viewer = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, HorizontalExpand = true };
        body.AddChild(viewer);
        viewer.AddChild(_image); viewer.AddChild(_details);
        _delete = Button("delete"); _confirm = Button("confirm");
        viewer.AddChild(_delete); viewer.AddChild(_confirm);
        _delete.OnPressed += _ => { _confirm.Visible = true; _delete.Visible = false; };
        _confirm.OnPressed += _ =>
        {
            if (_state.Selected is { } selected) SendMessage(new ArtModerationRequest("delete", selected.Id));
            _confirm.Disabled = true;
        };
        root.AddChild(_status);
        _window.OnClose += () => SendMessage(new CloseEuiMessage());
        Render();
    }

    public override void Opened() => _window.OpenCentered();

    public override void Closed()
    {
        _image.Texture = null;
        _texture?.Dispose(); _texture = null;
        _window.Dispose();
    }

    private void Load(int page) => SendMessage(new ArtModerationRequest("list", search: _search.Text, page: page));

    public override void HandleState(EuiStateBase state)
    {
        if (state is not ArtModerationEuiState art) return;
        _state = art;
        Render();
    }

    private void Render()
    {
        _list.DisposeAllChildren();
        foreach (var entry in _state.Entries)
        {
            var button = new Button
            {
                Text = $"{(entry.Title.Length == 0 ? Loc.GetString("dl-art-moderation-untitled") : entry.Title)}\n{entry.AuthorName}",
                ClipText = true, ToolTip = $"{entry.Title}\n{entry.AuthorName}\n{entry.Author}", Disabled = _state.Loading,
            };
            button.OnPressed += _ => SendMessage(new ArtModerationRequest("view", entry.Id));
            _list.AddChild(button);
        }
        if (_state.Entries.Count == 0) _list.AddChild(new Label { Text = Loc.GetString("dl-art-moderation-empty") });
        _page.Text = (_state.Page + 1).ToString();
        _previous.Disabled = _state.Loading || _state.Page == 0;
        _next.Disabled = _state.Loading || !_state.HasNext;
        _delete.Visible = _state.Selected != null;
        _delete.Disabled = _state.Loading;
        _confirm.Visible = false; _confirm.Disabled = _state.Loading;
        _details.DisposeAllChildren();
        if (_state.Selected is { } selected)
        {
            foreach (var text in new[] { selected.Title,
                Loc.GetString("dl-art-moderation-author", ("name", selected.AuthorName)),
                Loc.GetString("dl-art-moderation-account", ("name", _state.AccountName)),
                selected.Author.ToString(), selected.CreatedAt.ToString("yyyy-MM-dd HH:mm 'UTC'"),
                Loc.GetString("dl-art-moderation-id", ("id", selected.Id.ToString())) })
                _details.AddChild(new LineEdit { Text = text, Editable = false });
        }
        _status.Text = _state.Loading ? Loc.GetString("dl-art-moderation-loading")
            : _state.Status.Length == 0 ? "" : Loc.GetString(_state.Status);
        if (_png == _state.Image) return;
        _png = _state.Image;
        _image.Texture = null; _texture?.Dispose(); _texture = null;
        if (_png.Length == 0) return;
        using var input = new MemoryStream(Convert.FromBase64String(_png));
        using var image = Image.Load<Rgba32>(input);
        _texture = (OwnedTexture) Texture.LoadFromImage(image, "art-moderation", new TextureLoadParameters
            { Srgb = true, SampleParameters = new TextureSampleParameters { Filter = true } });
        _image.Texture = _texture;
    }
}
