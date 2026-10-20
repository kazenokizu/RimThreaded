using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;
using static HarmonyLib.AccessTools;

namespace RimThreaded.RW_Patches
{
    // 1.6 port. The 1.4 replacement of TryRebuildDirtyRegionsAndRooms read the mod's own dirty-cell queue and lacked 1.6's
    // map.events.Notify_RegionsRoomsChanged() and newRegions.Clear(). Vanilla now runs; the whole rebuild holds the map's RegionDirtyer
    // monitor (the same one every RegionDirtyer method takes), which serializes rebuilds and keeps DirtyCells stable while it is iterated.
    // The wait is bounded: if the monitor is not obtained in time the rebuild is skipped (vanilla does the same while "working"), and
    // Map.MapUpdate retries it next frame. The old copy is in removed_1.6/.
    public class RegionAndRoomUpdater_Patch
    {
        internal static void InitializeThreadStatics() { }

        [ThreadStatic] static Stack<object> heldLocks;
        static Stack<object> HeldLocks => heldLocks ?? (heldLocks = new Stack<object>());
        static volatile bool warnedTimeout;

        internal static void RunDestructivePatches()
        {
            Type original = typeof(RegionAndRoomUpdater);
            foreach (string name in new[] { "TryRebuildDirtyRegionsAndRooms", "RebuildAllRegionsAndRooms" })
            {
                MethodInfo target = Method(original, name);
                if (target == null)
                {
                    Log.Error("RimThreaded PATCH FAILED [RegionAndRoomUpdater." + name + "]: method not found");
                    continue;
                }
                MethodInfo enter = Method(typeof(RegionAndRoomUpdater_Patch), nameof(Enter));
                RimThreadedHarmony.nonDestructivePrefixes.Add(enter);
                RimThreadedHarmony.harmony.Patch(target, prefix: new HarmonyMethod(enter, 1000),
                    finalizer: new HarmonyMethod(Method(typeof(RegionAndRoomUpdater_Patch), nameof(Exit)), -1000));
            }
        }

        public static bool Enter(RegionAndRoomUpdater __instance)
        {
            object monitor = __instance.map?.regionDirtyer;
            if (monitor == null)
            {
                HeldLocks.Push(null);
                return true;
            }
            if (Monitor.TryEnter(monitor, RimThreaded.halfTimeoutMS))
            {
                HeldLocks.Push(monitor);
                return true;
            }
            HeldLocks.Push(null);
            if (!warnedTimeout)
            {
                warnedTimeout = true;
                Log.Warning("RimThreaded: region rebuild skipped, region lock not obtained within " + (int)RimThreaded.halfTimeoutMS.TotalMilliseconds + " ms (retried next frame; logged once).");
            }
            return false;
        }

        public static void Exit(RegionAndRoomUpdater __instance)
        {
            if (HeldLocks.Count == 0) return;
            object monitor = HeldLocks.Pop();
            if (monitor != null) Monitor.Exit(monitor);
        }
    }
}
