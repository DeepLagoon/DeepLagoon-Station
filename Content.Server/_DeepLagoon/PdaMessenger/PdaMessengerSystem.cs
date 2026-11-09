using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Content.Server.Administration.Logs;
using Content.Server.Chat.Managers;
using Content.Server.Database;
using Content.Server.Examine;
using Content.Server.Preferences.Managers;
using Content.Shared._DeepLagoon.DiscordLink;
using Content.Shared._DeepLagoon.PdaMessenger;
using Content.Shared.Chat;
using Content.Shared.Database;
using Content.Shared.Examine;
using Content.Shared.GameTicking;
using Content.Shared.Ghost;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Mind;
using Content.Shared.Mobs.Systems;
using Content.Shared.PDA;
using Content.Shared.Players;
using Robust.Server.Player;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.ContentPack;
using Robust.Shared.Timing;
using Microsoft.Data.Sqlite;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

namespace Content.Server._DeepLagoon.PdaMessenger;

public sealed partial class PdaMessengerSystem : EntitySystem
{
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;
    [Dependency] private SharedDiscordBoostSystem _supporters = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private IChatManager _chat = default!;
    [Dependency] private IAdminLogManager _logs = default!;
    [Dependency] private IResourceManager _resources = default!;
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private SharedMindSystem _mind = default!;
    [Dependency] private ExamineSystem _examine = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private InventorySystem _inventory = default!;

    private const int ContactLimit = 64;
    private const float ReadNumberRange = 3f;
    private sealed class Address(int id, EntityUid mind, NetUserId user, HashSet<int> contacts)
    {
        public readonly int Id = id;
        public readonly EntityUid Mind = mind;
        public readonly NetUserId User = user;
        public readonly HashSet<int> Contacts = contacts;
        public readonly HashSet<int> Unread = new();
        public TimeSpan NextSend;
        public TimeSpan NextContactChange;
    }
    private sealed record Message(int Id, int From, int To, string Text, byte[]? Image);
    private sealed class View(EntityUid pda, Address address)
    {
        public readonly EntityUid Pda = pda;
        public readonly Address Address = address;
        public int Peer;
        public TimeSpan NextRead;
    }
    private sealed record Upload(View View, Address Sender, Address Recipient, string Text, byte[] Bytes, TimeSpan Expires)
    {
        public int Offset;
    }

    private readonly Dictionary<(EntityUid Mind, NetUserId User), Address> _addresses = new();
    private readonly Dictionary<int, Address> _roundNumbers = new();
    private readonly HashSet<EntityUid> _registering = new();
    private readonly Dictionary<NetUserId, View> _views = new();
    private readonly Dictionary<NetUserId, Upload> _uploads = new();
    private readonly List<Message> _messages = new();
    private PdaMessengerStore? _store;
    private PdaMessengerStore Store => _store ??= new PdaMessengerStore(_resources.UserData.RootDir is { } root
        ? Path.Combine(root, "pda-messenger.db") : ":memory:");
    private int _nextMessage;
    private int _imageBytes;
    private int _imageJobs;
    private int _generation;

