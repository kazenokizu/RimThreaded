using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimThreaded.RW_Patches
{
    class Alert_MinorBreakRisk_Patch
    {
        public static void RunDestructivePatches()
        {
            Type original = typeof(Alert_MinorBreakRisk);
            Type patched = typeof(Alert_MinorBreakRisk_Patch);
            RimThreadedHarmony.Prefix(original, patched, nameof(GetReport));
        }
        public static bool GetReport(Alert_MinorBreakRisk __instance, ref AlertReport __result)
        {
            // 1.6 logic (BreakRiskAlertUtility), computed with local lists instead of shared static lists
            List<Pawn> pawnsAtRiskMinorResult = new List<Pawn>();
            List<Pawn> pawnList = PawnsFinder.AllMapsCaravansAndTravellingTransporters_AliveSpawned_FreeColonists_NoSuspended;
            for (int i = 0; i < pawnList.Count; i++)
            {
                Pawn item = pawnList[i];
                if (item.Downed) continue;
                MentalBreaker breaker = item.mindState.mentalBreaker;
                if (breaker.BreakExtremeIsImminent || breaker.BreakMajorIsImminent)
                {
                    __result = false;
                    return false;
                }
                if (breaker.BreakMinorIsImminent)
                {
                    pawnsAtRiskMinorResult.Add(item);
                }
            }
            __result = AlertReport.CulpritsAre(pawnsAtRiskMinorResult);
            return false;
        }
    }
}
