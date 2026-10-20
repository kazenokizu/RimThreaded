using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimThreaded.RW_Patches
{
    // 1.6 port: countedAmounts / resources are readonly now, so the 1.4 copy-on-write replacement of the dictionary
    // is no longer possible. UpdateResourceCounts can be called from worker threads (recipe toils, carry tracker),
    // so every reader/writer of countedAmounts takes the per-map lock instead.
    public class ResourceCounter_Patch
    {
        internal static void RunDestructivePatches()
        {
            Type original = typeof(ResourceCounter);
            Type patched = typeof(ResourceCounter_Patch);
            RimThreadedHarmony.Prefix(original, patched, nameof(ResourceCounter.GetCount));
            RimThreadedHarmony.TranspileMethodLock(original, nameof(ResourceCounter.UpdateResourceCounts));
            RimThreadedHarmony.TranspileMethodLock(original, "get_" + nameof(ResourceCounter.TotalHumanEdibleNutrition));
            RimThreadedHarmony.TranspileMethodLock(original, "get_" + nameof(ResourceCounter.TotalHumanBabyEdibleNutrition));
            RimThreadedHarmony.TranspileMethodLock(original, nameof(ResourceCounter.GetCountIn), new Type[] { typeof(ThingRequestGroup) });
        }

        public static bool GetCount(ResourceCounter __instance, ref int __result, ThingDef rDef)
        {
            if (rDef.resourceReadoutPriority == ResourceCountPriority.Uncounted)
            {
                __result = 0;
                return false;
            }
            lock (__instance)
            {
                if (__instance.countedAmounts.TryGetValue(rDef, out int value))
                {
                    __result = value;
                    return false;
                }
                Log.Error("Looked for nonexistent key " + rDef?.ToString() + " in counted resources.");
                __instance.countedAmounts.Add(rDef, 0);
            }
            __result = 0;
            return false;
        }
    }
}
