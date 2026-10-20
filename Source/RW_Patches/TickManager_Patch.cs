using Verse;
using UnityEngine;
using System;
using System.Collections.Generic;

namespace RimThreaded.RW_Patches
{
    public class TickManager_Patch
    {
        public static void RunDestructivePatches()
        {
            Type original = typeof(TickManager);
            Type patched = typeof(TickManager_Patch);
            RimThreadedHarmony.Prefix(original, patched, nameof(DoSingleTick));
            RimThreadedHarmony.Prefix(original, patched, nameof(get_TickRateMultiplier));
        }

        public static bool DoSingleTick(TickManager __instance)
        {
            //RimThreaded.currentInstance = __instance;

            // Same place as vanilla (before the tick counter moves), on the main thread while no worker is ticking:
            // - MapPreTick runs PathFinder.PathFinderTick -> PathFinderMapData.GatherData, which enumerates the cell-change lists that
            //   spawns/despawns append to. Run as a worker "prepare" step (WindManagerPrepare) it overlapped other lists' ticks:
            //   "Collection was modified" in GatherData and "Pathfinder current work queue is not empty" (1.7 Player.log, run 7).
            // - Region rebuild: workers never rebuild regions (RegionGrid_Patch.GetValidRegionAt), so without this a dirty region stayed
            //   invalid until the next frame's MapUpdate - hundreds of ticks at high speed - and region queries returned null meanwhile
            //   (ArgumentNullException 'root' in CellFinder.RandomRegionNear from RCellFinder.SpotToStandDuringJob).
            List<Map> maps = Find.Maps;
            for (int i = 0; i < maps.Count; i++)
            {
                try
                {
                    maps[i].regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                }
                catch (Exception ex)
                {
                    Log.Error("RimThreaded: region rebuild before tick: " + ex);
                }
                maps[i].MapPreTick();
            }

            if (!DebugSettings.fastEcology)
            {
                __instance.ticksGameInt++;
            }
            else
            {
                __instance.ticksGameInt += 2000;
            }
            Shader.SetGlobalFloat(ShaderPropertyIDs.GameSeconds, __instance.TicksGame.TicksToSeconds());

            RimThreaded.MainThreadWaitLoop(__instance);

            if (DebugViewSettings.logHourlyScreenshot && Find.TickManager.TicksGame >= __instance.lastAutoScreenshot + 2500)
            {
                ScreenshotTaker.QueueSilentScreenshot();
                __instance.lastAutoScreenshot = Find.TickManager.TicksGame / 2500 * 2500;
            }

            Debug.developerConsoleVisible = false;
            return false;
        }


        public static bool get_TickRateMultiplier(TickManager __instance, ref float __result)
        {
            if (__instance.slower.ForcedNormalSpeed && !RimThreadedMod.Settings.disableforcedslowdowns)
            {
                TimeControls_Patch.lastTickForcedSlow = true;
                if (!TimeControls_Patch.overrideForcedSlow)
                {
                    __result = __instance.curTimeSpeed == TimeSpeed.Paused ? 0.0f : 1f;
                    return false;
                }
            }
            else
            {
                TimeControls_Patch.lastTickForcedSlow = false;
                TimeControls_Patch.overrideForcedSlow = false;
            }

            switch (__instance.curTimeSpeed)
            {
                case TimeSpeed.Paused:
                    __result = 0.0f;
                    return false;
                case TimeSpeed.Normal:
                    __result = RimThreaded.timeSpeedNormal;
                    return false;
                case TimeSpeed.Fast:
                    __result = RimThreaded.timeSpeedFast;
                    return false;
                case TimeSpeed.Superfast:
                    __result = Find.Maps.Count == 0 ? RimThreaded.timeSpeedSuperfast : RimThreaded.timeSpeedSuperfast;
                    return false;
                case TimeSpeed.Ultrafast:
#if RW17
                    // 1.7: the dev-mode ultra speed boost returns 20000 (1.6 returned 150, the same as this mod's default)
                    if (TickManager.UltraSpeedBoost)
                    {
                        __result = 20000f;
                        return false;
                    }
#endif
                    __result = Find.Maps.Count == 0 ? RimThreaded.timeSpeedUltrafast : RimThreaded.timeSpeedUltrafast;
                    return false;
                default:
                    __result = -1f;
                    return false;
            }

        }

    }
}
