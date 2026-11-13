using System.Numerics;
using Content.Shared.Rotation;
using Robust.Client.GameObjects;
using Robust.Client.Input;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests.Movement;

public sealed class CharacterHotkeysTest : MovementTest
{
    protected override bool AddWalls => false;

    [Test]
    public async Task PrecisionMovementStopsAtTileEdgeUntilNIsReleased()
    {
        var start = Vector2.Zero;
        await Server.WaitAssertion(() => start = SEntMan.GetComponent<TransformComponent>(SPlayer).LocalPosition);
        await Press(Keyboard.Key.N);
        await Press(Keyboard.Key.Right);
        await RunSeconds(2f);

        await Server.WaitAssertion(() => AssertPosition(SEntMan.GetComponent<TransformComponent>(SPlayer).LocalPosition));
        await Client.WaitAssertion(() => AssertPosition(CEntMan.GetComponent<TransformComponent>(CPlayer).LocalPosition));

        await Release(Keyboard.Key.N);
        await RunTicks(10);
        await Release(Keyboard.Key.Right);
        await Server.WaitAssertion(() => Assert.That(SEntMan.GetComponent<TransformComponent>(SPlayer).LocalPosition.X,
            Is.GreaterThan(MathF.Floor(start.X) + 1f)));

        void AssertPosition(Vector2 position)
        {
            Assert.That(position.X, Is.InRange(start.X + 0.25f, MathF.Floor(start.X) + 0.999f));
            Assert.That(position.Y, Is.EqualTo(start.Y).Within(0.01f));
        }
    }

    [TestCase(Keyboard.Key.A, true, 1)]
    [TestCase(Keyboard.Key.Left, false, 1)]
    [TestCase(Keyboard.Key.D, false, -1)]
    [TestCase(Keyboard.Key.Right, true, -1)]
    public async Task TiltChangesSpriteWithoutTurningOrMovingCharacter(Keyboard.Key direction, bool altFirst, int tilt)
    {
        var start = Vector2.Zero;
        var facing = Angle.Zero;
        await Server.WaitAssertion(() =>
        {
            var transform = SEntMan.GetComponent<TransformComponent>(SPlayer);
            start = transform.LocalPosition;
            facing = transform.LocalRotation;
        });

        await Press(altFirst ? Keyboard.Key.Alt : Keyboard.Key.Shift, alt: altFirst, shift: !altFirst);
        await Press(altFirst ? Keyboard.Key.Shift : Keyboard.Key.Alt, alt: true, shift: true);
        await Press(direction, alt: true, shift: true);
        await RunTicks(10);

        await Server.WaitAssertion(() =>
        {
            var transform = SEntMan.GetComponent<TransformComponent>(SPlayer);
            Assert.That(transform.LocalPosition, Is.EqualTo(start));
            Assert.That(transform.LocalRotation, Is.EqualTo(facing));
            Assert.That(Server.System<SharedAppearanceSystem>().TryGetData<int>(SPlayer, RotationVisuals.Tilt, out var value));
            Assert.That(value, Is.EqualTo(tilt));
        });
        await Client.WaitAssertion(() => Assert.That(CEntMan.GetComponent<SpriteComponent>(CPlayer).Rotation.Degrees,
            Is.EqualTo(15 * tilt).Within(0.01)));

        await Release(Keyboard.Key.Alt, shift: true);
        await Release(direction, shift: true);
        await Release(Keyboard.Key.Shift);
        await RunTicks(10);
        await Client.WaitAssertion(() => Assert.That(CEntMan.GetComponent<SpriteComponent>(CPlayer).Rotation.Degrees,
            Is.EqualTo(0).Within(0.01)));
    }

    private Task Press(Keyboard.Key key, bool alt = false, bool shift = false) => Client.WaitPost(() =>
        Client.ResolveDependency<IInputManager>().KeyDown(new KeyEventArgs(key, false, alt, false, shift, false, 0)));

    private Task Release(Keyboard.Key key, bool alt = false, bool shift = false) => Client.WaitPost(() =>
        Client.ResolveDependency<IInputManager>().KeyUp(new KeyEventArgs(key, false, alt, false, shift, false, 0)));
}
