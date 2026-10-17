using Content.Shared.Cargo.Components;
using Content.Shared.Stacks;
using Content.Shared.Chemistry.Components.SolutionManager;
using Content.Shared.Chemistry.Reagent;
using Robust.Shared.Prototypes;

namespace Content.Client.VendingMachines;

/// <summary>The existing vending price estimate, shared by the native and TGUI catalogues.</summary>
public static class VendingMachinePricing
{
    public static int GetPrice(EntityPrototype? prototype, float priceModifier, IPrototypeManager prototypes, IEntityManager entities)
    {
        // Frontier: item pricing
        // ok so we dont really have access to the pricing system so we are doing a quick price check
        // based on prototype info since the items inside a vending machine dont actually exist as entities
        // until they are spawned. So this little alg does the following:
        // first, checks for a staticprice component, and if it has one, checks to make sure its not 0 since
        // stacks and other items have 0 cost.
        // If the price is 0, then we check for both a stack price and a stack component, since if it has one
        // it should have the other too, and then calculates the price based on that.
        // If the price is still 0 or non-existant (this is the case for food and containers since their value is
        // determined dynamically by their contents/inventory), it then falls back to the default mystery
        // hardcoded value of 20xMarketModifier.
        var cost = 20;
        if (prototype != null && prototype.TryGetComponent<StaticPriceComponent>(out var priceComponent, entities.ComponentFactory))
        {
            if (priceComponent.Price != 0)
            {
                var price = (float)priceComponent.Price;
                cost = (int)(price * priceModifier);
            }
            else
            {
                if (prototype.TryGetComponent<StackPriceComponent>(out var stackPrice, entities.ComponentFactory)
                    && prototype.TryGetComponent<StackComponent>(out var stack, entities.ComponentFactory))
                {
                    var price = stackPrice.Price * stack.Count;
                    cost = (int)(price * priceModifier);
                }
                else
                    cost = (int)(cost * priceModifier);
            }
        }
        else
            cost = (int)(cost * priceModifier);

        if (prototype != null && prototype.TryGetComponent<SolutionContainerManagerComponent>(out var priceSolutions, entities.ComponentFactory))
        {
            if (priceSolutions.Solutions != null)
            {
                foreach (var solution in priceSolutions.Solutions.Values)
                {
                    foreach (var (reagent, quantity) in solution.Contents)
                    {
                        if (!prototypes.TryIndex<ReagentPrototype>(reagent.Prototype,
                            out var reagentProto))
                            continue;

                        // TODO check ReagentData for price information?
                        var costReagent = quantity.Float() * reagentProto.PricePerUnit;
                        cost += (int)(costReagent * priceModifier);
                    }
                }
            }
        }
        // End Frontier: item pricing

        // Frontier: calculate vending price (this duplicates Content.Server.PricingSystem.GetVendPrice - this should be moved to Content.Shared if possible)
        if (prototype != null)
        {
            var price = 0.0;

            if (prototype.TryGetComponent<StaticPriceComponent>(out var staticComp, entities.ComponentFactory) && staticComp.VendPrice > 0.0)
            {
                price += staticComp.VendPrice;
            }
            else if (prototype.TryGetComponent<StackPriceComponent>(out var stackComp, entities.ComponentFactory) && stackComp.VendPrice > 0.0)
            {
                price += stackComp.VendPrice;
            }

            // If there is anything that explicitly sets vending price - higher OR lower, override the base.
            if (price > 0.0)
            {
                cost = (int)price;
            }
        }

        return cost;
        // End Frontier
    }

}