    public override void Initialize()
    {
        SubscribeNetworkEvent<PdaMessengerRequest>(OnRequest);
        SubscribeNetworkEvent<PdaMessengerUploadChunk>(OnUpload);
        SubscribeLocalEvent<PlayerDetachedEvent>(OnDetach);
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnSpawn);
        SubscribeLocalEvent<PdaComponent, ExaminedEvent>(OnExamine);
        SubscribeLocalEvent<PdaComponent, BoundUIClosedEvent>(OnPdaClosed);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => ResetRound());
    }

    private void ResetRound()
    {
        _addresses.Clear();
        _roundNumbers.Clear();
        _registering.Clear();
        _views.Clear();
        _uploads.Clear();
        _messages.Clear();
        _imageBytes = 0;
        _generation++;
    }

    public override void Shutdown()
    {
        ResetRound();
        _store?.Dispose();
        base.Shutdown();
    }

    private async void OnSpawn(PlayerSpawnCompleteEvent args)
    {
        var player = args.Player;
        if (player.ContentData()?.Mind is not { } mind ||
            !ServerPreferencesManager.ShouldStorePrefs(player.Channel.AuthType) ||
            !_preferences.TryGetCachedPreferences(player.UserId, out var preferences))
            return;

        // Resolve the spawned profile now. Lobby selection can change while the player is in the round.
        var slot = preferences.Characters.FirstOrDefault(pair => ReferenceEquals(pair.Value, args.Profile));
        if (slot.Value == null || !_registering.Add(mind))
            return;

        var generation = _generation;
        try
        {
            var profile = await _database.GetCharacterProfileIdAsync(player.UserId, slot.Key);
            if (generation != _generation || !Exists(mind) || profile is not { } id)
                return;

            var number = Store.GetOrCreateNumber(player.UserId, id);
            var address = new Address(number, mind, player.UserId, Store.LoadContacts(number));
            _addresses[(mind, player.UserId)] = address;
            _roundNumbers[number] = address;
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to register PDA messenger line: {exception}");
        }
        finally
        {
            if (generation == _generation)
                _registering.Remove(mind);
        }
    }

    private void OnExamine(EntityUid uid, PdaComponent pda, ExaminedEvent args)
    {
        var holder = Transform(uid).ParentUid;
        // Inventory containers hide examine details even when a PDA is held out or worn.
        if (!args.IsInDetailsRange &&
            (!(_hands.IsHolding(holder, uid) || _inventory.TryGetSlotEntity(holder, "id", out var equipped) && equipped == uid)
             || !_examine.InRangeUnOccluded(args.Examiner, uid, ReadNumberRange)))
            return;

        int number;
        if (_mind.TryGetMind(holder, out _, out _))
        {
            number = GetExamineAddress(holder)?.Id ?? 0;
        }
        else if (_roundNumbers.ContainsKey(pda.MessengerNumber))
        {
            number = pda.MessengerNumber;
        }
        else
        {
            number = pda.PdaOwner is { } owner ? GetExamineAddress(owner)?.Id ?? 0 : 0;
        }

        if (number != 0)
            pda.MessengerNumber = number;
        args.PushMarkup(number == 0
            ? Loc.GetString("dl-messenger-examine-unassigned")
            : Loc.GetString("dl-messenger-number-line", ("number", number.ToString())));
    }

    private Address? GetExamineAddress(EntityUid body)
    {
        if (!_mind.TryGetMind(body, out var mindId, out var mind) || mind.OwnedEntity != body)
            return null;

        if (TryComp<ActorComponent>(body, out var actor) &&
            TryGetCharacter(actor.PlayerSession, out _, out var activeMind) && activeMind == mindId)
            return GetAddress(actor.PlayerSession, mindId);

        var user = mind.UserId ?? mind.OriginalOwnerUserId;
        return user is { } id ? _addresses.GetValueOrDefault((mindId, id)) : null;
    }

    private void OnPdaClosed(EntityUid uid, PdaComponent pda, BoundUIClosedEvent args)
    {
        if (args.UiKey is PdaUiKey.Key && TryComp<ActorComponent>(args.Actor, out var actor) &&
            _views.TryGetValue(actor.PlayerSession.UserId, out var view) && view.Pda == uid)
            OnDetach(actor.PlayerSession.UserId);
    }

    private void OnDetach(PlayerDetachedEvent args) => OnDetach(args.Player.UserId);

    private void OnDetach(NetUserId user)
    {
        _views.Remove(user);
        _uploads.Remove(user);
    }

    private bool TryGetCharacter(ICommonSession player, out EntityUid actor, out EntityUid mind)
    {
        actor = default;
        mind = default;
        if (player.AttachedEntity is not { } entity || player.ContentData()?.Mind is not { } character ||
            HasComp<GhostComponent>(entity) || _mobState.IsDead(entity) ||
            !TryComp<MindComponent>(character, out var playerMind) ||
            playerMind.OwnedEntity != entity || playerMind.UserId != player.UserId)
            return false;

        actor = entity;
        mind = character;
        return true;
    }

    private bool CarriesPda(EntityUid actor, EntityUid pda) =>
        HasComp<PdaComponent>(pda) && Transform(pda).ParentUid == actor;

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

    private Address? GetAddress(ICommonSession session, EntityUid mind)
    {
        if (_addresses.TryGetValue((mind, session.UserId), out var address))
            return address;
        if (_registering.Contains(mind))
            return null;

        // Guests and bodies without a saved character profile use a round-only line.
        int number;
        do
        {
            number = RandomNumberGenerator.GetInt32(PdaMessengerLimits.FirstTemporaryNumber, PdaMessengerLimits.LastNumber + 1);
        } while (_roundNumbers.ContainsKey(number));
        address = new Address(number, mind, session.UserId, []);
        _addresses.Add((mind, session.UserId), address);
        _roundNumbers.Add(number, address);
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
            pda is not { } device || !CarriesPda(actor, device) || !_ui.IsUiOpen(device, PdaUiKey.Key, actor))
            return;

        if (request.Action == PdaMessengerAction.Open)
        {
            if (GetAddress(session, mind) is not { } address)
                return;
            Comp<PdaComponent>(device).MessengerNumber = address.Id;
            if (_views.TryGetValue(session.UserId, out var current) && current.Pda == device && current.Address == address)
            {
                if (current.NextRead <= _timing.RealTime)
                {
                    current.NextRead = _timing.RealTime + TimeSpan.FromSeconds(0.25);
                    Publish(session, address, current);
                }
                return;
            }

            var opened = new View(device, address);
            _views[session.UserId] = opened;
            _uploads.Remove(session.UserId);
            Publish(session, address, opened);
            return;
        }

        if (!_views.TryGetValue(session.UserId, out var view) || view.Pda != device ||
            view.Address.Mind != mind)
            return;

        var sender = view.Address;
        if (request.Action == PdaMessengerAction.Close)
        {
            OnDetach(session.UserId);
            return;
        }

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

        if (request.Action is PdaMessengerAction.AddContact or PdaMessengerAction.RemoveContact)
        {
            ChangeContact(session, view, sender, request);
            return;
        }

        if (request.Action == PdaMessengerAction.Select && request.Target != sender.Id &&
            (sender.Contacts.Contains(request.Target) ||
             _messages.Any(message => message.From == request.Target && message.To == sender.Id)))
        {
            view.Peer = request.Target;
            sender.Unread.Remove(request.Target);
        }

        if (request.Action is PdaMessengerAction.Select or PdaMessengerAction.Refresh)
            Publish(session, sender, view);
    }

    private void ChangeContact(ICommonSession session, View view, Address sender, PdaMessengerRequest request)
    {
        if (sender.NextContactChange > _timing.RealTime)
            return;
        sender.NextContactChange = _timing.RealTime + TimeSpan.FromSeconds(0.5);

        var adding = request.Action == PdaMessengerAction.AddContact;
        var peer = request.Target;
        if (adding && !PdaMessengerLimits.TryParseNumber(request.Text, out peer))
        {
            Publish(session, sender, view, "dl-messenger-invalid-number");
            return;
        }
        if (peer == sender.Id)
        {
            Publish(session, sender, view, "dl-messenger-own-contact");
            return;
        }
        if (sender.Contacts.Contains(peer) == adding)
        {
            if (adding)
                Publish(session, sender, view, "dl-messenger-duplicate-contact");
            return;
        }
        if (adding && sender.Contacts.Count >= ContactLimit)
        {
            Publish(session, sender, view, "dl-messenger-contact-limit");
            return;
        }

        var temporary = sender.Id >= PdaMessengerLimits.FirstTemporaryNumber || peer >= PdaMessengerLimits.FirstTemporaryNumber;
        if (adding && peer >= PdaMessengerLimits.FirstTemporaryNumber && !_roundNumbers.ContainsKey(peer))
        {
            Publish(session, sender, view, "dl-messenger-unknown-temporary-number");
            return;
        }
        if (!temporary)
        {
            try
            {
                Store.SetContact(sender.Id, peer, adding);
            }
            catch (SqliteException exception)
            {
                Log.Error($"Failed to save PDA messenger contact: {exception}");
                Publish(session, sender, view, "dl-messenger-save-error");
                return;
            }
        }

        if (adding)
        {
            sender.Contacts.Add(peer);
            sender.Unread.Remove(peer);
            view.Peer = peer;
        }
        else
        {
            sender.Contacts.Remove(peer);
            if (view.Peer == peer)
                view.Peer = 0;
        }
        var status = adding
            ? temporary ? "dl-messenger-temporary-contact-added" : "dl-messenger-contact-added"
            : "dl-messenger-contact-removed";
        Publish(session, sender, view, status);
    }

    private void Send(ICommonSession session, View view, Address sender, PdaMessengerRequest request)
    {
        if (sender.NextSend > _timing.RealTime)
            return;
        sender.NextSend = _timing.RealTime + TimeSpan.FromSeconds(1);

        if (request.Target != view.Peer || !sender.Contacts.Contains(view.Peer) ||
            !_roundNumbers.TryGetValue(view.Peer, out var recipient) ||
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

        sender.NextSend = _timing.RealTime + TimeSpan.FromSeconds(4);
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
                !_ui.IsUiOpen(upload.View.Pda, PdaUiKey.Key, actor) ||
                !_views.TryGetValue(session.UserId, out var current) || current != upload.View ||
                !upload.Sender.Contacts.Contains(upload.Recipient.Id))
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
        if (_views.TryGetValue(listener.UserId, out var recipientView) && recipientView.Address == recipient &&
            CarriesPda(target, recipientView.Pda) && _ui.IsUiOpen(recipientView.Pda, PdaUiKey.Key, target))
        {
            if (recipientView.Peer == sender.Id)
                recipient.Unread.Remove(sender.Id);
            Publish(listener, recipient, recipientView);
        }

        var notice = Loc.GetString("dl-messenger-notification", ("number", sender.Id.ToString()));
        _chat.ChatMessageToOne(ChatChannel.Notifications, "", notice, EntityUid.Invalid, false, listener.Channel);
    }

    private static bool InConversation(Message message, int first, int second) =>
        message.From == first && message.To == second || message.To == first && message.From == second;

    private void Publish(ICommonSession session, Address address, View view, string status = "")
    {
        if (!session.Channel.IsConnected || !TryGetCharacter(session, out var actor, out var mind) ||
            mind != address.Mind || !CarriesPda(actor, view.Pda) || !_ui.IsUiOpen(view.Pda, PdaUiKey.Key, actor) ||
            !_views.TryGetValue(session.UserId, out var current) || current != view)
            return;

        var incoming = _messages.Where(message => message.To == address.Id).Select(message => message.From);
        var contacts = address.Contacts.Union(incoming).Select(id => new PdaMessengerContact(id,
            address.Contacts.Contains(id), _roundNumbers.TryGetValue(id, out var peer) && TryGetOnline(peer, out _),
            address.Unread.Contains(id)));

        var history = _messages.Where(message => InConversation(message, address.Id, view.Peer)).TakeLast(40)
            .Select(message => new PdaMessengerMessage(message.Id, message.From == address.Id, message.Text, message.Image != null))
            .ToArray();
        var directory = contacts.OrderByDescending(contact => contact.Unread).ThenBy(contact => contact.Id).ToArray();
        RaiseNetworkEvent(new PdaMessengerSnapshot(GetNetEntity(view.Pda), address.Id, view.Peer, CanAttach(session), directory, history,
            status.Length == 0 ? "" : Loc.GetString(status), status == "dl-messenger-sent",
            status is "dl-messenger-contact-added" or "dl-messenger-temporary-contact-added"), session);
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
