using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using Content.Shared.Humanoid.Prototypes;

namespace Content.Shared.Humanoid.Markings
{
    [Prototype]
    public sealed partial class MarkingPrototype : IPrototype
    {
        [IdDataField]
        public string ID { get; private set; } = "uwu";

        public string Name { get; private set; } = default!;

        [DataField("bodyPart", required: true)]
        public HumanoidVisualLayers BodyPart { get; private set; } = default!;

        [DataField("markingCategory", required: true)]
        public MarkingCategories MarkingCategory { get; private set; } = default!;

        [DataField]
        public bool EditorHidden { get; private set; }

        [DataField]
        public bool EditorExclusive { get; private set; }

        public bool IsBodyReplacement => EditorExclusive && MarkingCategory is
            MarkingCategories.Head or MarkingCategories.Chest or MarkingCategories.Arms or MarkingCategories.Legs && BodyPart is
            HumanoidVisualLayers.Head or HumanoidVisualLayers.Chest or
            HumanoidVisualLayers.LArm or HumanoidVisualLayers.RArm or HumanoidVisualLayers.LHand or HumanoidVisualLayers.RHand or
            HumanoidVisualLayers.LLeg or HumanoidVisualLayers.RLeg or HumanoidVisualLayers.LFoot or HumanoidVisualLayers.RFoot;

        public bool CanGlow => !IsBodyReplacement;

        [DataField]
        public bool? Digitigrade { get; private set; }

        [DataField]
        public List<string> EditorLinkedMarkings { get; private set; } = new();

        [DataField("speciesRestriction")]
        public List<string>? SpeciesRestrictions { get; private set; }

        [DataField("sexRestriction")]
        public Sex? SexRestriction { get; private set; }

        [DataField("followSkinColor")]
        public bool FollowSkinColor { get; private set; } = false;

        [DataField("forcedColoring")]
        public bool ForcedColoring { get; private set; } = false;

        [DataField("coloring")]
        public MarkingColors Coloring { get; private set; } = new();

        [DataField("sprites", required: true)]
        public List<SpriteSpecifier> Sprites { get; private set; } = default!;

        [DataField]
        public Dictionary<string, HumanoidVisualLayers> SpriteLayers { get; private set; } = new();

        [DataField]
        public Dictionary<string, HumanoidVisualLayers> SpriteLayersBefore { get; private set; } = new();

        public HumanoidVisualLayers GetLayerAnchor(SpeciesPrototype species)
        {
            return species.BodyMarkingAnchor is { } anchor && BodyPart is
                HumanoidVisualLayers.Head or HumanoidVisualLayers.Chest or HumanoidVisualLayers.Snout or
                HumanoidVisualLayers.LArm or HumanoidVisualLayers.RArm or HumanoidVisualLayers.LHand or HumanoidVisualLayers.RHand or
                HumanoidVisualLayers.LLeg or HumanoidVisualLayers.RLeg or HumanoidVisualLayers.LFoot or HumanoidVisualLayers.RFoot
                ? anchor : BodyPart;
        }

        // impstation edit - allow markings to support shaders
		[DataField("shader")]
		public string? Shader { get; private set; } = null;
        // end impstation edit
        public Marking AsMarking()
        {
            return new Marking(ID, Sprites.Count);
        }
    }
}
