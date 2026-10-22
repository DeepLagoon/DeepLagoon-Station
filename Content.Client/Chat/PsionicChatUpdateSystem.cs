using Content.Shared.Abilities.Psionics;
using Content.Client.Chat.Managers;
using Robust.Client.Player;

namespace Content.Client.Chat
{
    public sealed partial class PsionicChatUpdateSystem : EntitySystem
    {
        [Dependency] private IChatManager _chatManager = default!;
        [Dependency] private IPlayerManager _playerManager = default!;

        public override void Initialize()
        {
            base.Initialize();
            SubscribeLocalEvent<TelepathyComponent, ComponentInit>(OnInit);
            SubscribeLocalEvent<TelepathyComponent, ComponentShutdown>(OnRemove);
            SubscribeLocalEvent<PsionicComponent, ComponentStartup>(OnPsionicInit);
            SubscribeLocalEvent<PsionicComponent, ComponentShutdown>(OnPsionicRemove);
        }

        public PsionicComponent? Player => CompOrNull<PsionicComponent>(_playerManager.LocalPlayer?.ControlledEntity);
        public bool IsPsionic => Player is { LifeStage: <= ComponentLifeStage.Running };
        public bool HasTelepathy => CompOrNull<TelepathyComponent>(_playerManager.LocalPlayer?.ControlledEntity)
            is { LifeStage: <= ComponentLifeStage.Running };

        private void OnInit(EntityUid uid, TelepathyComponent component, ComponentInit args)
        {
            if (uid == _playerManager.LocalEntity)
                _chatManager.UpdatePermissions();
        }

        private void OnRemove(EntityUid uid, TelepathyComponent component, ComponentShutdown args)
        {
            if (uid == _playerManager.LocalEntity)
                _chatManager.UpdatePermissions();
        }

        private void OnPsionicInit(EntityUid uid, PsionicComponent component, ComponentStartup args)
        {
            if (uid == _playerManager.LocalEntity)
                _chatManager.UpdatePermissions();
        }

        private void OnPsionicRemove(EntityUid uid, PsionicComponent component, ComponentShutdown args)
        {
            if (uid == _playerManager.LocalEntity)
                _chatManager.UpdatePermissions();
        }
    }
}
