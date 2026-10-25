using System.Linq;
using System.Threading.Tasks;
using Content.Server.Administration;
using Content.Server.Administration.Managers;
using Content.Server.Database;
using Content.Server.EUI;
using Content.Shared._DeepLagoon.Art;
using Content.Shared.Administration;
using Content.Shared.Eui;
using Robust.Shared.Network;

namespace Content.Server._DeepLagoon.Art;

public sealed class ArtModerationEui : BaseEui
{
    [Dependency] private IAdminManager _admins = default!;
    [Dependency] private IServerDbManager _db = default!;
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private ILogManager _logs = default!;
    private ArtModerationEuiState _state = new();
    private string _search = "";
    private bool Authorized => !IsShutDown && _admins.HasAdminFlag(Player, AdminFlags.None);

    public ArtModerationEui() => IoCManager.InjectDependencies(this);

    public override void Opened()
    {
        _admins.OnPermsChanged += PermissionsChanged;
        HandleMessage(new ArtModerationRequest("list"));
    }

    public override void Closed() => _admins.OnPermsChanged -= PermissionsChanged;

    private void PermissionsChanged(AdminPermsChangedEventArgs args)
    {
        if (args.Player == Player && !Authorized) Close();
    }

    public override EuiStateBase GetNewState() => Authorized ? _state : new ArtModerationEuiState();

    private static ArtModerationEntry Entry(ArchivedArtwork work) => new()
    {
        Id = work.Id, Title = work.Title, AuthorName = work.AuthorName,
        Author = work.Author, CreatedAt = work.CreatedAt,
    };

    private async Task LoadList()
    {
        var rows = await _db.ListArtworksAsync(_search, _state.Page);
        if (!Authorized) return;
        _state.HasNext = rows.Count > ArtModerationEuiState.PageSize;
        _state.Entries = rows.Take(ArtModerationEuiState.PageSize).Select(Entry).ToList();
        if (_state.Selected is { } selected && await _db.GetArtworkAsync(selected.Id) == null)
        {
            _state.Selected = null;
            _state.Image = "";
        }
    }

    public override async void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);
        if (!Authorized || _state.Loading || msg is not ArtModerationRequest request) return;
        if (request.Action is not ("list" or "view" or "delete") || request.Search == null || request.Search.Length > 128) return;
        _state.Loading = true;
        _state.Status = "";
        StateDirty();
        try
        {
            if (request.Action == "list")
            {
                _search = request.Search;
                _state.Page = Math.Clamp(request.Page, 0, 100000);
                await LoadList();
            }
            else
            {
                if (request.Action == "delete" && _state.Selected?.Id != request.Id) return;
                var work = await _db.GetArtworkAsync(request.Id);
                if (!Authorized) return;
                if (work == null)
                {
                    _state.Selected = null; _state.Image = "";
                    _state.Status = "dl-art-moderation-missing";
                    await LoadList();
                    return;
                }
                if (request.Action == "view")
                {
                    var baked = await Task.Run(() => ArtDocument.Bake(work.Document, fullResolution: true));
                    var author = await _db.GetPlayerRecordByUserId(new NetUserId(work.Author));
                    if (!Authorized) return;
                    // A different moderator may have removed it during composition.
                    if (await _db.GetArtworkAsync(work.Id) == null || !Authorized) return;
                    _state.Selected = Entry(work);
                    _state.AccountName = author?.LastSeenUserName ?? "";
                    _state.Image = baked.Preview;
                }
                else
                {
                    if (!await _db.DeleteArtworkAsync(work.Id))
                        _state.Status = "dl-art-moderation-missing";
                    else
                    {
                        _entities.System<ArtSystem>().RemoveArchivedArtwork(work.Id, work.WorkId, work.RevisionHash);
                        _logs.GetSawmill("admin.art").Info($"{Player.Name} ({Player.UserId}) removed artwork {work.Id}, work {work.WorkId}, revision {work.RevisionHash}, author {work.Author} ({work.AuthorName})");
                        _state.Status = "dl-art-moderation-removed";
                    }
                    _state.Selected = null; _state.Image = "";
                    await LoadList();
                    if (_state.Entries.Count == 0 && _state.Page > 0) { _state.Page--; await LoadList(); }
                }
            }
        }
        catch (Exception e)
        {
            _logs.GetSawmill("admin.art").Error($"Artwork moderation failed: {e.GetType().Name}");
            _state.Status = "dl-art-moderation-error";
        }
        finally
        {
            _state.Loading = false;
            if (Authorized) StateDirty();
        }
    }
}
