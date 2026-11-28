using System.Numerics;
using Robust.Client.UserInterface.Controls;

namespace Content.Client.Cargo.UI;

/// <summary>Choose columns during measurement, so resize never needs a second frame to correct card widths.</summary>
public sealed class CargoCatalogueGrid : GridContainer
{
    public float CardWidth { get; set; } = 280;
    public CargoCatalogueGrid() { HSeparationOverride = 10; VSeparationOverride = 10; }
    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        var width = float.IsFinite(availableSize.X) ? availableSize.X : CardWidth * 3 + 20;
        var columns = Math.Clamp((int)((width + 10) / (CardWidth + 10)), 1, 3);
        if (Columns != columns) Columns = columns;
        var cardWidth = Math.Max(CardWidth, (width - (columns - 1) * 10) / columns);
        foreach (var child in Children)
            if (child.MaxWidth != cardWidth) child.MaxWidth = cardWidth;
        return base.MeasureOverride(availableSize);
    }
}
