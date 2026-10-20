using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimThreaded.RW_Patches
{
    // 1.6 port: the 1.4 version re-implemented Thing/Building/Building_Bed.SpawnSetup by hand.
    // Those methods changed a lot in 1.6, so the whole chain is left vanilla and only the part
    // that is unsafe on worker threads (Room.Notify_RoomShapeChanged, which dereferences Room.Map
    // while districts are being rebuilt) is replaced.
    class Building_Bed_Patch
    {
        internal static void RunDestructivePatches()
        {
            Type original = typeof(Room);
            Type patched = typeof(Building_Bed_Patch);
            RimThreadedHarmony.Prefix(original, patched, nameof(Room.Notify_RoomShapeChanged));
        }

        public static bool Notify_RoomShapeChanged(Room __instance)
        {
            Room r = __instance;
            Map tmpMap = r.Map; // read once, can become null while districts are rebuilt
            if (tmpMap is null)
            {
                return false;
            }
            r.cachedCellCount = -1;
            r.cachedOpenRoofCount = -1;
            r.cachedExposedCount = -1;
            r.cachedExposedThreshold = 0;
            if (r.Dereferenced)
            {
                r.isPrisonCell = false;
                r.statsAndRoleDirty = true;
                return false;
            }
            r.tempTracker.RoomChanged();
            if (Current.ProgramState == ProgramState.Playing && !r.Fogged && !GravshipPlacementUtility.placingGravship)
            {
                tmpMap.autoBuildRoofAreaSetter.TryGenerateAreaFor(r);
            }
            r.isPrisonCell = false;
            if (Building_Bed.RoomCanBePrisonCell(r))
            {
                List<Thing> containedAndAdjacentThings = r.ContainedAndAdjacentThings;
                for (int i = 0; i < containedAndAdjacentThings.Count; i++)
                {
                    if (containedAndAdjacentThings[i] is Building_Bed building_Bed && building_Bed.ForPrisoners)
                    {
                        r.isPrisonCell = true;
                        break;
                    }
                }
            }
            List<Thing> list = tmpMap.listerThings.ThingsOfDef(ThingDefOf.NutrientPasteDispenser);
            for (int j = 0; j < list.Count; j++)
            {
                list[j].Notify_ColorChanged();
            }
            if (Current.ProgramState == ProgramState.Playing && r.isPrisonCell)
            {
                foreach (Building_Bed containedBed in r.ContainedBeds)
                {
                    containedBed.ForPrisoners = true;
                }
            }
            r.statsAndRoleDirty = true;
            return false;
        }
    }
}
