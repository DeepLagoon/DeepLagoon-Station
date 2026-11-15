// New Frontiers - This file is licensed under AGPLv3
// Copyright (c) 2024 New Frontiers Contributors
// See AGPLv3.txt for details.
using Content.Shared._NF.Shuttles.Events;
using Content.Shared.Shuttles.BUIStates;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Physics.Components;
using System.Numerics;

namespace Content.Client.Shuttles.UI
{
    public partial class ShuttleNavControl // Mono
    {
        public InertiaDampeningMode DampeningMode { get; set; }

        /// <summary>
        /// Whether the shuttle is currently in FTL. This is used to disable the Park button
        /// while in FTL to prevent parking while traveling.
        /// </summary>
        public bool InFtl { get; set; }

        private void NfUpdateState(NavInterfaceState state)
        {

            if (!EntManager.GetCoordinates(state.Coordinates).HasValue ||
                !EntManager.TryGetComponent(EntManager.GetCoordinates(state.Coordinates).GetValueOrDefault().EntityId,out TransformComponent? transform) ||
                !EntManager.TryGetComponent(transform.GridUid, out PhysicsComponent? physicsComponent))
            {
                return;
            }

            DampeningMode = state.DampeningMode;

            // Check if the entity has an FTLComponent which indicates it's in FTL
            if (transform.GridUid != null)
            {
                InFtl = EntManager.HasComponent<FTLComponent>(transform.GridUid);
            }
            else
            {
                InFtl = false;
            }
        }

        // New Frontiers - Maximum IFF Distance - checks distance to object, draws if closer than max range
        // This code is licensed under AGPLv3. See AGPLv3.txt
        private bool NfCheckShouldDrawIffRangeCondition(bool shouldDrawIff, Vector2 distance)
        {
            if (shouldDrawIff && MaximumIFFDistance >= 0.0f)
            {
                if (distance.Length() > MaximumIFFDistance)
                {
                    shouldDrawIff = false;
                }
            }

            return shouldDrawIff;
        }
    }
}
