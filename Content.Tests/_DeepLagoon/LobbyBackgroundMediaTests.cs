using Content.Shared._DeepLagoon.Lobby;
using NUnit.Framework;

namespace Content.Tests._DeepLagoon;

[TestFixture]
public sealed class LobbyBackgroundMediaTests
{
    [TestCase("/Textures/LobbyScreens/still.png", true)]
    [TestCase("/Textures/LobbyScreens/loop.GIF", true)]
    [TestCase("/Web/DeepLagoon/LobbyMedia/loop.webm", true)]
    [TestCase("https://example.com/loop.webm", false)]
    [TestCase("/Web/DeepLagoon/interface.html", false)]
    [TestCase("/Web/DeepLagoon/LobbyMedia/loop.mp4", false)]
    [TestCase("/Textures/../ConfigPresets/loop.webm", false)]
    [TestCase("/Textures/%2e%2e/loop.webm", false)]
    [TestCase("/Textures/loop.webm?redirect=1", false)]
    [TestCase("/Textures/loop.webm#x", false)]
    [TestCase("/Textures/loop\n.webm", false)]
    [TestCase(null, false)]
    public void OnlySupportedLocalMediaCanBeSelected(string path, bool expected)
    {
        Assert.That(LobbyBackgroundMedia.IsSupported(path), Is.EqualTo(expected));
    }
}
