using Content.Shared._DeepLagoon.Apartments;
using Robust.Shared.Network;

namespace Content.Server._DeepLagoon.Apartments;

public sealed partial class ApartmentSystem
{
    public Instance PrepareElevatorApartment(NetUserId owner)
    {
        if (!Enabled) throw new InvalidOperationException("Apartments disabled.");
        EnsureMaps();
        RefreshSelectedInstance(owner);
        if (!_instances.TryGetValue(owner, out var instance))
        {
            if (_instances.Count + _retiring.Count >= Math.Max(1, _config.GetCVar(ApartmentCVars.LoadedLimit)))
                throw new InvalidOperationException("Apartment limit reached.");
            instance = Create(owner, _templates.GetValueOrDefault(owner, _config.GetCVar(ApartmentCVars.DefaultTemplate)));
            _instances.Add(owner, instance);
            _guestAccess[owner] = instance.Guests;
            _templates[owner] = instance.Template.ID;
        }
        Wake(instance);
        return instance;
    }
}
