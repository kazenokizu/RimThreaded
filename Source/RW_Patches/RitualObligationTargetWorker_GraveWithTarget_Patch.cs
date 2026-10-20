using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimThreaded.RW_Patches
{
    class RitualObligationTargetWorker_GraveWithTarget_Patch
    {
        internal static void RunDestructivePatches()
        {
            Type original = typeof(RitualObligationTargetWorker_GraveWithTarget);
            Type patched = typeof(RitualObligationTargetWorker_GraveWithTarget_Patch);
            RimThreadedHarmony.Prefix(original, patched, nameof(LabelExtraPart), new Type[] { typeof(RitualObligation) });
            RimThreadedHarmony.Prefix(original, patched, nameof(GetTargetInfos));
        }
        // 1.6 logic made null-safe. The 1.4 copies always cast targetA to Corpse; in 1.6 the target may also be a Pawn (vanilla checks
        // "Thing is Pawn" first), so the old cast threw InvalidCastException. 1.6 also has the classic-mode text and no "must be buried" suffix.
        internal static IEnumerable<string> GetTargetInfos_E(RitualObligationTargetWorker_GraveWithTarget __instance, RitualObligation obligation)
        {
            if (obligation == null)
            {
                if (Find.IdeoManager.classicMode)
                    yield return "RitualTargetGraveInfoAbstractNoIdeo".Translate();
                else
                    yield return "RitualTargetGraveInfoAbstract".Translate(__instance.parent.ideo.Named("IDEO"));
                yield break;
            }
            Pawn pawn = TargetPawn(obligation);
            if (pawn == null)
                yield break;
            yield return "RitualTargetGraveInfo".Translate(pawn.Named("PAWN"));
        }

        static Pawn TargetPawn(RitualObligation obligation)
        {
            Thing thing = obligation?.targetA.Thing;
            if (thing is Pawn pawn)
                return pawn;
            return (thing as Corpse)?.InnerPawn;
        }

        public static bool GetTargetInfos(RitualObligationTargetWorker_GraveWithTarget __instance, ref IEnumerable<string> __result, RitualObligation obligation)
        {
            __result = GetTargetInfos_E(__instance, obligation);
            return false;
        }

        public static bool LabelExtraPart(RitualObligationTargetWorker_GraveWithTarget __instance, ref string __result, RitualObligation obligation)
        {
            __result = TargetPawn(obligation)?.LabelShort ?? string.Empty;
            return false;
        }
    }
}
