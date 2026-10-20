using RimWorld;
using System;
using Verse;

namespace RimThreaded.RW_Patches
{
    class RoofGrid_Patch
    {
        internal static void RunDestructivePatches()
        {
            Type original = typeof(RoofGrid);
            Type patched = typeof(RoofGrid_Patch);

            RimThreadedHarmony.Prefix(original, patched, nameof(SetRoof));
        }
        public static bool SetRoof(RoofGrid __instance, IntVec3 c, RoofDef def)
        {
            Map map = __instance.map;
            int mcc = map.cellIndices.CellToIndex(c);
            if (__instance.roofGrid[mcc] != def)
            {
                __instance.roofGrid[mcc] = def;
                // 1.6 vanilla body (district-based roof notification, glow dirty via DirtyCell, event notify)
                map.regionGrid.GetValidRegionAt_NoRebuild(c)?.District?.Notify_RoofChanged();
                if (__instance.drawerInt != null)
                {
                    __instance.drawerInt.SetDirty();
                }
                map.mapDrawer.MapMeshDirty(c, MapMeshFlagDefOf.Roofs);
                map.glowGrid.DirtyCell(c);
                map.events.Notify_RoofChanged(c);
            }
            return false;
        }
    }
}