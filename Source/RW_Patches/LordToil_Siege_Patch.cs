using System;
using RimWorld;

namespace RimThreaded.RW_Patches
{
    // 1.6 port: the 1.4 copy of UpdateAllDuties had an inverted check ("if rememberedDuties.ContainsKey(pawn) Add"), so existing builders were
    // never remembered and the defender pass turned every builder back into a defender on each update. Vanilla now runs, serialized on the toil
    // (its rememberedDuties dictionary is per-toil instance state). The old copy is in removed_1.6/.
    class LordToil_Siege_Patch
    {
        internal static void RunDestructivePatches()
        {
            RimThreadedHarmony.TranspileMethodLock(typeof(LordToil_Siege), nameof(LordToil_Siege.UpdateAllDuties));
        }
    }
}
