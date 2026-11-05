using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Administration.Logs;
using Content.Server.Chat.Managers;
using Content.Shared._DeepLagoon.DiscordLink;
using Content.Shared._DeepLagoon.PdaMessenger;
using Content.Shared.Chat;
using Content.Shared.Database;
using Content.Shared.GameTicking;
using Content.Shared.Ghost;
using Content.Shared.IdentityManagement;
using Content.Shared.Mobs.Systems;
using Content.Shared.PDA;
using Content.Shared.Players;
using Robust.Server.Player;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

namespace Content.Server._DeepLagoon.PdaMessenger;

public sealed class PdaMessengerSystem : EntitySystem
{
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;
    [Dependency] private SharedDiscordBoostSystem _supporters = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private IChatManager _chat = default!;
    [Dependency] private IAdminLogManager _logs = default!;

    private sealed record Address(int Id, EntityUid Mind, NetUserId User)
    {
        public string Name = "";
        public readonly HashSet<int> Unread = new();
    }
    private sealed record Message(int Id, int From, int To, string Text, byte[]? Image);
    private sealed record View(EntityUid Pda)
    {
        public int Peer;
        public bool Closed;
        public TimeSpan NextRead;
        public TimeSpan NextSend;
    }
    private sealed record Upload(View View, Address Sender, Address Recipient, string Text, byte[] Bytes, TimeSpan Expires)
    {
        public int Offset;
    }

    private readonly Dictionary<EntityUid, Address> _addresses = new();
    private readonly Dictionary<int, Address> _directory = new();
    private readonly Dictionary<NetUserId, View> _views = new();
    private readonly Dictionary<NetUserId, Upload> _uploads = new();
    private readonly List<Message> _messages = new();
    private int _nextAddress;
    private int _nextMessage;
    private int _imageBytes;
    private int _imageJobs;
    private int _generation;

