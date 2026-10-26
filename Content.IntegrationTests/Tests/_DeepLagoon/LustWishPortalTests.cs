using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server._DeepLagoon.LustWish;
using Content.Server.Chat.Systems;
using Content.Shared._DeepLagoon.LustWish;
using Content.Shared.Buckle.Components;
using Content.Shared.DragDrop;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Robust.Client.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Player;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class LustWishPortalTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task WallMountDragDropProjectionHearingAndSupportLifetime(bool destroySupport)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true, Connected = true });
        var map = await pair.CreateTestMap();
        EntityUid body = default, first = default, second = default, wallA = default, wallB = default, proxy = default;
        NetEntity bodyNet = default, proxyNet = default, soundNet = default;
        await pair.Server.WaitAssertion(() =>
        {
            var e = pair.Server.EntMan;
            var t = e.System<SharedTransformSystem>();
            var a = new EntityCoordinates(map.Grid, new Vector2(.5f,.5f));
            var b = new EntityCoordinates(map.Grid, new Vector2(30.5f,.5f));
            body = e.SpawnEntity("MobHuman", a);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), body);
            var kit = e.SpawnEntity("DLLustWishPortalKit", a);
            Assert.That(e.System<SharedHandsSystem>().TryPickupAnyHand(body, kit), Is.True);
            var c = e.GetComponent<LustWishPortalKitComponent>(kit);
            e.EventBus.RaiseLocalEvent(kit, new UseInHandEvent(body));
            e.EventBus.RaiseLocalEvent(kit, new AfterInteractEvent(body,kit,map.Grid,a,true));
            Assert.That(c.FirstPortal, Is.Null, "Neither using in hand nor clicking a floor places a portal");
            var mapSystem = e.System<SharedMapSystem>();
            for (var x=0; x<=31; x++)
                for (var y=0; y<=2; y++)
                    mapSystem.SetTile(map.Grid.Owner,map.Grid.Comp,new EntityCoordinates(map.Grid,new Vector2(x,y)),map.Tile.Tile);
            wallA = e.SpawnEntity("WallSolid",new EntityCoordinates(map.Grid,new Vector2(.5f,1.5f)));
            wallB = e.SpawnEntity("WallSolid",new EntityCoordinates(map.Grid,new Vector2(30.5f,1.5f)));
            e.EventBus.RaiseLocalEvent(kit,new AfterInteractEvent(body,kit,wallA,a,true));
            first = c.FirstPortal!.Value;
            t.SetCoordinates(body,b);
            e.EventBus.RaiseLocalEvent(kit,new AfterInteractEvent(body,kit,wallB,b,true));
            second = e.GetComponent<LustWishPortalComponent>(first).Peer!.Value;
            t.SetCoordinates(body,a);
            var drop = new DragDropTargetEvent(body,body);
            e.EventBus.RaiseLocalEvent(first,ref drop);
            Assert.That(drop.Handled, Is.True, "Native chair drag/drop must buckle the character");
            Assert.That(e.GetComponent<BuckleComponent>(body).BuckledTo, Is.EqualTo(first));
            var occupant=e.GetComponent<LustWishPortalOccupantComponent>(body);
            proxy=occupant.Projection;
            Assert.That(e.GetComponent<TransformComponent>(first).ParentUid, Is.EqualTo(wallA));
            Assert.That(e.GetComponent<TransformComponent>(proxy).ParentUid, Is.EqualTo(second));
            var observer=e.SpawnEntity("MobHuman",b);
            var portals=e.System<LustWishPortalSystem>();
            Assert.That(portals.ResolveTarget(observer,proxy), Is.EqualTo(body));
            Assert.That(portals.CanRelay(observer,body), Is.True);
            Assert.That(portals.AcousticDistance(body,observer), Is.LessThan(2));
            var recipients=new Dictionary<ICommonSession,ChatSystem.ICChatRecipientData>();
            e.EventBus.RaiseEvent(EventSource.Local,new ExpandICChatRecipientsEvent(observer,7,recipients));
            Assert.That(recipients.ContainsKey(pair.Server.PlayerMan.Sessions.Single()), Is.True, "Speech reaches the participant from the far end");
            // Moving and rotating the grid preserves the mounted endpoints and the occupant.
            t.SetLocalPosition(map.Grid,new Vector2(12,7));
            t.SetLocalRotation(map.Grid,Angle.FromDegrees(90));
            Assert.That(e.GetComponent<TransformComponent>(first).ParentUid, Is.EqualTo(wallA));
            Assert.That(e.GetComponent<LustWishPortalComponent>(first).Participant, Is.EqualTo(body));
            var sound=e.System<Robust.Server.Audio.AudioSystem>().PlayPvs(new ResolvedPathSpecifier("/Audio/Effects/beep1.ogg"),second,AudioParams.Default.WithLoop(true).WithMaxDistance(5));
            Assert.That(sound, Is.Not.Null);
            soundNet=e.GetNetEntity(sound!.Value.Entity);
            e.DeleteEntity(observer);
            bodyNet=e.GetNetEntity(body); proxyNet=e.GetNetEntity(proxy);
        });
        await pair.RunTicksSync(30);
        await pair.Server.WaitAssertion(() =>
        {
            var e=pair.Server.EntMan;
            Assert.That(e.HasComponent<LustWishPortalOccupantComponent>(body), Is.True, "The participant remains buckled while the supporting grid moves");
            Assert.That(e.EntityExists(proxy), Is.True, "The projected half remains alive");
            Assert.That(pair.Server.PlayerMan.Sessions.Single().ViewSubscriptions.Contains(second), Is.True, "The far end is subscribed for PVS");
        });
        await pair.Client.WaitAssertion(() =>
        {
            var e=pair.Client.EntMan;
            var bodyClient=e.GetEntity(bodyNet); var proxyClient=e.GetEntity(proxyNet);
            Assert.That(e.HasComponent<LustWishProjectionComponent>(proxyClient), Is.True, "The distant half must reach the client through the portal view");
            e.System<Content.Client._DeepLagoon.LustWish.LustWishPortalVisualSystem>().FrameUpdate(0);
            var sprites=e.System<SpriteSystem>();
            Assert.That(sprites.TryGetPostShader((bodyClient,e.GetComponent<SpriteComponent>(bodyClient)),"lustwish-portal-half",out _), Is.True);
            Assert.That(sprites.TryGetPostShader((proxyClient,e.GetComponent<SpriteComponent>(proxyClient)),"lustwish-portal-half",out _), Is.True);
            Assert.That(e.GetComponent<SpriteComponent>(proxyClient).Visible, Is.True);
            var audioUid=e.GetEntity(soundNet);
            Assert.That(e.HasComponent<AudioComponent>(audioUid), Is.True, "Remote positional sound reaches the listener through portal PVS");
            e.System<Content.Client._DeepLagoon.LustWish.LustWishPortalAudioSystem>().FrameUpdate(0);
            var listener=e.System<Robust.Client.Audio.AudioSystem>().GetListenerCoordinates();
            Assert.That(Vector2.Distance(e.GetComponent<AudioComponent>(audioUid).Position,listener.Position), Is.LessThan(2), "Remote sound is positioned at the nearby mouth rather than thirty tiles away");
        });
        await pair.Server.WaitAssertion(() =>
        {
            var e=pair.Server.EntMan;
            if (destroySupport) e.DeleteEntity(wallA);
            else e.System<SharedTransformSystem>().Unanchor(wallB);
            Assert.That(e.HasComponent<LustWishPortalOccupantComponent>(body), Is.False, "Losing either support immediately releases the participant");
            Assert.That(e.GetComponent<BuckleComponent>(body).Buckled, Is.False);
            Assert.That(e.EntityExists(body), Is.True);
        });
        await pair.RunTicksSync(5);
        await pair.Server.WaitAssertion(() =>
        {
            var e=pair.Server.EntMan;
            Assert.That(e.EntityExists(destroySupport ? first : second), Is.False);
            Assert.That(e.EntityExists(proxy), Is.False);
            if (destroySupport) e.DeleteEntity(wallB); else e.DeleteEntity(wallA);
            Assert.That(e.EntityExists(body), Is.True, "Deleting a supporting wall must never delete its former participant");
        });
        await pair.RunTicksSync(5);
        await pair.Client.WaitAssertion(() =>
        {
            var e=pair.Client.EntMan; var uid=e.GetEntity(bodyNet);
            Assert.That(e.System<SpriteSystem>().TryGetPostShader((uid,e.GetComponent<SpriteComponent>(uid)),"lustwish-portal-half",out _), Is.False);
        });
        await pair.Server.WaitPost(() => pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(),null));
        await pair.Server.WaitPost(() => pair.Server.EntMan.DeleteEntity(map.MapUid));
        await pair.ReallyBeIdle();
        await pair.CleanReturnAsync();
    }
}
