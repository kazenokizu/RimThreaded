using System;
using System.Collections.Generic;
using Verse;

namespace RimThreaded.RW_Patches
{
    class RegionGrid_Patch
    {
        public static void RunDestructivePatches()
        {
            Type original = typeof(RegionGrid);
            Type patched = typeof(RegionGrid_Patch);
            RimThreadedHarmony.Prefix(original, patched, nameof(GetValidRegionAt));
            RimThreadedHarmony.Prefix(original, patched, nameof(UpdateClean));
        }

        // RimThreaded policy (unchanged from 1.4): a query never triggers a region rebuild, except on the map's generation tick (raid/quest map
        // generation needs reachability right away). Rebuilds happen in Map.MapUpdate on the main thread. Otherwise identical to 1.6 vanilla,
        // including the 1.6 "disposed" check. The rebuild takes the region lock itself (RegionAndRoomUpdater_Patch).
        public static bool GetValidRegionAt(RegionGrid __instance, ref Region __result, IntVec3 c)
        {
            if (__instance.disposed)
            {
                __result = null;
                return false;
            }
            Map map = __instance.map;
            RegionAndRoomUpdater regionAndRoomUpdater = map.regionAndRoomUpdater;
            if (!c.InBounds(map))
            {
                Log.Error("Tried to get valid region out of bounds at " + c);
                __result = null;
                return false;
            }
            if (!regionAndRoomUpdater.Enabled && regionAndRoomUpdater.AnythingToRebuild)
            {
                Log.Warning("Trying to get valid region at " + c + " but RegionAndRoomUpdater is disabled. The result may be incorrect.");
            }
            int index = map.cellIndices.CellToIndex(c);
            Region region = __instance.regionGrid[index];
            if ((region == null || !region.valid) && Find.TickManager.TicksGame - map.generationTick == 0) //credit to BlackJack
            {
                regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                region = __instance.regionGrid[index];
            }
            __result = region != null && region.valid ? region : null;
            return false;
        }

        public static bool UpdateClean(RegionGrid __instance)
        {
            /*
            for (int i = 0; i < 16; i++)
            {
                if (__instance.curCleanIndex >= __instance.regionGrid.Length)
                {
                    __instance.curCleanIndex = 0;
                }
                Region region = __instance.regionGrid[__instance.curCleanIndex];
                if (region != null && !region.valid)
                {
                    lock (__instance.map.regionAndRoomUpdater)
                    {
                        __instance.regionGrid[__instance.curCleanIndex] = null;
                        __instance.map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                    }
                }
                __instance.curCleanIndex++;
            }
            */
            return false;
        }
    }
}
