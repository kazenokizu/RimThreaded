using RimWorld;
using System;
using System.Collections.Generic;
using Verse;

namespace RimThreaded.RW_Patches
{
    class TaleManager_Patch
    {

        public static void RunDestructivePatches()
        {
            Type original = typeof(TaleManager);
            Type patched = typeof(TaleManager_Patch);
            RimThreadedHarmony.Prefix(original, patched, "Add");
            RimThreadedHarmony.Prefix(original, patched, "RemoveTale");
            // index loops over tales that remove entries: one thread's loop saw the list shrink under it when another thread culled
            // (1.7 Player.log run 9: ArgumentOutOfRangeException in RemoveExpiredTales). Each loop now holds the manager's lock.
            RimThreadedHarmony.TranspileMethodLock(original, "RemoveExpiredTales");
            RimThreadedHarmony.TranspileMethodLock(original, "CheckCullTales");
            RimThreadedHarmony.TranspileMethodLock(original, "Notify_PawnDestroyed");
            RimThreadedHarmony.TranspileMethodLock(original, "Notify_PawnDiscarded");
            // reader loop over the same list (mood ticks on worker threads); RemoveTale swaps in a shorter list mid-loop otherwise
            RimThreadedHarmony.TranspileMethodLock(original, "GetLatestTale");
        }

        public static bool Add(TaleManager __instance, Tale tale)
        {
            lock (__instance)
            {
                __instance.tales.Add(tale);
                __instance.CheckCullTales(tale);
            }
            return false;
        }
        public static bool RemoveTale(TaleManager __instance, Tale tale)
        {
            if (!tale.Unused)
            {
                Log.Warning("Tried to remove used tale " + tale);
            }
            else
            {
                lock (__instance)
                {
                    List<Tale> newTales = new List<Tale>(__instance.tales);
                    newTales.Remove(tale);
                    __instance.tales = newTales;
                }
            }
            return false;
        }


    }
}
