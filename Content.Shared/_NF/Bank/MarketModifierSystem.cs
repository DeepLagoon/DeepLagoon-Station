using Content.Shared.Examine;
using Content.Shared._NF.Bank.Components;
using Content.Shared.VendingMachines;

namespace Content.Shared._NF.Bank;

public sealed partial class MarketModifierSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<MarketModifierComponent, ExaminedEvent>(OnExamined);
    }

    // This code is licensed under AGPLv3. See AGPLv3.txt
    private void OnExamined(Entity<MarketModifierComponent> ent, ref ExaminedEvent args)
    {
        // If the machine is a vendor, don't print out rates
        if (HasComp<VendingMachineComponent>(ent))
            return;

        string locVerb = ent.Comp.Buy ? "buy" : "sell";
        if (ent.Comp.Mod >= 1.0f)
            args.PushMarkup(Loc.GetString($"market-modifier-{locVerb}-high", ("mod", Math.Round(ent.Comp.Mod, 2))));
        else
            args.PushMarkup(Loc.GetString($"market-modifier-{locVerb}-low", ("mod", Math.Round(ent.Comp.Mod, 2))));
    }
}
