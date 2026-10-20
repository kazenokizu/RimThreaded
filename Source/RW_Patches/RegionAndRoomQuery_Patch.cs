using System;
using Verse;

namespace RimThreaded.RW_Patches
{

    public class RegionAndRoomQuery_Patch
    {

        static readonly Type original = typeof(RegionAndRoomQuery);
        static readonly Type patched = typeof(RegionAndRoomQuery_Patch);

        internal static void RunNonDestructivePatches()
        {
            RimThreadedHarmony.Postfix(original, patched, nameof(RoomAt));
        }

        // 1.6 port: the retry used to run under lock(map.regionAndRoomUpdater). Since the rebuild holds the RegionDirtyer monitor
        // instead (RegionAndRoomUpdater_Patch), nothing else takes that lock, so it only made every null lookup (wall cells, etc.)
        // on every thread queue behind each other. The single retry is kept; it is the upstream behaviour.
        public static void RoomAt(ref Room __result, IntVec3 c, Map map, RegionType allowedRegionTypes = RegionType.Set_All)
        {
            if (__result == null && map != null)
                __result = RegionAndRoomQuery.DistrictAt(c, map, allowedRegionTypes)?.Room;
        }


    }
}