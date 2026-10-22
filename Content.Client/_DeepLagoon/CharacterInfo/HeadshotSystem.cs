using System.IO;
using System.Linq;
using Content.Client.Lobby;
using Content.Shared._DeepLagoon.CharacterInfo;
using Content.Shared.DetailExaminable;
using Content.Shared._DeepLagoon.DiscordLink;
using Content.Shared.Preferences;
using Robust.Client.UserInterface;
using Robust.Shared.Network;
using Robust.Shared.ContentPack;
using Robust.Shared.Utility;
using System.Text;

namespace Content.Client._DeepLagoon.CharacterInfo;
public sealed partial class HeadshotSystem : EntitySystem
{
    [Dependency] private IFileDialogManager _dialogs = default!;
    [Dependency] private IClientPreferencesManager _preferences = default!;
    [Dependency] private Robust.Client.Player.IPlayerManager _players = default!;
    [Dependency] private IClientNetManager _net = default!;
    [Dependency] private IResourceManager _resources = default!;
    private sealed class Pending(Action<byte[],string,string,string> callback)
    { public Action<byte[],string,string,string> Callback = callback; public MemoryStream Bytes = new(); public int Length = -1; }
    private readonly Dictionary<Guid, Pending> _pending = new();
    // Content hashes invalidate replacements even though the server keeps the profile's image ID.
    private sealed record CachedImage(byte[] Bytes, string Mime, string Hash) { public long LastUse; }
    private readonly Dictionary<string, CachedImage> _cache = new();
    private long _cacheUse;
    private const int CacheBudget = 32 * 1024 * 1024;
    private static readonly ResPath CacheDirectory = new("/headshot_cache");
    private string CacheKey(string id) => _net.ServerChannel?.RemoteEndPoint + "/" + id;
    private ResPath CachePath(string id) => CacheDirectory / (HeadshotLimits.ContentHash(Encoding.UTF8.GetBytes(CacheKey(id))) + ".cache");
    public bool Extended => EntityManager.System<SharedDiscordBoostSystem>().GetSupporterTier(_players.LocalSession) >= 2;
    public override void Initialize()
    {
        SubscribeNetworkEvent<HeadshotDataEvent>(Receive);
        SubscribeNetworkEvent<HeadshotGalleryDataEvent>((ev, _) => { if (_galleryRequests.Remove(ev.Request, out var callback)) callback(ev); });
        SubscribeNetworkEvent<CharacterInfoOpenEvent>((ev,_)=>
        {
            var entity=GetEntity(ev.Target);
            if (Exists(entity) && TryComp<DetailExaminableComponent>(entity,out var details))
                CharacterCardWindow.Open(entity, MetaData(entity).EntityName, details.Content, details.OocNotes, details.Erp, details.NonCon, details.Vore, -1);
        });
    }
    private Guid PendingRequest(Action<byte[],string,string,string> callback)
    { var id=Guid.NewGuid(); _pending[id]=new Pending(callback);
      Robust.Shared.Timing.Timer.Spawn(60000,()=> {_cachedRequests.Remove(id); if (_pending.Remove(id,out var pending)) {pending.Bytes.Dispose();pending.Callback([],"","","Истекло время ожидания изображения.");}});
      return id; }
    public void Read(int slot, NetEntity? target, Action<byte[],string,string,string> callback, string imageId = "")
    {
        var id = target is { } entity && TryComp<DetailExaminableComponent>(GetEntity(entity), out var details)
            ? details.HeadshotId
            : _preferences.Preferences?.Characters.TryGetValue(slot, out var profile) == true && profile is HumanoidCharacterProfile humanoid ? humanoid.HeadshotId : "";
        if (imageId.Length > 0) id = imageId;
        var cached = GetCached(id);
        if (cached != null) cached.LastUse = ++_cacheUse;
        var displayed = cached != null && target == null;
        if (displayed) callback(cached!.Bytes, cached.Mime, id, "");
        // The server still authorizes access; a matching hash needs only a tiny acknowledgement.
        var request = PendingRequest((bytes, mime, returnedId, error) =>
        {
            if (error.Length == 0 && bytes.Length > 0) Cache(returnedId, bytes, mime);
            if (!displayed || error.Length == 0 && (returnedId != id || !bytes.SequenceEqual(cached!.Bytes)))
                callback(bytes, mime, returnedId, error);
        });
        _cachedRequests[request] = (id, cached);
        RaiseNetworkEvent(new HeadshotRequestEvent(request, slot, target, cached?.Hash ?? "", imageId));
    }
    private readonly Dictionary<Guid, (string Id, CachedImage? Image)> _cachedRequests = new();

