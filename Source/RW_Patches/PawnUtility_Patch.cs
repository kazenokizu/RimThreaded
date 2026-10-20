using System;
using RimWorld;
using Verse;
using static HarmonyLib.AccessTools;

namespace RimThreaded.RW_Patches
{
    // 1.6 port: the 1.4 patch replaced PawnBlockingPathAt with a hand copy (1.6 split it into PawnBlockedBy and added the useId argument)
    // and cached IsInvisible per pawn (the method no longer exists; IsPsychologicallyInvisible only walks the copy-on-write hediff list).
    // Vanilla 1.6 is used. The one real hazard is other.jobs.curDriver being cleared by another thread between the CurJob check and the
    // curDriver read in PawnBlockedBy; a pawn that is switching jobs is simply treated as not blocking.
    public static class PawnUtility_Patch
    {
        public static void RunDestructivePatches()
        {
            try
            {
                RimThreadedHarmony.harmony.Patch(Method(typeof(PawnUtility), nameof(PawnUtility.PawnBlockedBy)),
                    finalizer: new HarmonyLib.HarmonyMethod(typeof(PawnUtility_Patch), nameof(PawnBlockedByFinalizer)));
            }
            catch (Exception e)
            {
                RimThreadedHarmony.LogPatchFailure("Finalizer", typeof(PawnUtility), typeof(PawnUtility_Patch), nameof(PawnUtility.PawnBlockedBy), e);
            }
        }

        public static Exception PawnBlockedByFinalizer(Exception __exception, ref bool __result)
        {
            if (__exception is NullReferenceException)
            {
                __result = false;
                return null;
            }
            return __exception;
        }
    }
}
