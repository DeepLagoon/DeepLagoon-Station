using System;
using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface.Controls;

namespace Content.Client.UserInterface.Controls
{
    public sealed class StripeBack : Container
    {
        private const float PadSize = 4;
        private const float EdgeSize = 2;
        private static readonly Color EdgeColor = Color.FromHex("#525252ff");

        private bool _hasTopEdge = true;
        private bool _hasBottomEdge = true;
        private bool _hasMargins = true;

        public const string StylePropertyBackground = "background";

        public bool HasTopEdge
        {
            get => _hasTopEdge;
            set
            {
                _hasTopEdge = value;
                InvalidateMeasure();
            }
        }

        public bool HasBottomEdge
        {
            get => _hasBottomEdge;
            set
            {
                _hasBottomEdge = value;
                InvalidateMeasure();
            }
        }

        public bool HasMargins
        {
            get => _hasMargins;
            set
            {
                _hasMargins = value;
                InvalidateMeasure();
            }
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            var padSize = HasMargins ? PadSize : 0;
            var padSizeTotal = 0f;

            if (HasBottomEdge)
                padSizeTotal += padSize + EdgeSize;
            if (HasTopEdge)
                padSizeTotal += padSize + EdgeSize;

            var size = Vector2.Zero;

            availableSize.Y = Math.Max(0, availableSize.Y - padSizeTotal);

            foreach (var child in Children)
            {
                child.Measure(availableSize);
                size = Vector2.Max(size, child.DesiredSize);
            }

            return size + new Vector2(0, padSizeTotal);
        }

        protected override Vector2 ArrangeOverride(Vector2 finalSize)
        {
            var padSize = HasMargins ? PadSize : 0;
            // A parent can allocate less height than our minimum during layout.
            var top = HasTopEdge ? Math.Min(padSize + EdgeSize, finalSize.Y) : 0;
            var bottom = HasBottomEdge ? finalSize.Y - padSize - EdgeSize : finalSize.Y;
            var box = new UIBox2(0, top, finalSize.X, Math.Max(top, bottom));

            foreach (var child in Children)
            {
                child.Arrange(box);
            }

            return finalSize;
        }


        protected override void Draw(DrawingHandleScreen handle)
        {
            var padSize = HasMargins ? PadSize : 0;
            var edgeCount = (HasTopEdge ? 1 : 0) + (HasBottomEdge ? 1 : 0);
            // Before layout, or while collapsing, the edges may not fit at all.
            if (PixelWidth <= 0 || PixelHeight <= 0 || PixelHeight < edgeCount * (padSize + EdgeSize) * UIScale)
                return;

            UIBox2 centerBox = PixelSizeBox;

            if (HasTopEdge)
            {
                centerBox += (0, (padSize + EdgeSize) * UIScale, 0, 0);
                handle.DrawRect(new UIBox2(0, padSize * UIScale, PixelWidth, centerBox.Top), EdgeColor);
            }

            if (HasBottomEdge)
            {
                centerBox += (0, 0, 0, -((padSize + EdgeSize) * UIScale));
                handle.DrawRect(new UIBox2(0, centerBox.Bottom, PixelWidth, PixelHeight - padSize * UIScale),
                    EdgeColor);
            }

            GetActualStyleBox()?.Draw(handle, centerBox, UIScale);
        }

        private StyleBox? GetActualStyleBox()
        {
            return TryGetStyleProperty(StylePropertyBackground, out StyleBox? box) ? box : null;
        }
    }
}
