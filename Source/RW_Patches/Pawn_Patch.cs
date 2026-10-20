using System;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimThreaded.RW_Patches
{
    // 1.6 port: the 1.4 replacements of Pawn.Destroy, Pawn.VerifyReservations and Pawn.PostApplyDamage are gone. They were hand copies of vanilla
    // bodies (Destroy was a perf/crash workaround) and had drifted from 1.6: no Anomaly/Ideo/relations/meditation/render-tree work in Destroy,
    // no health.CanBleed or infection pathways in PostApplyDamage, a changed VerifyReservations(Job prevJob) signature. Vanilla is correct by
    // definition; PawnsFinder's shared result lists are thread-static via replacements_1.6.json, so the vanilla Destroy loop is safe on that point.
    // The old copy is kept in removed_1.6/.
    public class Pawn_Patch
    {
        internal static void RunDestructivePatches()
        {
            Type original = typeof(Pawn);
            Type patched = typeof(Pawn_Patch);
            RimThreadedHarmony.Prefix(original, patched, nameof(get_Inspired));
            RimThreadedHarmony.Prefix(original, patched, nameof(get_InAggroMentalState));
        }

        // Same logic as 1.6 vanilla, but each field is read once so a concurrent reset to null cannot cause an NRE between the check and the use.
        public static bool get_InAggroMentalState(Pawn __instance, ref bool __result)
        {
            Pawn_MindState mindState = __instance.mindState;
            if (mindState == null)
            {
                __result = false;
                return false;
            }
            __result = !__instance.Dead && mindState.mentalStateHandler.InMentalState && mindState.mentalStateHandler.CurStateDef.IsAggro;
            return false;
        }

        public static bool get_Inspired(Pawn __instance, ref bool __result)
        {
            Pawn_MindState mindState = __instance.mindState;
            InspirationHandler handler = mindState?.inspirationHandler;
            __result = !__instance.Dead && handler != null && handler.Inspired;
            return false;
        }
    }
}
