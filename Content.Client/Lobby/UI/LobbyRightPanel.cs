using System.Numerics;
using Robust.Client.UserInterface.Controls;

namespace Content.Client.Lobby.UI;

/// <summary>Reserves the lower 85% of the lobby sidebar for chat, with round status above it.</summary>
public sealed class LobbyRightPanel : Container
{
    public const float ChatHeightFraction = 0.85f;
    public event Action<Vector2>? StatusMeasuring;

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        if (ChildCount == 2)
        {
            StatusMeasuring?.Invoke(new Vector2(availableSize.X, StatusHeight(availableSize.Y)));
            GetChild(0).Measure(new Vector2(availableSize.X, StatusHeight(availableSize.Y)));
            GetChild(1).Measure(new Vector2(availableSize.X, availableSize.Y * ChatHeightFraction));
        }
        return Vector2.Zero;
    }

    protected override Vector2 ArrangeOverride(Vector2 finalSize)
    {
        if (ChildCount == 2)
        {
            var chatHeight = finalSize.Y * ChatHeightFraction;
            GetChild(0).Arrange(UIBox2.FromDimensions(Vector2.Zero,
                new Vector2(finalSize.X, StatusHeight(finalSize.Y))));
            GetChild(1).Arrange(UIBox2.FromDimensions(new Vector2(0, finalSize.Y - chatHeight),
                new Vector2(finalSize.X, chatHeight)));
        }
        return finalSize;
    }

    private static float StatusHeight(float height) => Math.Max(0, height * (1 - ChatHeightFraction) - LobbyConsoleTheme.BlockGap);
}