    public override void Initialize()
    {
        SubscribeNetworkEvent<PdaMessengerRequest>(OnRequest);
        SubscribeNetworkEvent<PdaMessengerUploadChunk>(OnUpload);
        SubscribeLocalEvent<PlayerDetachedEvent>(OnDetach);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ =>
        {
            _addresses.Clear();
            _directory.Clear();
            _views.Clear();
            _uploads.Clear();
            _messages.Clear();
            _imageBytes = 0;
            _generation++;
        });
    }

    private void OnDetach(PlayerDetachedEvent args)
    {
        _views.Remove(args.Player.UserId);
        _uploads.Remove(args.Player.UserId);
    }

    private bool TryGetCharacter(ICommonSession player, out EntityUid actor, out EntityUid mind)
    {
        actor = default;
        mind = default;
        if (player.AttachedEntity is not { } entity || player.ContentData()?.Mind is not { } character ||
            HasComp<GhostComponent>(entity) || _mobState.IsDead(entity))
            return false;

        actor = entity;
        mind = character;
        return true;
    }

    private bool CarriesPda(EntityUid actor, EntityUid pda) =>
        HasComp<PdaComponent>(pda) && TryComp<TransformComponent>(pda, out var transform) && transform.ParentUid == actor;

    private bool HasPda(EntityUid actor)
    {
        var children = Transform(actor).ChildEnumerator;
        while (children.MoveNext(out var child))
        {
            if (HasComp<PdaComponent>(child))
                return true;
        }
        return false;
    }

    private Address GetAddress(ICommonSession session, EntityUid actor, EntityUid mind)
    {
        if (!_addresses.TryGetValue(mind, out var address))
        {
            address = new Address(++_nextAddress, mind, session.UserId);
            _addresses.Add(mind, address);
            _directory.Add(address.Id, address);
        }
        address.Name = Name(Identity.Entity(actor, EntityManager));
        return address;
    }

    private bool TryGetOnline(Address address, out ICommonSession? session)
    {
        session = null;
        if (!_players.TryGetSessionById(address.User, out var player) || !TryGetCharacter(player, out var actor, out var mind)
            || mind != address.Mind || !HasPda(actor))
            return false;
        session = player;
        return true;
    }

    private bool CanAttach(ICommonSession session) => _supporters.GetSupporterTier(session) > 0;

    private void OnRequest(PdaMessengerRequest request, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (!TryGetCharacter(session, out var actor, out var mind) || !TryGetEntity(request.Pda, out var pda) ||
            pda is not { } device || !CarriesPda(actor, device))
            return;

        if (request.Action == PdaMessengerAction.Open)
        {
            if (!_ui.IsUiOpen(device, PdaUiKey.Key, actor))
                return;
            if (_views.TryGetValue(session.UserId, out var current) && current.NextRead > _timing.RealTime)
                return;

            _views[session.UserId] = new View(device)
            {
                NextRead = _timing.RealTime + TimeSpan.FromSeconds(0.25),
                NextSend = current?.NextSend ?? TimeSpan.Zero,
            };
            _uploads.Remove(session.UserId);
            Publish(session, GetAddress(session, actor, mind), _views[session.UserId]);
            return;
        }

        if (!_views.TryGetValue(session.UserId, out var view) || view.Pda != device ||
            !_addresses.TryGetValue(mind, out var sender))
            return;

        if (request.Action == PdaMessengerAction.Close)
        {
            view.Closed = true;
            _uploads.Remove(session.UserId);
            return;
        }

        if (view.Closed)
            return;

        if (request.Action == PdaMessengerAction.Send)
        {
            Send(session, view, sender, request);
            return;
        }

        if (view.NextRead > _timing.RealTime)
            return;
        view.NextRead = _timing.RealTime + TimeSpan.FromSeconds(0.25);

        if (request.Action == PdaMessengerAction.ReadImage)
        {
            SendImage(session, view, sender, request.Target);
            return;
        }

        if (request.Action == PdaMessengerAction.Select && request.Target != sender.Id &&
            (_directory.TryGetValue(request.Target, out var peer) && TryGetOnline(peer, out _) ||
             _messages.Any(message => InConversation(message, sender.Id, request.Target))))
        {
            view.Peer = request.Target;
            sender.Unread.Remove(request.Target);
        }

        if (request.Action is PdaMessengerAction.Select or PdaMessengerAction.Refresh)
            Publish(session, sender, view);
    }

    private void Send(ICommonSession session, View view, Address sender, PdaMessengerRequest request)
    {
        if (view.NextSend > _timing.RealTime)
            return;
        view.NextSend = _timing.RealTime + TimeSpan.FromSeconds(1);

        if (request.Target != view.Peer || !_directory.TryGetValue(view.Peer, out var recipient) ||
            !TryGetOnline(recipient, out _))
        {
            Publish(session, sender, view, "dl-messenger-offline");
            return;
        }

        var text = request.Text?.Trim() ?? "";
        if (text.Length > PdaMessengerLimits.TextLength || text.Length == 0 && request.UploadLength == 0)
            return;

        if (request.UploadLength == 0)
        {
            Deliver(session, sender, recipient, text, null);
            return;
        }

        foreach (var expired in _uploads.Where(pair => pair.Value.Expires < _timing.RealTime).Select(pair => pair.Key).ToArray())
            _uploads.Remove(expired);

        if (!CanAttach(session) || request.UploadLength is < 1 or > PdaMessengerLimits.ImageBytes ||
            _uploads.Count >= 8 || _uploads.ContainsKey(session.UserId))
        {
            Publish(session, sender, view, "dl-messenger-attachment-error");
            return;
        }

        view.NextSend = _timing.RealTime + TimeSpan.FromSeconds(4);
        _uploads[session.UserId] = new Upload(view, sender, recipient, text,
            new byte[request.UploadLength], _timing.RealTime + TimeSpan.FromSeconds(20));
    }

    private async void OnUpload(PdaMessengerUploadChunk chunk, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (!_uploads.TryGetValue(session.UserId, out var upload) ||
            !TryGetEntity(chunk.Pda, out var pda) || pda != upload.View.Pda)
            return;

        if (upload.Expires < _timing.RealTime || chunk.Offset != upload.Offset ||
            chunk.Bytes is not { Length: > 0 and <= PdaMessengerLimits.ChunkBytes } bytes ||
            bytes.Length > upload.Bytes.Length - upload.Offset)
        {
            _uploads.Remove(session.UserId);
            return;
        }

        bytes.CopyTo(upload.Bytes, upload.Offset);
        upload.Offset += bytes.Length;
        if (upload.Offset != upload.Bytes.Length)
            return;

        _uploads.Remove(session.UserId);
        if (_imageJobs >= 2)
        {
            Publish(session, upload.Sender, upload.View, "dl-messenger-attachment-error");
            return;
        }

        var generation = _generation;
        _imageJobs++;
        try
        {
            var image = await Task.Run(() => Normalize(upload.Bytes));
            if (generation != _generation || !session.Channel.IsConnected ||
                !TryGetCharacter(session, out var actor, out var mind) || mind != upload.Sender.Mind ||
                !CarriesPda(actor, upload.View.Pda) || !CanAttach(session) ||
                !_views.TryGetValue(session.UserId, out var current) || !ReferenceEquals(current, upload.View) || current.Closed)
                return;

            if (image == null || _imageBytes + image.Length > 12 * 1024 * 1024 || !TryGetOnline(upload.Recipient, out _))
            {
                Publish(session, upload.Sender, upload.View, "dl-messenger-attachment-error");
                return;
            }

            Deliver(session, upload.Sender, upload.Recipient, upload.Text, image);
        }
        finally
        {
            _imageJobs--;
        }
    }

    private static byte[]? Normalize(byte[] bytes)
    {
        try
        {
            var info = Image.Identify(bytes);
            var mime = info.Metadata.DecodedImageFormat?.DefaultMimeType;
            if (mime is not ("image/png" or "image/jpeg") ||
                info.Width > PdaMessengerLimits.ImageDimension || info.Height > PdaMessengerLimits.ImageDimension ||
                info.FrameMetadataCollection.Count > 1)
                return null;

            // Canonical PNG with no source metadata, sized to the in-game preview and transport budget.
            using var image = Image.Load(bytes);
            if (image.Width > 448 || image.Height > 448)
                image.Mutate(operation => operation.Resize(new ResizeOptions { Size = new Size(448, 448), Mode = ResizeMode.Max }));
            using var stream = new MemoryStream();
            image.SaveAsPng(stream, new PngEncoder { ColorType = PngColorType.Palette, SkipMetadata = true });
            return stream.Length <= PdaMessengerLimits.ImageBytes ? stream.ToArray() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Deliver(ICommonSession session, Address sender, Address recipient, string text, byte[]? image)
    {
        _messages.Add(new Message(++_nextMessage, sender.Id, recipient.Id, text, image));
        _imageBytes += image?.Length ?? 0;
        if (_messages.Count > 2000)
        {
            _imageBytes -= _messages[0].Image?.Length ?? 0;
            _messages.RemoveAt(0);
        }

        var attachment = image != null ? " [image]" : "";
        _logs.Add(LogType.PdaInteract, LogImpact.Low,
            $"PDA message from {ToPrettyString(sender.Mind):user} to {ToPrettyString(recipient.Mind):target}: {text}{attachment}");

        if (_views.TryGetValue(session.UserId, out var senderView))
            Publish(session, sender, senderView, "dl-messenger-sent");

        if (!TryGetOnline(recipient, out var listener) || listener?.AttachedEntity is not { } target)
            return;

        recipient.Unread.Add(sender.Id);
        if (_views.TryGetValue(listener.UserId, out var recipientView) && !recipientView.Closed && CarriesPda(target, recipientView.Pda))
        {
            if (recipientView.Peer == sender.Id)
                recipient.Unread.Remove(sender.Id);
            Publish(listener, recipient, recipientView);
        }

        var notice = Loc.GetString("dl-messenger-notification", ("name", FormattedMessage.EscapeText(sender.Name)));
        _chat.ChatMessageToOne(ChatChannel.Notifications, "", notice, EntityUid.Invalid, false, listener.Channel);
    }

    private static bool InConversation(Message message, int first, int second) =>
        message.From == first && message.To == second || message.To == first && message.From == second;

    private void Publish(ICommonSession session, Address address, View view, string status = "")
    {
        if (view.Closed || !session.Channel.IsConnected || !TryGetCharacter(session, out var actor, out var mind) ||
            mind != address.Mind || !CarriesPda(actor, view.Pda) ||
            !_views.TryGetValue(session.UserId, out var current) || current != view)
            return;

        var contacts = new Dictionary<int, PdaMessengerContact>();
        foreach (var other in _players.Sessions)
        {
            if (other == session || !TryGetCharacter(other, out var target, out var targetMind) || !HasPda(target))
                continue;

            var peer = GetAddress(other, target, targetMind);
            contacts[peer.Id] = new PdaMessengerContact(peer.Id, peer.Name, true, address.Unread.Contains(peer.Id));
        }
        foreach (var message in _messages)
        {
            var peerId = message.From == address.Id ? message.To : message.To == address.Id ? message.From : 0;
            if (peerId != 0 && !contacts.ContainsKey(peerId) && _directory.TryGetValue(peerId, out var peer))
                contacts.Add(peerId, new PdaMessengerContact(peerId, peer.Name, false, address.Unread.Contains(peerId)));
        }

        var history = _messages.Where(message => InConversation(message, address.Id, view.Peer)).TakeLast(40)
            .Select(message => new PdaMessengerMessage(message.Id, message.From == address.Id, message.Text, message.Image != null))
            .ToArray();
        var directory = contacts.Values.OrderByDescending(contact => contact.Unread).ThenBy(contact => contact.Name).ToArray();
        RaiseNetworkEvent(new PdaMessengerSnapshot(GetNetEntity(view.Pda), view.Peer, CanAttach(session), directory, history,
            status.Length == 0 ? "" : Loc.GetString(status), status == "dl-messenger-sent"), session);
    }

    private void SendImage(ICommonSession session, View view, Address address, int messageId)
    {
        var message = _messages.Find(item => item.Id == messageId && InConversation(item, address.Id, view.Peer));
        if (message?.Image is not { } image)
            return;
        view.NextRead = _timing.RealTime + TimeSpan.FromSeconds(1);

        for (var offset = 0; offset < image.Length; offset += PdaMessengerLimits.ChunkBytes)
        {
            var length = Math.Min(PdaMessengerLimits.ChunkBytes, image.Length - offset);
            RaiseNetworkEvent(new PdaMessengerImageChunk(GetNetEntity(view.Pda), messageId, image.Length, offset,
                image.AsSpan(offset, length).ToArray()), session);
        }
    }
}
