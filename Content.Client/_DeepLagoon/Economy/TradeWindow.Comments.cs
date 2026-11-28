using System.Linq;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._DeepLagoon.Economy;

public sealed partial class TradeWindow
{
    private readonly bool _readOnly;
    private BoxContainer? _embeddedRoot;
    private string _commentText = "";
    public bool IsEmbedded => _embeddedRoot != null;
    public bool EmbeddedDisposed => _embeddedRoot?.Disposed == true;

    public void DisposeEmbedded() => _embeddedRoot?.Dispose();

    public Control Embed()
    {
        _embeddedRoot = Column(); _embeddedRoot.VerticalExpand = true;
        ContentsContainer.RemoveChild(_native); ContentsContainer.RemoveChild(_panel);
        var style = AddButton(_embeddedRoot, "TGUI / SS14", () =>
        {
            if (_native.Visible) _panel.SwitchToTgui(); else _panel.SwitchToNative();
        });
        style.HorizontalAlignment = HAlignment.Right;
        _embeddedRoot.AddChild(_native); _embeddedRoot.AddChild(_panel);
        _native.VerticalExpand = _panel.VerticalExpand = true;
        return _embeddedRoot;
    }

    private static string CommentTime(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value).ToLocalTime().ToString("dd.MM HH:mm");
    private void RenderComments(BoxContainer parent, Content.Shared._DeepLagoon.Economy.TradeOfferView offer)
    {
        Text(parent, L("comments"));
        if (_state.CommentOffer == offer.Id)
            foreach (var comment in _state.Comments)
            {
                Text(parent, comment.Author + " · " + CommentTime(comment.Time));
                Text(parent, comment.Text);
            }
        if (!_readOnly) return;
        if (!_state.CanComment) { Text(parent, L("comment-id")); return; }
        Text(parent, _state.CommentAuthor);
        var input = new LineEdit { Text = _commentText, HorizontalExpand = true };
        input.OnTextChanged += _ => _commentText = input.Text[..Math.Min(500, input.Text.Length)];
        input.OnTextEntered += _ => PostComment();
        parent.AddChild(input);
        AddButton(parent, L("comment-send"), PostComment).Disabled = _pending;
    }
    private void PostComment()
    {
        if (!_state.CanComment || _pending || Selected is not { } offer || string.IsNullOrWhiteSpace(_commentText)) return;
        var request = Query(Content.Shared._DeepLagoon.Economy.TradeAction.Comment);
        request.Offer = offer.Id; request.Description = _commentText.Trim();
        SendMutation(request);
        _commentText = "";
    }
}
