# Vending weapon review

Lethal lasers, including mixed-mode energy guns and laser bundles, are removed from vending and faction stores.
Nonlethal and practice lasers remain. Emag inventory is preserved. General retail excludes weapons fabricated by
research-connected techfabs (including their starting packs); faction machines keep their non-laser equipment.

`reviewed_vending_loot.yml` defines the removed ordinary stock: T1/T2 enters native wreck and orbital-debris
salvage tables, and T2/T3 enters expedition weapon markers. T4/T5 uses the existing rare expedition budget;
the Cybersun juggernaut bundle is explicitly T5. Classification in `expedition_loot.yml` enforces the same caps
for nested cases and delayed contents. Practice/toy guns and the postal launcher receive explicit low-tier
classifications instead of the conservative T5 fallback for unknown guns.

Ordinary self-recharging lasers now retain their batteries but require native weapon chargers. The approved
exceptions are AntiqueLaser, AdvancedLaser (including Expedition), PulsePistol (including HoS/Captain),
PulseCarbine, PulseRifle, the three Ullman pulse weapons, and HeavyPulseCannon. EG-4 and its variants retain
self-recharge at 37.5 units/second, half their previous rate. NPC and cyborg weapon definitions are unchanged.

Each distinct window actually crossed transmits 75% of laser damage. Hitscan damage, reflected beams and chained/diffracted continuations use
`LaserWindowSystem`; moving laser/disabler bolts use `LaserProjectileWindowSystem`, retaining attenuation
across simulation ticks and avoiding duplicate counts from multiple fixtures or collision callbacks.
Ballistic projectiles are unaffected. Crossing two windows transmits 56.25% of the original damage.

`VendingWeaponReviewTests` covers the runtime inventories, tiered loot, native window fixtures and damage,
non-regeneration, and charging through a native weapon charger.
