using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.CryptoFarm;
using Content.Server._DeepLagoon.Exchange;
using Content.Server._DeepLagoon.ShuttleParking;
using Content.Server._Goobstation.ItemMiner;
using Content.Server._Mono.Cargo;
using Content.Server.Database;
using Content.Server.Preferences.Managers;
using Content.Server.Stack;
using Content.Shared._Goobstation.ItemMiner;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Storage.EntitySystems;
using Robust.Shared.Asynchronous;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._DeepLagoon;

[TestFixture, NonParallelizable]
public sealed class CryptoFarmWalletTests
{
    private static T Complete<T>(ITaskManager tasks, Task<T> task)
    {
        tasks.BlockWaitOnTask(task);
        return task.GetAwaiter().GetResult();
    }

    [Test]
    public async Task IdSwipeBindsTheActingCharacterAndMiningCreditsOnlyItsWalletWithoutPhysicalOutput()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var server = pair.Server;
        var e = server.EntMan;
        var map = await pair.CreateTestMap();
        var owner = await server.AddDummySession("crypto_wallet_owner");
        var other = await server.AddDummySession("crypto_wallet_other");
        var prefs = server.ResolveDependency<IServerPreferencesManager>();
        await PoolManager.WaitUntil(server, () => owner.Status == SessionStatus.InGame && other.Status == SessionStatus.InGame &&
            prefs.TryGetCachedPreferences(owner.UserId, out _) && prefs.TryGetCachedPreferences(other.UserId, out _));
        EntityUid farm = default;
        EntityUid body = default;
        EntityUid card = default;
        ExchangeResult initial = null!;
        var slot = 0;
        var db = server.ResolveDependency<IServerDbManager>();
        var tasks = server.ResolveDependency<ITaskManager>();
        await server.WaitAssertion(() =>
        {
            Assert.That(prefs.TryGetCachedPreferences(owner.UserId, out var preferences), Is.True);
            slot = preferences!.SelectedCharacterIndex;
            // Accelerate the shared issuance clock in this isolated fixture, without changing production timers.
            initial = Complete(tasks, db.ReadExchangeAsync(owner.UserId, slot, DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds()));
            server.ResolveDependency<IConfigurationManager>().SetCVar(ExchangeCVars.Enabled, true);
            farm = e.SpawnEntity("DatafarmCrypto", map.GridCoords);
            body = e.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(1, 0)));
            server.PlayerMan.SetAttachedEntity(owner, body);
            card = e.SpawnEntity("CaptainIDCard", map.GridCoords);
            Assert.That(e.System<SharedHandsSystem>().TryPickupAnyHand(body, card), Is.True);
            var unbound = new ItemMinerCheckEvent(e.GetComponent<ItemMinerComponent>(farm).Proto);
            e.EventBus.RaiseLocalEvent(farm, ref unbound);
            Assert.That(unbound.Cancelled, Is.True);
            var click = new InteractUsingEvent(body, card, farm, map.GridCoords);
            e.EventBus.RaiseLocalEvent(farm, click);
            Assert.That(click.Handled, Is.True);
        });
        await PoolManager.WaitUntil(server, () => e.GetComponent<CryptoFarmComponent>(farm).IsBound);
        await server.WaitAssertion(() =>
        {
            var binding = e.GetComponent<CryptoFarmComponent>(farm);
            Assert.That(binding.WalletUser, Is.EqualTo(owner.UserId.UserId));
            Assert.That(binding.WalletProfileId, Is.EqualTo(initial.ProfileId));
            var otherBody = e.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0, 1)));
            server.PlayerMan.SetAttachedEntity(other, otherBody);
            var otherCard = e.SpawnEntity("CaptainIDCard", map.GridCoords);
            Assert.That(e.System<SharedHandsSystem>().TryPickupAnyHand(otherBody, otherCard), Is.True);
            e.EventBus.RaiseLocalEvent(farm, new InteractUsingEvent(otherBody, otherCard, farm, map.GridCoords));
            Assert.That(binding.WalletUser, Is.EqualTo(owner.UserId.UserId), "A different account cannot overwrite the owner.");

            // Existing physical coins stay intact; the farm itself no longer stores them.
            var legacy = e.SpawnEntity("BitcoinContribcoin", map.GridCoords);
            e.System<StackSystem>().SetCount(legacy, 15);
            Assert.That(e.HasComponent<Content.Shared.Storage.StorageComponent>(farm), Is.False);
            var miner = e.GetComponent<ItemMinerComponent>(farm);
            miner.NeedApcPower = false;
            miner.NeedsAnchored = false;
            miner.SpawnChance = 1;
            miner.MinedSound = null;
            miner.NextAt = server.ResolveDependency<IGameTiming>().CurTime;
            e.System<ItemMinerSystem>().Update(0);
            Assert.That(binding.HasPendingMining, Is.True);
            e.System<ExchangeSystem>().Update(0);
        });
        await PoolManager.WaitUntil(server, () => !e.GetComponent<CryptoFarmComponent>(farm).HasPendingMining);
        await server.WaitAssertion(() =>
        {
            var paid = Complete(tasks, db.ReadExchangeAsync(owner.UserId, slot, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            Assert.That(paid.Wallet.Values.Sum(), Is.EqualTo(1000));
            Assert.That(paid.Bank, Is.EqualTo(initial.Bank));
            Assert.That(paid.Market.Pools.Values.Sum(p => p.Cash), Is.Zero);
            Assert.That(e.System<CryptoFarmSystem>().StoredCoins(farm), Is.Zero);
            Assert.That(e.EntityQuery<DriftingPriceComponent, TransformComponent>(true)
                .Count(p => p.Item2.GridUid == map.Grid.Owner && !e.IsQueuedForDeletion(p.Item1.Owner)), Is.EqualTo(1));
            server.PlayerMan.SetAttachedEntity(owner, null);
            server.PlayerMan.SetAttachedEntity(other, null);
            e.DeleteEntity(map.MapUid);
            server.ResolveDependency<IConfigurationManager>().SetCVar(ExchangeCVars.Enabled, false);
        });
        await server.RemoveDummySession(owner);
        await server.RemoveDummySession(other);
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ParkingPreservesBindingAndRetriesAnAlreadyCommittedOfflinePayoutWithoutDuplicatingCoins()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Destructive = true });
        var server = pair.Server;
        var e = server.EntMan;
        var map = await pair.CreateTestMap();
        var owner = await server.AddDummySession("crypto_parked_wallet");
        var prefs = server.ResolveDependency<IServerPreferencesManager>();
        await PoolManager.WaitUntil(server, () => owner.Status == SessionStatus.InGame && prefs.TryGetCachedPreferences(owner.UserId, out _));
        var db = server.ResolveDependency<IServerDbManager>();
        var tasks = server.ResolveDependency<ITaskManager>();
        ExchangeResult initial = null!;
        var slot = 0;
        await server.WaitAssertion(() =>
        {
            Assert.That(prefs.TryGetCachedPreferences(owner.UserId, out var preferences), Is.True);
            slot = preferences!.SelectedCharacterIndex;
            initial = Complete(tasks, db.ReadExchangeAsync(owner.UserId, slot, DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds()));
        });
        await server.RemoveDummySession(owner);
        EntityUid restoredFarm = default;
        await server.WaitAssertion(() =>
        {
            server.ResolveDependency<IConfigurationManager>().SetCVar(ExchangeCVars.Enabled, true);
            var farm = e.SpawnEntity("DatafarmCrypto", map.GridCoords);
            var parking = e.System<ParkingSystem>();
            parking.RegisterPurchasedShip(map.Grid, owner.UserId.UserId, 1000, "", "Captain");
            var binding = e.GetComponent<CryptoFarmComponent>(farm);
            binding.WalletUser = binding.MiningUser = owner.UserId.UserId;
            binding.WalletProfileId = binding.MiningProfileId = initial.ProfileId;
            binding.WalletCharacter = "Parked character";
            binding.MiningOperation = Guid.NewGuid();
            var request = new ExchangeMiningRequest(binding.MiningOperation, binding.MiningUser, binding.MiningProfileId);
            var first = parking.LoadSnapshot(parking.FilteredSnapshot(map.Grid));
            var second = parking.LoadSnapshot(parking.FilteredSnapshot(first));
            restoredFarm = e.EntityQuery<CryptoFarmComponent, TransformComponent>(true).Single(p => p.Item2.GridUid == second).Item1.Owner;
            var restored = e.GetComponent<CryptoFarmComponent>(restoredFarm);
            Assert.That(restored.WalletUser, Is.EqualTo(binding.WalletUser));
            Assert.That(restored.WalletProfileId, Is.EqualTo(binding.WalletProfileId));
            Assert.That(restored.WalletCharacter, Is.EqualTo(binding.WalletCharacter));
            Assert.That(restored.MiningOperation, Is.EqualTo(request.Operation));
            Assert.That(restored.MiningUser, Is.EqualTo(request.User));
            Assert.That(restored.MiningProfileId, Is.EqualTo(request.ProfileId));
            // Simulate a successful DB commit whose acknowledgement never reached the original farm.
            Assert.That(Complete(tasks, db.MintExchangeToWalletsAsync([request], DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).Single().Units,
                Is.EqualTo(1000));
            e.DeleteEntity(farm);
            e.DeleteEntity(first);
            e.System<SharedTransformSystem>().SetCoordinates(second, new EntityCoordinates(map.MapUid, new Vector2(8, 0)));
            Assert.That(e.GetComponent<MetaDataComponent>(restoredFarm).EntityPaused, Is.False);
            e.System<CryptoFarmSystem>().Update(0);
            e.System<ExchangeSystem>().Update(0);
        });
        await PoolManager.WaitUntil(server, () => !e.GetComponent<CryptoFarmComponent>(restoredFarm).HasPendingMining);
        await server.WaitAssertion(() =>
        {
            var result = Complete(tasks, db.ReadExchangeAsync(owner.UserId, slot, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            Assert.That(result.Wallet.Values.Sum(), Is.EqualTo(1000));
            Assert.That(result.Market.Pools.Values.Sum(p => p.MinedUnits), Is.EqualTo(1000));
            Assert.That(result.Bank, Is.EqualTo(initial.Bank));
            Assert.That(e.System<CryptoFarmSystem>().StoredCoins(restoredFarm), Is.Zero);
            e.DeleteEntity(map.MapUid);
            server.ResolveDependency<IConfigurationManager>().SetCVar(ExchangeCVars.Enabled, false);
        });
        await pair.CleanReturnAsync();
    }
}
