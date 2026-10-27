using Content.Shared._DeepLagoon.LustWish;
using Robust.Shared.Serialization.Manager;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture]
public sealed class LustWishSerializationTests
{
    [TestCase("00000000-0000-0000-0000-000000000000")]
    [TestCase("68c3d23a-5621-4634-b7f3-39e6fd2aca8e")]
    public async Task KeyAndLockCodesSurviveSerialization(string value)
    {
        await using var pair = await PoolManager.GetServerClient();
        await pair.Server.WaitAssertion(() =>
        {
            var serialization = pair.Server.ResolveDependency<ISerializationManager>();
            var code = Guid.Parse(value);
            var key = new LustWishKeyComponent { Code = code };
            var accessory = new LustWishAccessoryComponent { KeyCode = code, Locked = true };
            var keyCopy = serialization.Read<LustWishKeyComponent>(serialization.WriteValue(key, alwaysWrite: true));
            var accessoryCopy = serialization.Read<LustWishAccessoryComponent>(serialization.WriteValue(accessory, alwaysWrite: true));
            Assert.That(keyCopy.Code, Is.EqualTo(code));
            Assert.That(accessoryCopy.KeyCode, Is.EqualTo(code));
            Assert.That(accessoryCopy.Locked, Is.True);
            var canvas = new Content.Shared._DeepLagoon.Art.ArtCanvasComponent { WorkId = code, Author = code };
            var canvasCopy = serialization.Read<Content.Shared._DeepLagoon.Art.ArtCanvasComponent>(
                serialization.WriteValue(canvas, alwaysWrite: true));
            Assert.That(canvasCopy.WorkId, Is.EqualTo(code));
            Assert.That(canvasCopy.Author, Is.EqualTo(code));
        });
        await pair.CleanReturnAsync();
    }
}
