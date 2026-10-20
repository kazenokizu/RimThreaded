using System;
using Verse;
using Verse.AI.Group;

namespace RimThreaded.RW_Patches
{
    // 1.6 port: the 1.4 pawn->lord dictionary cache and the AddPawn/AddPawns/RemovePawn copies are gone. 1.6 stores the lord on the pawn
    // (Pawn.lord, Pawn.GetLord() is a field read), so the cache is unnecessary, and the old copies never assigned Pawn.lord, which would have
    // made every lord member report "no lord". Vanilla runs unchanged; the two private/public mutators of ownedPawns are only serialized.
    // The old copies are kept in removed_1.6/.
    class Lord_Patch
    {
        internal static void RunDestructivePatches()
        {
            Type original = typeof(Lord);
            RimThreadedHarmony.TranspileMethodLock(original, "AddPawnInternal", new Type[] { typeof(Pawn), typeof(bool) });
            RimThreadedHarmony.TranspileMethodLock(original, nameof(Lord.RemovePawn));
        }
    }
}
