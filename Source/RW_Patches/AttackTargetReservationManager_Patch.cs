using System;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimThreaded.RW_Patches
{
    // 1.6 port. The 1.4 patch rebuilt the vanilla list in place (Clear + AddRange) under a lock while readers iterated it without one, and left
    // CanReserve / ExposeData unlocked. Now vanilla logic, every method that touches the list serialized on the manager. The old patch is in
    // removed_1.6/.
    public class AttackTargetReservationManager_Patch
    {
        internal static void InitializeThreadStatics() { }

        internal static void RunDestructivePatches()
        {
            Type original = typeof(AttackTargetReservationManager);
            RimThreadedHarmony.TranspileMethodLock(original, "Reserve", new[] { typeof(Pawn), typeof(Job), typeof(IAttackTarget) });
            RimThreadedHarmony.TranspileMethodLock(original, "Release", new[] { typeof(Pawn), typeof(Job), typeof(IAttackTarget) });
            RimThreadedHarmony.TranspileMethodLock(original, "CanReserve", new[] { typeof(Pawn), typeof(IAttackTarget) });
            RimThreadedHarmony.TranspileMethodLock(original, "IsReservedBy", new[] { typeof(Pawn), typeof(IAttackTarget) });
            RimThreadedHarmony.TranspileMethodLock(original, "ReleaseAllForTarget", new[] { typeof(IAttackTarget) });
            RimThreadedHarmony.TranspileMethodLock(original, "ReleaseClaimedBy", new[] { typeof(Pawn), typeof(Job) });
            RimThreadedHarmony.TranspileMethodLock(original, "ReleaseAllClaimedBy", new[] { typeof(Pawn) });
            RimThreadedHarmony.TranspileMethodLock(original, "FirstReservationFor", new[] { typeof(Pawn) });
            RimThreadedHarmony.TranspileMethodLock(original, "ExposeData", Type.EmptyTypes);
            RimThreadedHarmony.TranspileMethodLock(original, "GetReservationsCount", new[] { typeof(IAttackTarget), typeof(Faction) });
        }
    }
}
