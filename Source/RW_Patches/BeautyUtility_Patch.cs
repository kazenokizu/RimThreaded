using RimWorld;
using System;
using System.Collections.Generic;
using Verse;
using static RimWorld.BeautyUtility;

namespace RimThreaded.RW_Patches
{
    class BeautyUtility_Patch
    {
        internal static void RunDestructivePatches()
        {
            Type original = typeof(BeautyUtility);
            Type patched = typeof(BeautyUtility_Patch);
            RimThreadedHarmony.Prefix(original, patched, nameof(CellBeauty));
        }
        // 1.6: the 1.4 hand copy had drifted (HashSet countedThings, GetBeauty, filth check, and it skipped every thing when countedThings was null).
        // Only the null guards the copy existed for are kept; vanilla runs for everything else.
        public static bool CellBeauty(ref float __result, IntVec3 c, Map map, HashSet<Thing> countedThings = null)
        {
            if (map == null || map.terrainGrid == null || map.thingGrid == null || map.roofGrid == null)
            {
                __result = 0f;
                return false;
            }
            return true;
        }
    }
}
