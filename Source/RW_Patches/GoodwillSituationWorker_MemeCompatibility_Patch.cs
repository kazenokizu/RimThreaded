using RimWorld;
using System;

namespace RimThreaded.RW_Patches
{
    class GoodwillSituationWorker_MemeCompatibility_Patch
    {
        public static void RunDestructivePatches()
        {
            Type original = typeof(GoodwillSituationWorker_MemeCompatibility);
            Type patched = typeof(GoodwillSituationWorker_MemeCompatibility_Patch);
            RimThreadedHarmony.Prefix(original, patched, nameof(Applies), new Type[] { typeof(Faction), typeof(Faction) });
        }

        // Guard only: a worker without a def answers false; otherwise vanilla 1.6 runs. (The 1.4 version returned its computed answer as the
        // prefix result instead of __result, which only gave the right value by coincidence.)
        public static bool Applies(GoodwillSituationWorker_MemeCompatibility __instance, ref bool __result, Faction a, Faction b)
        {
            if (__instance.def == null || a?.ideos == null || b?.ideos == null)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }
}
