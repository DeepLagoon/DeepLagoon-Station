using Content.Shared._Mono.FireControl;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.Timing;
using System.Linq;
using Content.Client._DeepLagoon.WebUI;

namespace Content.Client._Mono.FireControl.UI;

public sealed partial class FireControlPanel
{
    [Dependency] private IGameTiming _reloadTiming = default!;
    private readonly Dictionary<NetEntity, FireControllableEntry> _weaponStates = new();

    private TguiData WeaponState(NetEntity entity, Robust.Client.UserInterface.Controls.Button button)
    {
        _weaponStates.TryGetValue(entity, out var entry);
        var next = entry.NextFire;
        var duration = entry.ReloadDuration;
        if (_entityManager.TryGetComponent<GunComponent>(_entityManager.GetEntity(entity), out var gun))
        {
            next = gun.NextFire;
            duration = gun.FireRateModified > 0 ? 1f / gun.FireRateModified : duration;
        }
        var remaining = Math.Max(0d, (next - _reloadTiming.CurTime).TotalSeconds);
        var progress = duration > 0 ? Math.Clamp(1d - remaining / duration, 0d, 1d) : 1d;
        return new TguiData().String("id", entity.ToString()).String("name", button.Text)
            .Bool("selected", button.Pressed).Bool("disabled", button.Disabled)
            .Number("reload", (float)progress).Number("reloadSeconds", (float)remaining);
    }

    public TguiData TguiState() => new TguiData().String("status", ServerStatus.Text)
        .Bool("hasWeapons", _currentState is { Connected: true, FireControllables.Length: > 0 })
        .Bool("available", _currentState != null).Array("entries", WeaponsList.Select(pair => WeaponState(pair.Key, pair.Value)));

    public void TguiAction(TguiActionData args)
    {
        switch (args.String("id"))
        {
            case "refresh": OnServerRefresh?.Invoke(); return;
            case "all": SelectAllWeapons(null!); return;
            case "none": UnselectAllWeapons(null!); return;
            case "ballistic": SelectBallisticWeapons(null!); return;
            case "energy": SelectEnergyWeapons(null!); return;
            case "missile": SelectMissileWeapons(null!); return;
        }
        foreach (var (entity, button) in WeaponsList)
        {
            if (entity.ToString() != args.String("id") || button.Disabled) continue;
            button.Pressed ^= true;
            OnWeaponSelectionChanged?.Invoke();
            UpdateAllWeaponButtonTexts();
            break;
        }
    }
}
