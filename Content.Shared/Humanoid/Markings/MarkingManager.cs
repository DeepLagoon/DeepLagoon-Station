using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Shared.Humanoid.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Shared.Humanoid.Markings
{
    public sealed partial class MarkingManager
    {
        [Dependency] private IPrototypeManager _prototypeManager = default!;

        private readonly List<MarkingPrototype> _index = new();
        public FrozenDictionary<MarkingCategories, FrozenDictionary<string, MarkingPrototype>> CategorizedMarkings = default!;
        public FrozenDictionary<string, MarkingPrototype> Markings = default!;
        private readonly HashSet<string> _commonMarkings = new();
        private readonly Dictionary<string, HashSet<string>> _speciesRules = new();

        public bool IsCommon(MarkingPrototype marking) => _commonMarkings.Contains(marking.ID);

        public static HumanoidVisualLayers? OppositePart(HumanoidVisualLayers part) => part switch
        {
            HumanoidVisualLayers.LArm => HumanoidVisualLayers.RArm,
            HumanoidVisualLayers.RArm => HumanoidVisualLayers.LArm,
            HumanoidVisualLayers.LHand => HumanoidVisualLayers.RHand,
            HumanoidVisualLayers.RHand => HumanoidVisualLayers.LHand,
            HumanoidVisualLayers.LLeg => HumanoidVisualLayers.RLeg,
            HumanoidVisualLayers.RLeg => HumanoidVisualLayers.LLeg,
            HumanoidVisualLayers.LFoot => HumanoidVisualLayers.RFoot,
            HumanoidVisualLayers.RFoot => HumanoidVisualLayers.LFoot,
            _ => null,
        };

        public static bool IsRightPart(HumanoidVisualLayers part) => part is HumanoidVisualLayers.RArm
            or HumanoidVisualLayers.RHand or HumanoidVisualLayers.RLeg or HumanoidVisualLayers.RFoot;

        /// <summary>Pairs existing directional prototypes without changing saved marking IDs.</summary>
        public MarkingPrototype? EditorMirror(MarkingPrototype marking)
        {
            if (OppositePart(marking.BodyPart) is not { } opposite) return null;
            var right = IsRightPart(marking.BodyPart);
            var from = right ? "R" : "L";
            var to = right ? "L" : "R";
            var candidates = new List<string>
            {
                marking.ID.Replace(right ? "Right" : "Left", right ? "Left" : "Right", StringComparison.Ordinal),
                marking.ID.Replace(right ? "right" : "left", right ? "left" : "right", StringComparison.Ordinal),
                marking.ID.Replace(marking.BodyPart.ToString(), opposite.ToString(), StringComparison.Ordinal),
            };
            foreach (var limb in new[] { "Arm", "Hand", "Leg", "Foot", "Shoulder" })
                candidates.Add(marking.ID.Replace(from + limb, to + limb, StringComparison.Ordinal));
            if (marking.ID.EndsWith(from, StringComparison.Ordinal)) candidates.Add(marking.ID[..^1] + to);
            if (marking.ID.StartsWith(from, StringComparison.Ordinal)) candidates.Add(to + marking.ID[1..]);
            foreach (var id in candidates)
                if (Markings.TryGetValue(id, out var mirror) && mirror.BodyPart == opposite
                    && mirror.MarkingCategory == marking.MarkingCategory && mirror.EditorExclusive == marking.EditorExclusive
                    && mirror.SexRestriction == marking.SexRestriction)
                    return mirror;
            return null;
        }

        public IReadOnlyCollection<string>? GetSpeciesCategories(MarkingPrototype marking)
        {
            if (IsCommon(marking)) return null;
            return _speciesRules.TryGetValue(marking.ID, out var species) ? species : marking.SpeciesRestrictions;
        }

        public bool IsSpeciesAllowed(string species, MarkingPrototype marking, bool ignoreSpecies = false)
        {
            if (ignoreSpecies || species == "Synth" || IsCommon(marking)) return true;
            if (_speciesRules.TryGetValue(marking.ID, out var allowed)) return allowed.Contains(species);
            var speciesProto = _prototypeManager.Index<SpeciesPrototype>(species);
            var onlyWhitelisted = _prototypeManager.Index<MarkingPointsPrototype>(speciesProto.MarkingPoints).OnlyWhitelisted;
            return marking.SpeciesRestrictions != null ? marking.SpeciesRestrictions.Contains(species) : !onlyWhitelisted;
        }

        public bool HasDigitigradeLegs(MarkingSet set)
        {
            return set.Markings.Values.SelectMany(list => list)
                .Where(marking => marking.Visible && TryGetMarking(marking, out var prototype) && prototype.Digitigrade != null)
                .OrderByDescending(marking => marking.DrawOrder)
                .GroupBy(marking => Markings[marking.MarkingId].BodyPart)
                .Any(leg => Markings[leg.First().MarkingId].Digitigrade == true);
        }

        public void Initialize()
        {
            _prototypeManager.PrototypesReloaded += OnPrototypeReload;
            CachePrototypes();
        }

        private void CachePrototypes()
        {
            _commonMarkings.Clear();
            _speciesRules.Clear();
            foreach (var rules in _prototypeManager.EnumeratePrototypes<MarkingSpeciesRulesPrototype>())
            {
                foreach (var id in rules.Common) _commonMarkings.Add(id.Id);
                foreach (var id in rules.GeneticOnly) _speciesRules[id.Id] = new();
                foreach (var (species, ids) in rules.Species)
                foreach (var id in ids)
                {
                    if (!_speciesRules.TryGetValue(id.Id, out var allowed)) _speciesRules[id.Id] = allowed = new();
                    allowed.Add(species.Id);
                }
            }
            _index.Clear();
            var markingDict = new Dictionary<MarkingCategories, Dictionary<string, MarkingPrototype>>();

            foreach (var category in Enum.GetValues<MarkingCategories>())
            {
                markingDict.Add(category, new());
            }

            foreach (var prototype in _prototypeManager.EnumeratePrototypes<MarkingPrototype>())
            {
                _index.Add(prototype);
                markingDict[prototype.MarkingCategory].Add(prototype.ID, prototype);
            }

            Markings = _prototypeManager.EnumeratePrototypes<MarkingPrototype>().ToFrozenDictionary(x => x.ID);
            CategorizedMarkings = markingDict.ToFrozenDictionary(
                x => x.Key,
                x => x.Value.ToFrozenDictionary());
        }

        public FrozenDictionary<string, MarkingPrototype> MarkingsByCategory(MarkingCategories category)
        {
            // all marking categories are guaranteed to have a dict entry
            return CategorizedMarkings[category];
        }

        /// <summary>
        ///     Markings by category and species.
        /// </summary>
        /// <param name="category"></param>
        /// <param name="species"></param>
        /// <remarks>
        ///     This is done per category, as enumerating over every single marking by species isn't useful.
        ///     Please make a pull request if you find a use case for that behavior.
        /// </remarks>
        /// <returns></returns>
        public IReadOnlyDictionary<string, MarkingPrototype> MarkingsByCategoryAndSpecies(MarkingCategories category,
            string species, bool ignoreSpecies = false)
        {
            var res = new Dictionary<string, MarkingPrototype>();

            foreach (var (key, marking) in MarkingsByCategory(category))
            {
                if (!IsSpeciesAllowed(species, marking, ignoreSpecies))
                {
                    continue;
                }
                res.Add(key, marking);
            }

            return res;
        }

        /// <summary>
        ///     Markings by category and sex.
        /// </summary>
        /// <param name="category"></param>
        /// <param name="sex"></param>
        /// <remarks>
        ///     This is done per category, as enumerating over every single marking by species isn't useful.
        ///     Please make a pull request if you find a use case for that behavior.
        /// </remarks>
        /// <returns></returns>
        public IReadOnlyDictionary<string, MarkingPrototype> MarkingsByCategoryAndSex(MarkingCategories category,
            Sex sex)
        {
            var res = new Dictionary<string, MarkingPrototype>();

            foreach (var (key, marking) in MarkingsByCategory(category))
            {
                if (marking.SexRestriction != null && marking.SexRestriction != sex)
                {
                    continue;
                }

                res.Add(key, marking);
            }

            return res;
        }

        /// <summary>
        ///     Markings by category, species and sex.
        /// </summary>
        /// <param name="category"></param>
        /// <param name="species"></param>
        /// <param name="sex"></param>
        /// <remarks>
        ///     This is done per category, as enumerating over every single marking by species isn't useful.
        ///     Please make a pull request if you find a use case for that behavior.
        /// </remarks>
        /// <returns></returns>
        public IReadOnlyDictionary<string, MarkingPrototype> MarkingsByCategoryAndSpeciesAndSex(MarkingCategories category,
            string species, Sex sex, bool ignoreSpecies = false)
        {
            var res = new Dictionary<string, MarkingPrototype>();

            foreach (var (key, marking) in MarkingsByCategory(category))
            {
                if (!IsSpeciesAllowed(species, marking, ignoreSpecies))
                {
                    continue;
                }

                if (marking.SexRestriction != null && marking.SexRestriction != sex)
                {
                    continue;
                }

                res.Add(key, marking);
            }

            return res;
        }

        public bool TryGetMarking(Marking marking, [NotNullWhen(true)] out MarkingPrototype? markingResult)
        {
            return Markings.TryGetValue(marking.MarkingId, out markingResult);
        }

        /// <summary>
        ///     Check if a marking is valid according to the category, species, and current data this marking has.
        /// </summary>
        /// <param name="marking"></param>
        /// <param name="category"></param>
        /// <param name="species"></param>
        /// <param name="sex"></param>
        /// <returns></returns>
        public bool IsValidMarking(Marking marking, MarkingCategories category, string species, Sex sex, bool ignoreSpecies = false)
        {
            if (!TryGetMarking(marking, out var proto))
            {
                return false;
            }

            if (proto.MarkingCategory != category ||
                !IsSpeciesAllowed(species, proto, ignoreSpecies) ||
                proto.SexRestriction != null && proto.SexRestriction != sex)
            {
                return false;
            }

            if (marking.MarkingColors.Count != proto.Sprites.Count)
            {
                return false;
            }

            return true;
        }

        private void OnPrototypeReload(PrototypesReloadedEventArgs args)
        {
            if (args.WasModified<MarkingPrototype>() || args.WasModified<MarkingSpeciesRulesPrototype>())
                CachePrototypes();
        }

        public bool CanBeApplied(string species, Sex sex, Marking marking, IPrototypeManager? prototypeManager = null, bool ignoreSpecies = false)
        {
            IoCManager.Resolve(ref prototypeManager);


            if (!TryGetMarking(marking, out var prototype))
            {
                return false;
            }

            if (!IsSpeciesAllowed(species, prototype, ignoreSpecies))
            {
                return false;
            }

            if (prototype.SexRestriction != null && prototype.SexRestriction != sex)
            {
                return false;
            }

            return true;
        }

        public bool CanBeApplied(string species, Sex sex, MarkingPrototype prototype, IPrototypeManager? prototypeManager = null, bool ignoreSpecies = false)
        {
            IoCManager.Resolve(ref prototypeManager);


            if (!IsSpeciesAllowed(species, prototype, ignoreSpecies))
            {
                return false;
            }

            if (prototype.SexRestriction != null && prototype.SexRestriction != sex)
            {
                return false;
            }

            return true;
        }

        public bool MustMatchSkin(string species, HumanoidVisualLayers layer, out float alpha, IPrototypeManager? prototypeManager = null)
        {
            IoCManager.Resolve(ref prototypeManager);
            var speciesProto = prototypeManager.Index<SpeciesPrototype>(species);
            if (
                !prototypeManager.TryIndex(speciesProto.SpriteSet, out HumanoidSpeciesBaseSpritesPrototype? baseSprites) ||
                !baseSprites.Sprites.TryGetValue(layer, out var spriteName) ||
                !prototypeManager.TryIndex(spriteName, out HumanoidSpeciesSpriteLayer? sprite) ||
                sprite == null ||
                !sprite.MarkingsMatchSkin
            )
            {
                alpha = 1f;
                return false;
            }

            alpha = sprite.LayerAlpha;
            return true;
        }
    }
}