    public void Invalidate(string id)
    {
        if (!Guid.TryParse(id, out _)) return;
        _cache.Remove(CacheKey(id));
        try { _resources.UserData.Delete(CachePath(id)); } catch (Exception) { }
    }

    private void Cache(string id, byte[] bytes, string mime)
    {
        if (!Guid.TryParse(id, out _) || bytes.Length == 0) return;
        if (_cache.TryGetValue(CacheKey(id), out var existing) && existing.Bytes.SequenceEqual(bytes)) return;
        _cache[CacheKey(id)] = new CachedImage(bytes, mime, HeadshotLimits.ContentHash(bytes)) { LastUse = ++_cacheUse };
        while (_cache.Values.Sum(image => image.Bytes.Length) > CacheBudget)
            _cache.Remove(_cache.MinBy(entry => entry.Value.LastUse).Key);
        try
        {
            _resources.UserData.CreateDir(CacheDirectory);
            using (var file = _resources.UserData.Open(CachePath(id), FileMode.Create, FileAccess.Write, FileShare.None))
            {
                file.Write(BitConverter.GetBytes(DateTime.UtcNow.Ticks));
                file.WriteByte((byte)(mime switch { "image/png" => 1, "image/jpeg" => 2, "image/gif" => 3, _ => 0 }));
                file.Write(bytes);
            }
            // Keep persistent images bounded too, across servers and client restarts.
            var files = new List<(ResPath Path, long Age, long Size)>();
            foreach (var name in _resources.UserData.DirectoryEntries(CacheDirectory).Where(name => name.Length == 70 && name.EndsWith(".cache", StringComparison.Ordinal) && name[..64].All(Uri.IsHexDigit)).ToArray())
            {
                var path = CacheDirectory / name;
                var file = ReadCacheFile(path);
                if (file.Length < 10) { _resources.UserData.Delete(path); continue; }
                files.Add((path, BitConverter.ToInt64(file, 0), file.Length));
            }
            var total = files.Sum(file => file.Size);
            foreach (var file in files.OrderBy(file => file.Age))
            {
                if (total <= CacheBudget) break;
                _resources.UserData.Delete(file.Path); total -= file.Size;
            }
        }
        catch (Exception) { /* An unwritable cache must not prevent viewing or uploading a headshot. */ }
    }

    private CachedImage? GetCached(string id)
    {
        if (!Guid.TryParse(id, out _)) return null;
        if (_cache.TryGetValue(CacheKey(id), out var cached)) return cached;
        try
        {
            var file = ReadCacheFile(CachePath(id));
            if (file.Length < 10) return null;
            var mime = file[8] switch { 1 => "image/png", 2 => "image/jpeg", 3 => "image/gif", _ => "" };
            if (mime.Length == 0) return null;
            var bytes = file[9..];
            cached = new CachedImage(bytes, mime, HeadshotLimits.ContentHash(bytes)) { LastUse = ++_cacheUse };
            _cache[CacheKey(id)] = cached;
            while (_cache.Values.Sum(image => image.Bytes.Length) > CacheBudget)
                _cache.Remove(_cache.MinBy(entry => entry.Value.LastUse).Key);
            return cached;
        }
        catch (Exception) { return null; }
    }

