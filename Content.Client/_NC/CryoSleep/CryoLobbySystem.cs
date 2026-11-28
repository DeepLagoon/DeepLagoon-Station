using static Content.Shared._NF.CryoSleep.SharedCryoSleepSystem;

namespace Content.Client._NF.CryoSleep;

public sealed class CryoLobbySystem : EntitySystem
{
    public event Action<CryoCharacterListResponse>?
        CharacterListReceived;

    public event Action<WakeupRequestMessage.Response>?
        WakeupReceived;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<CryoCharacterListResponse>(
            response => CharacterListReceived?.Invoke(response));

        SubscribeNetworkEvent<WakeupRequestMessage.Response>(
            response => WakeupReceived?.Invoke(response));
    }

    public void RequestCharacters()
    {
        RaiseNetworkEvent(new CryoCharacterListRequest());
    }

    public void Wakeup(Guid id)
    {
        RaiseNetworkEvent(new CryoCharacterWakeupRequest
        {
            Id = id
        });
    }
}
