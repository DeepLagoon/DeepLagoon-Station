using Robust.Shared.GameObjects;

namespace Content.Shared.Movement.Systems;

public abstract partial class SharedMoverController
{
    // Disabled by default (including client prediction). The server enables these
    // fixed-size counters only around an explicitly requested diagnostic pass.
    protected bool CaptureMoverBranches;
    protected MoverBranchCounts MoverBranches;

    protected struct MoverBranchCounts
    {
        public long Entered, Relay, RelayMissing, NoTransform, CannotMove, NoPhysics, Pulled, AirBlocked;
        public long Prepared, Queued, Serial, Contained, Asleep, NonCollidable, ZeroWish, AirWeightless;
        public long Actors, ActiveNpcs, Brains;

        public void Add(in MoverBranchCounts other)
        {
            Entered += other.Entered; Relay += other.Relay; RelayMissing += other.RelayMissing;
            NoTransform += other.NoTransform; CannotMove += other.CannotMove; NoPhysics += other.NoPhysics;
            Pulled += other.Pulled; AirBlocked += other.AirBlocked;
            Prepared += other.Prepared; Queued += other.Queued; Serial += other.Serial;
            Contained += other.Contained; Asleep += other.Asleep; NonCollidable += other.NonCollidable;
            ZeroWish += other.ZeroWish; Actors += other.Actors; ActiveNpcs += other.ActiveNpcs; Brains += other.Brains;
            AirWeightless += other.AirWeightless;
        }
    }

    protected virtual void CountPreparedMover(in MobMovementStep step, bool queued)
    {
        MoverBranches.Prepared++;
        if (queued) MoverBranches.Queued++;
        else MoverBranches.Serial++;
        if ((MetaData(step.Uid).Flags & MetaDataFlags.InContainer) != 0) MoverBranches.Contained++;
        if (!step.Physics.Awake) MoverBranches.Asleep++;
        if (!step.Physics.CanCollide) MoverBranches.NonCollidable++;
        if (step.WishDir == System.Numerics.Vector2.Zero) MoverBranches.ZeroWish++;
    }
}
