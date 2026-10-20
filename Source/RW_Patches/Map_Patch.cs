using RimWorld;
using System;
using System.Collections.Generic;
using Verse;
using RimWorld.Planet;

namespace RimThreaded.RW_Patches
{
    class Map_Patch
    {

        internal static void RunDestructivePatches()
        {
            Type original = typeof(Map);
            Type patched = typeof(Map_Patch);
            RimThreadedHarmony.Prefix(original, patched, "get_IsPlayerHome");
        }

        // 1.6 logic (null-safe): gravship-landed maps and maps with a player grav engine also count as player home (Odyssey).
        // The 1.4 copy returned false for those.
        public static bool get_IsPlayerHome(Map __instance, ref bool __result)
        {
            MapParent parent = __instance.info?.parent;
            if (!__instance.wasSpawnedViaGravShipLanding && (parent == null || parent.Faction != Faction.OfPlayer || parent.def == null || !parent.def.canBePlayerHome))
            {
                __result = GravshipUtility.PlayerHasGravEngine(__instance);
                return false;
            }
            __result = true;
            return false;
        }

        public static void MapsPostTickPrepare()
        {
            SteadyEnvironmentEffects_Patch.totalSteadyEnvironmentEffectsTicks = 0;
            SteadyEnvironmentEffects_Patch.steadyEnvironmentEffectsTicksCompleted = 0;
            SteadyEnvironmentEffects_Patch.steadyEnvironmentEffectsCount = 0;
            WildPlantSpawner_Patch.wildPlantSpawnerCount = 0;
            WildPlantSpawner_Patch.wildPlantSpawnerTicksCount = 0;
            WildPlantSpawner_Patch.wildPlantSpawnerTicksCompleted = 0;
            try
            {
                List<Map> maps = Find.Maps;
                for (int j = 0; j < maps.Count; j++)
                {
                    Map map = maps[j];
                    map.MapPostTick();
                }
            }
            catch (Exception ex3)
            {
                Log.Error(ex3.ToString());
            }

        }

        public static void MapPostListTick()
        {
            SteadyEnvironmentEffects_Patch.SteadyEffectTick();
            WildPlantSpawner_Patch.WildPlantSpawnerListTick();
        }
    }
}
