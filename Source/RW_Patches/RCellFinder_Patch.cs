using System;
using RimWorld;
using Verse;

namespace RimThreaded.RW_Patches
{
    // Worker threads never rebuild regions (RegionGrid_Patch.GetValidRegionAt); dirty regions are rebuilt on the main thread before each
    // tick (TickManager_Patch.DoSingleTick) and every frame. Inside a tick, a pawn standing on a cell whose region was invalidated earlier in
    // the same tick has no region. Vanilla SpotToStandDuringJob then passes null to CellFinder.RandomRegionNear (ArgumentNullException 'root',
    // 1.7 Player.log run 7) and would also dereference it at the end. Returning IntVec3.Invalid is a result vanilla already produces
    // (extraValidator rejects the fallback cell), and its callers handle it.
    public class RCellFinder_Patch
    {
        internal static void RunDestructivePatches()
        {
            RimThreadedHarmony.Prefix(typeof(RCellFinder), typeof(RCellFinder_Patch), nameof(SpotToStandDuringJob),
                new Type[] { typeof(Pawn), typeof(Predicate<IntVec3>), typeof(Region) }, false);
        }

        // parameter list = (ref __result, original parameters): RimThreadedHarmony.Prefix looks the patch method up by that signature
        public static bool SpotToStandDuringJob(ref IntVec3 __result, Pawn pawn, Predicate<IntVec3> extraValidator, Region targetRegion)
        {
            if (targetRegion != null || pawn.GetRegion() != null)
                return true;
            __result = IntVec3.Invalid;
            return false;
        }
    }
}
