using Robust.Shared.Serialization;
using Content.Shared.Inventory;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager.Exceptions;

namespace Content.Shared.Chat.TypingIndicator;

/// <summary>
///     Networked event from client.
///     Send to server when client started/stopped typing in chat input field.
/// </summary>
[Serializable, NetSerializable]
public sealed class TypingChangedEvent : EntityEventArgs
{
    public readonly TypingIndicatorState State;

    public TypingChangedEvent(TypingIndicatorState state)
    {
        State = state;
    }
}

/// <summary>
///     This event will be broadcast right before displaying an entities typing indicator.
///     If _overrideIndicator is not null after the event is finished it will be used.
/// </summary>
[Serializable, NetSerializable]
public sealed class BeforeShowTypingIndicatorEvent : IInventoryRelayEvent
{
    public SlotFlags TargetSlots { get; } = SlotFlags.WITHOUT_POCKET;

    private ProtoId<TypingIndicatorPrototype>? _overrideIndicator = null;
    private TimeSpan? _latestEquipTime = null;
    private int _priority = int.MinValue;
    public BeforeShowTypingIndicatorEvent()
    {
        _overrideIndicator = null;
        _latestEquipTime = null;
    }
    /// <summary>
    ///     Prefer higher-priority clothing, then the most recently equipped item.
    /// </summary>
    ///  <returns>
    ///     True if this equipped item replaces the current override.
    ///  </returns>
    public bool TryUpdateTimeAndIndicator(ProtoId<TypingIndicatorPrototype>? indicator, TimeSpan? equipTime, int priority = 0)
    {
        if (equipTime != null && (priority > _priority || priority == _priority && (_latestEquipTime == null || _latestEquipTime < equipTime)))
        {
            _priority = priority;
            _latestEquipTime = equipTime;
            _overrideIndicator = indicator;
            return true;
        }
        return false;
    }
    public ProtoId<TypingIndicatorPrototype>? GetMostRecentIndicator()
    {
        return _overrideIndicator;
    }
}
