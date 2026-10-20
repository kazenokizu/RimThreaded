using System;
using UnityEngine;
using Verse;

namespace RimThreaded.RW_Patches
{
    // 1.6 port: SpawnSetup already bails out through Corpse.Bugged when InnerPawn is missing, so the 1.4 SpawnSetup transpiler is gone
    // (its IL pattern no longer exists). Drawing moved from DrawAt to DynamicDrawPhaseAt; keep the null guard for a corpse whose pawn
    // was removed on another thread while it is being drawn.
    class Corpse_Patch
    {
        internal static void RunDestructivePatches()
        {
            RimThreadedHarmony.Prefix(typeof(Corpse), typeof(Corpse_Patch), nameof(Corpse.DynamicDrawPhaseAt));
        }

        public static bool DynamicDrawPhaseAt(Corpse __instance, DrawPhase phase, Vector3 drawLoc, bool flip = false)
        {
            return __instance.InnerPawn != null;
        }
    }
}
