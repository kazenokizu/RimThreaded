using System;
using System.Collections.Generic;
using Verse;

namespace RimThreaded.RW_Patches
{
    // 1.6 port. Removed 1.4 copies that had drifted: AddDistrict/RemoveDistrict wrote RegionGrid.allRooms directly, bypassing 1.6's
    // RegionGrid.RoomAdded/RoomRemoved (which also maintain roomLookup by room ID); UpdateRoomStatsAndRole used the old 36-region limit
    // (1.6: 60) so mid-size rooms got no stats/role; Notify_RoofChanged lacked the vacuum caches. Vanilla now runs serialized on
    // RoomDistrictLock (see District_Patch.cs). Kept: null-safe Fogged and an allocation-per-call ContainedAndAdjacentThings.
    // Added: Regions returns a fresh list (vanilla's tmpRegions is thread-static via replacements_1.6.json, i.e. one list per thread shared by
    // every room, so nested calls on two rooms would overwrite each other), and the 1.6 Vacuum setter broadcasts to the room's things itself
    // (vanilla used uniqueContainedThings, which the ContainedAndAdjacentThings replacement never fills). The old copy is in removed_1.6/.
    class Room_Patch
    {
        internal static void RunDestructivePatches()
        {
            Type original = typeof(Room);
            Type patched = typeof(Room_Patch);
            RoomDistrictLock.Patch(original, nameof(Room.AddDistrict));
            RoomDistrictLock.Patch(original, nameof(Room.RemoveDistrict));
            RoomDistrictLock.Patch(original, nameof(Room.Notify_RoofChanged));
            RoomDistrictLock.Patch(original, nameof(Room.Notify_TerrainChanged));
            RoomDistrictLock.Patch(original, "UpdateRoomStatsAndRole");
            RoomDistrictLock.Patch(original, "OpenRoofCountStopAt");
            RoomDistrictLock.Patch(original, "ExposedCountStopAt");
            RimThreadedHarmony.Prefix(original, patched, nameof(get_Fogged));
            RimThreadedHarmony.Prefix(original, patched, nameof(get_ContainedAndAdjacentThings));
            RimThreadedHarmony.Prefix(original, patched, nameof(get_Regions));
            RimThreadedHarmony.Prefix(original, patched, nameof(set_Vacuum));
        }

        public static bool get_Regions(Room __instance, ref List<Region> __result)
        {
            List<Region> result = new List<Region>();
            lock (RoomDistrictLock.Monitor)
            {
                List<District> districts = __instance.districts;
                for (int i = 0; i < districts.Count; i++)
                    result.AddRange(districts[i].Regions);
            }
            __result = result;
            return false;
        }

        public static bool set_Vacuum(Room __instance, float value)
        {
            __instance.vacuum = UnityEngine.Mathf.Clamp(value, -0.1f, 1f);
            List<Thing> things = new List<Thing>();
            get_ContainedAndAdjacentThings(__instance, ref things);
            foreach (Thing thing in things)
            {
                if (thing is ThingWithComps thingWithComps)
                    thingWithComps.BroadcastCompSignal("Vacuum");
            }
            return false;
        }

        public static bool get_ContainedAndAdjacentThings(Room __instance, ref List<Thing> __result)//fixes problems with rimfridge.
        {
            HashSet<Thing> uniqueContainedThingsSet = new HashSet<Thing>();
            List<Thing> uniqueContainedThings = new List<Thing>();
            List<Region> regions = __instance.Regions;
            for (int i = 0; i < regions.Count; i++)
            {
                List<Thing> allThings = regions[i].ListerThings.AllThings;
                if (allThings == null)
                {
                    continue;
                }
                for (int j = 0; j < allThings.Count; j++)
                {
                    Thing item = allThings[j];
                    if (uniqueContainedThingsSet.Add(item))
                    {
                        uniqueContainedThings.Add(item);
                    }
                }
            }
            __result = uniqueContainedThings;
            return false;
        }

        public static bool get_Fogged(Room __instance, ref bool __result)
        {
            __result = false;
            if (__instance.RegionCount == 0)
            {
                return false;
            }
            Region tmpRegion = __instance.FirstRegion;
            Map tmpMap = __instance.Map;
            if (tmpMap == null || tmpRegion == null)
            {
                return false;
            }
            IntVec3 pos = tmpRegion.AnyCell;
            __result = pos.Fogged(tmpMap);
            return false;
        }
    }
}
