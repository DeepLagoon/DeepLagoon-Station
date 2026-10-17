using System.Numerics;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.UserInterface.Controls;

namespace Content.Client._DeepLagoon.Research;

public sealed class ResearchConsoleTguiWindow : FancyWindow
{
    public readonly TguiPanel Panel = new() { ConsoleKeyboardFeedback = true };

    public ResearchConsoleTguiWindow()
    {
        SetSize = new Vector2(1100, 720);
        MinSize = new Vector2(760, 480);
        ContentsContainer.AddChild(Panel);
    }
}
