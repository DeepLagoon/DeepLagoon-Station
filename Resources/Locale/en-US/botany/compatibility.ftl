entity-effect-guidebook-plant-attribute =
    { $chance ->
        [1] Adjusts
        *[other] adjust
    } {$attribute} by {$positive ->
    [false] [color=red]{$amount}[/color]
    *[true] [color=green]{$amount}[/color]
    }

entity-effect-guidebook-plant-cryoxadone =
    { $chance ->
        [1] Ages back
        *[other] age back
    } the plant, depending on the plant's age and time to grow

entity-effect-guidebook-plant-phalanximine =
    { $chance ->
        [1] Restores
        *[other] restore
    } viability to a plant rendered nonviable by a mutation

entity-effect-guidebook-plant-diethylamine =
    { $chance ->
        [1] Increases
        *[other] increase
    } the plant's lifespan and/or base health with 10% chance for each

entity-effect-guidebook-plant-robust-harvest =
    { $chance ->
        [1] Increases
        *[other] increase
    } the plant's potency by {$increase} up to a maximum of {$limit}. Causes the plant to lose its seeds once the potency reaches {$seedlesstreshold}. Trying to add potency over {$limit} may cause decrease in yield at a 10% chance

entity-effect-guidebook-plant-seeds-add =
    { $chance ->
        [1] Restores the
        *[other] restore the
    } seeds of the plant

entity-effect-guidebook-plant-seeds-remove =
    { $chance ->
        [1] Removes the
        *[other] remove the
    } seeds of the plant

entity-effect-guidebook-plant-change-trait =
    { $chance ->
        [1] { $change ->
            [Add] adds {$trait} to
            [Remove] removes {$trait} from
            [Toggle] toggles {$trait} on
            *[other] changes {$trait} on
        }
        *[other] { $change ->
            [Add] add {$trait} to
            [Remove] remove {$trait} from
            [Toggle] toggle {$trait} on
            *[other] change {$trait} on
        }
    } the plant

entity-effect-guidebook-plant-mutate-exude-gasses =
    { $chance ->
        [1] Mutates
        *[other] mutate
    } the plant to exude gases between {$minValue} and {$maxValue} moles

entity-effect-guidebook-plant-mutate-consume-gasses =
    { $chance ->
        [1] Mutates
        *[other] mutate
    } the plant to consume gases between {$minValue} and {$maxValue} moles

entity-effect-guidebook-plant-mutate-chemicals =
    { $chance ->
        [1] Mutates
        *[other] mutate
    } a plant to produce {$name}

plant-attribute-water-consumption = water consumption

plant-attribute-weed-tolerance = weed tolerance

plant-attribute-toxins-tolerance = toxins tolerance

plant-attribute-nutrient-consumption = nutrient consumption

plant-attribute-pest-tolerance = pest tolerance

plant-attribute-endurance = endurance

plant-attribute-yield = yield

plant-attribute-lifespan = lifespan

plant-attribute-maturation = maturation

plant-attribute-production = production

plant-attribute-low-heat-tolerance = low heat tolerance

plant-attribute-high-heat-tolerance = high heat tolerance

plant-attribute-low-pressure-tolerance = low pressure tolerance

plant-attribute-high-pressure-tolerance = high pressure tolerance

units-u = u

reagent-name-heparin = heparin
reagent-desc-heparin = An anticoagulant that prevents natural blood clotting. Large doses can cause bleeding.
reagent-name-mutagenic-fertilizer = mutagenic fertilizer
reagent-desc-mutagenic-fertilizer = A fertilizer that encourages useful changes in plants.
reagent-effect-status-effect-Anticoagulant = anticoagulation

reagent-name-heparin = heparin
reagent-desc-heparin = An anticoagulant that prevents natural blood clotting. Large doses can cause bleeding.
reagent-name-mutagenic-fertilizer = mutagenic fertilizer
reagent-desc-mutagenic-fertilizer = A fertilizer that encourages useful changes in plants.
reagent-effect-status-effect-Anticoagulant = anticoagulation

seeds-bloonion-name = bloonion
seeds-lemoon-name = lemoon
