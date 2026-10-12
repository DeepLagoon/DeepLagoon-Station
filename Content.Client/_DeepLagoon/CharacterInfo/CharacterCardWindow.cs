using System.Numerics;
using System.Linq;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.UserInterface.Controls;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Enums;
using Robust.Shared.Maths;

namespace Content.Client._DeepLagoon.CharacterInfo;
public sealed class CharacterCardWindow : FancyWindow
{
    private readonly EntityUid _entity;
    private readonly int _slot;
    private readonly TguiPanel _panel = new();
    private readonly SpriteView _sprite = new() { Stretch = SpriteView.StretchMode.Fill, OverrideDirection = Direction.South, MouseFilter = MouseFilterMode.Ignore };
    private static readonly Direction[] Directions = { Direction.South, Direction.West, Direction.North, Direction.East };
    private int _direction;
    private readonly CardHost _host;
    // TguiData only appends fields: the state is rebuilt from the base fields on every publish.
    private readonly Func<TguiData> _baseData;
    private string? _headshot;
    private string[] _gallery = [];
    private readonly Dictionary<string, string> _headshots = new();
    private CharacterCardWindow(EntityUid entity, string name, string flavor, string ooc, byte erp, byte nonCon, byte vore, int slot)
    {
        _entity = entity;
        _slot = slot;
        Title=Loc.GetString("dl-character-card-title"); SetSize=new Vector2(950,740); MinSize=new Vector2(640,600);
        _sprite.SetEntity(entity);
        _baseData=()=>new TguiData().String("name",name).String("flavor",flavor).String("ooc",ooc).Number("erp",erp).Number("noncon",nonCon).Number("vore",vore);
        _host=new CardHost(_panel,_sprite); ContentsContainer.AddChild(_host);
        _panel.OnAction += (action,_)=>
        {
            if(action=="close")Close();
            else if (action is "char_left" or "char_right")
            {
                _direction = (_direction + (action == "char_left" ? 3 : 1)) % Directions.Length;
                _sprite.OverrideDirection = Directions[_direction];
                _sprite.InvalidateMeasure();
            }
        };
        Publish();
        OnClose+=Dispose;
    }
    private void Publish()
    {
        var data = _baseData();
        if (_headshot != null) data.String("headshot", _headshot);
        if (_headshots.Count > 0)
            data.Array("headshots", _gallery.Where(_headshots.ContainsKey).Select(image => new TguiData().String("id", image).String("image", _headshots[image])));
        _panel.SetState("CharacterCard", data.ToString());
    }
    public static void Open(EntityUid entity,string name,string flavor,string ooc,byte erp,byte nonCon,byte vore,int slot)
    {
        var existing = IoCManager.Resolve<IUserInterfaceManager>().WindowRoot.Children
            .OfType<CharacterCardWindow>().FirstOrDefault(window => !window.Disposed && window._entity == entity && window._slot == slot);
        if (existing != null)
        {
            existing.MoveToFront();
            return;
        }
        var window=new CharacterCardWindow(entity,name,flavor,ooc,erp,nonCon,vore,slot);window.OpenCentered();
        var entities=IoCManager.Resolve<IEntityManager>();
        entities.System<HeadshotSystem>().Read(slot,slot<0?entities.GetNetEntity(entity):null,(bytes,mime,_,error)=>
        {
            if(window.Disposed)return;
            if(bytes.Length>0 && error.Length==0)
            {
                window._headshot="data:"+mime+";base64,"+Convert.ToBase64String(bytes);
                window._host.HasHeadshot=true;window._host.InvalidateArrange();
                window.Publish();
            }
        });
        entities.System<HeadshotSystem>().Gallery(slot, slot < 0 ? entities.GetNetEntity(entity) : null, gallery =>
        {
            if (window.Disposed || gallery.Error.Length > 0) return;
            foreach (var (id, index) in gallery.Active.Select((id, index) => (id, index)))
                Robust.Shared.Timing.Timer.Spawn((index + 1) * 350, () =>
                {
                    if (window.Disposed) return;
                    entities.System<HeadshotSystem>().Read(slot, slot < 0 ? entities.GetNetEntity(entity) : null, (bytes, mime, returnedId, error) =>
                    {
                        if (window.Disposed || error.Length > 0 || bytes.Length == 0 || returnedId != id) return;
                        window._headshots[id] = "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
                        window._gallery = gallery.Active;
                        window._host.HasHeadshot = true; window._host.InvalidateArrange();
                        window.Publish();
                    }, id);
                });
        });
    }
    private sealed class CardHost : Container
    {
        private readonly TguiPanel _panel;private readonly Control _sprite;public bool HasHeadshot;
        public CardHost(TguiPanel panel,Control sprite){_panel=panel;_sprite=sprite;HorizontalExpand=VerticalExpand=true;AddChild(panel);AddChild(sprite);}
        protected override Vector2 MeasureOverride(Vector2 available){_panel.Measure(available);_sprite.Measure(new Vector2(256,256));return Vector2.Zero;}
        protected override Vector2 ArrangeOverride(Vector2 final)
        {
            _panel.Arrange(UIBox2.FromDimensions(Vector2.Zero,final));
            // Match CharacterCard.scss. Fill the viewport with 12px of breathing
            // room, including tall/wide species, rather than a fixed 3x scale.
            var height = Math.Clamp(final.Y - (HasHeadshot ? 426 : 98), 128, 256);
            var position = new Vector2(22, HasHeadshot ? 372 : 44);
            _sprite.Arrange(UIBox2.FromDimensions(position + new Vector2(12),new Vector2(232,height - 24)));
            return final;
        }
    }
}
