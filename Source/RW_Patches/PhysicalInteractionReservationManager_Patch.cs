using System;
using System.Collections.Generic;
using Verse;
using Verse.AI;

namespace RimThreaded.RW_Patches
{
    // 1.6 port. The 1.4 patch moved physical-interaction reservations into private dictionaries, but the unpatched vanilla ReserversOf (used by
    // ReservationManager.ReserversOf) and ExposeData still read the vanilla list, so these reservations were invisible there and never saved
    // (upstream's own TODO: "write ExposeData"). Now vanilla storage and logic, every method serialized on the manager. The old patch is in
    // removed_1.6/.
    class PhysicalInteractionReservationManager_Patch
    {
        internal static void RunDestructivePatches()
        {
            Type original = typeof(PhysicalInteractionReservationManager);
            RimThreadedHarmony.TranspileMethodLock(original, "Reserve", new[] { typeof(Pawn), typeof(Job), typeof(LocalTargetInfo) });
            RimThreadedHarmony.TranspileMethodLock(original, "Release", new[] { typeof(Pawn), typeof(Job), typeof(LocalTargetInfo) });
            RimThreadedHarmony.TranspileMethodLock(original, "TryRelease", new[] { typeof(Pawn), typeof(Job), typeof(LocalTargetInfo) });
            RimThreadedHarmony.TranspileMethodLock(original, "IsReservedBy", new[] { typeof(Pawn), typeof(LocalTargetInfo) });
            RimThreadedHarmony.TranspileMethodLock(original, "FirstReserverOf", new[] { typeof(LocalTargetInfo) });
            RimThreadedHarmony.TranspileMethodLock(original, "ReserversOf", new[] { typeof(LocalTargetInfo), typeof(HashSet<Pawn>) });
            RimThreadedHarmony.TranspileMethodLock(original, "FirstReservationFor", new[] { typeof(Pawn) });
            RimThreadedHarmony.TranspileMethodLock(original, "ReleaseAllForTarget", new[] { typeof(LocalTargetInfo) });
            RimThreadedHarmony.TranspileMethodLock(original, "ReleaseClaimedBy", new[] { typeof(Pawn), typeof(Job) });
            RimThreadedHarmony.TranspileMethodLock(original, "ReleaseAllClaimedBy", new[] { typeof(Pawn) });
            RimThreadedHarmony.TranspileMethodLock(original, "ExposeData", Type.EmptyTypes);
        }
    }
}