    private byte[] ReadCacheFile(ResPath path)
    {
        // Some resource providers report stream position as Length. Count actual bytes instead.
        using var file = _resources.UserData.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var buffer = new MemoryStream();
        var chunk = new byte[HeadshotLimits.ChunkBytes]; int size;
        while ((size = file.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + size > HeadshotLimits.SponsorMaxBytes + 9) return [];
            buffer.Write(chunk, 0, size);
        }
        return buffer.ToArray();
    }
    private readonly Dictionary<Guid, Action<HeadshotGalleryDataEvent>> _galleryRequests = new();
    public void Gallery(int slot, NetEntity? target, Action<HeadshotGalleryDataEvent> callback, string action = "", string imageId = "")
    {
        var request = Guid.NewGuid(); _galleryRequests[request] = callback;
        Robust.Shared.Timing.Timer.Spawn(10000, () => { if (_galleryRequests.Remove(request, out var pending)) pending(new HeadshotGalleryDataEvent(request, [], [], 0, 0, "Истекло время ожидания библиотеки.")); });
        RaiseNetworkEvent(new HeadshotGalleryRequestEvent(request, slot, target, action, imageId));
    }
    public async void Upload(int slot, Action<byte[],string,string,string> callback, bool append = false, string imageId = "")
    {
        try
        {
            var extended = Extended;
            await using var file=await _dialogs.OpenFile(new FileDialogFilters(new FileDialogFilters.Group(extended ? ["png","jpg","jpeg","gif"] : ["png","jpg","jpeg"])),FileAccess.Read);
            if (file==null) { callback([],"","",""); return; }
            using var buffer=new MemoryStream(); var chunk=new byte[HeadshotLimits.ChunkBytes]; int size;
            while ((size=await file.ReadAsync(chunk))>0)
            { if (buffer.Length+size>(extended ? HeadshotLimits.SponsorMaxBytes : HeadshotLimits.MaxBytes)) { callback([],"","",$"Максимальный размер изображения — {(extended ? 5 : 1)} МБ."); return; } buffer.Write(chunk,0,size); }
            var bytes=buffer.ToArray(); if (bytes.Length==0) { callback([],"","","Пустой файл."); return; }
            var request=PendingRequest((data,mime,id,error)=>
            {
                if (error.Length==0 && _preferences.Preferences?.Characters.TryGetValue(slot,out var p)==true && p is HumanoidCharacterProfile profile) profile.HeadshotId=id;
                if (error.Length==0 && data.Length>0) { if (imageId.Length > 0) Invalidate(imageId); Cache(id,data,mime); }
                callback(data,mime,id,error);
            });
            RaiseNetworkEvent(new HeadshotBeginEvent(request,slot,bytes.Length,append,imageId));
            for (var offset=0;offset<bytes.Length;offset+=HeadshotLimits.ChunkBytes)
                RaiseNetworkEvent(new HeadshotUploadEvent(request,offset,bytes[offset..Math.Min(bytes.Length,offset+HeadshotLimits.ChunkBytes)]));
        }
        catch (Exception) { callback([],"","","Не удалось открыть изображение."); }
    }
    public async void Download(int slot)
    {
        Read(slot,null,async (bytes,mime,_,error)=>
        {
            if (error.Length>0 || bytes.Length==0) return;
            var output=await _dialogs.SaveFile(new FileDialogFilters(new FileDialogFilters.Group(mime switch { "image/jpeg" => "jpg", "image/gif" => "gif", _ => "png" })));
            if (output is {} destination) { await using var stream=destination.fileStream; await stream.WriteAsync(bytes); }
        });
    }
    private void Receive(HeadshotDataEvent ev, EntitySessionEventArgs _)
    {
        if (!_pending.TryGetValue(ev.Request,out var pending)) return;
        if (ev.NotModified)
        {
            _pending.Remove(ev.Request); pending.Bytes.Dispose();
            if (_cachedRequests.Remove(ev.Request, out var cached) && cached.Image != null && cached.Id == ev.Id)
                pending.Callback(cached.Image.Bytes, cached.Image.Mime, ev.Id, "");
            else pending.Callback([], "", "", "Кэш изображения недоступен.");
            return;
        }
        if (ev.Error.Length>0) { _pending.Remove(ev.Request); _cachedRequests.Remove(ev.Request); pending.Bytes.Dispose(); pending.Callback([],"","",ev.Error); return; }
        if (ev.Length is <0 or >HeadshotLimits.SponsorMaxBytes || ev.Offset!=pending.Bytes.Length || ev.Bytes.Length>HeadshotLimits.ChunkBytes || ev.Bytes.Length>ev.Length-ev.Offset || (pending.Length>=0 && pending.Length!=ev.Length))
        { _pending.Remove(ev.Request); _cachedRequests.Remove(ev.Request); pending.Bytes.Dispose(); pending.Callback([],"","","Передача изображения прервана."); return; }
        pending.Length=ev.Length; pending.Bytes.Write(ev.Bytes);
        if (pending.Bytes.Length!=ev.Length) return;
        _pending.Remove(ev.Request); _cachedRequests.Remove(ev.Request); var bytes=pending.Bytes.ToArray(); pending.Bytes.Dispose(); pending.Callback(bytes,ev.Mime,ev.Id,"");
    }
}
