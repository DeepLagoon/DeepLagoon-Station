using Content.Shared.Abilities.Psionics;
using Content.Shared.Actions.Events;
using Content.Shared.Shadowkin;
using Content.Shared.Physics;
using Content.Shared.Popups;
using Content.Shared.Maps;
using Robust.Server.GameObjects;

namespace Content.Server.Abilities.Psionics
{
    public sealed partial class DarkSwapSystem : EntitySystem
    {
        [Dependency] private SharedPsionicAbilitiesSystem _psionics = default!;
        [Dependency] private SharedPopupSystem _popup = default!;
        [Dependency] private PhysicsSystem _physics = default!;

        public override void Initialize()
        {
            base.Initialize();
            SubscribeLocalEvent<DarkSwapActionEvent>(OnPowerUsed);
        }

        private void OnPowerUsed(DarkSwapActionEvent args)
        {
            if (TryComp<EtherealComponent>(args.Performer, out var ethereal))
            {
                if (_physics.GetEntitiesIntersectingBody(args.Performer, (int) CollisionGroup.Impassable).Count > 0)
                {
                    _popup.PopupEntity(Loc.GetString("revenant-in-solid"), args.Performer, args.Performer);
                    return;
                }

                if (_psionics.OnAttemptPowerUse(args.Performer, "DarkSwap", true))
                {
                    SpawnAtPosition("ShadowkinShadow", Transform(args.Performer).Coordinates);
                    SpawnAtPosition("EffectFlashShadowkinDarkSwapOff", Transform(args.Performer).Coordinates);
                    RemComp(args.Performer, ethereal);
                    args.Handled = true;
                }
            }
            else if (_psionics.OnAttemptPowerUse(args.Performer, "DarkSwap", true))
            {
                var newethereal = EnsureComp<EtherealComponent>(args.Performer);
                newethereal.Darken = true;

                SpawnAtPosition("ShadowkinShadow", Transform(args.Performer).Coordinates);
                SpawnAtPosition("EffectFlashShadowkinDarkSwapOn", Transform(args.Performer).Coordinates);

                args.Handled = true;
            }

            if (args.Handled)
                _psionics.LogPowerUsed(args.Performer, "DarkSwap");
        }
    }
}
