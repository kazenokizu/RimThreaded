using RimWorld;
using System;
using Verse;

namespace RimThreaded.RW_Patches
{
    class FireUtility_Patch
    {
        internal static void RunDestructivePatches()
        {
            Type original = typeof(FireUtility);
            Type patched = typeof(FireUtility_Patch);
            RimThreadedHarmony.Prefix(original, patched, nameof(TryAttachFire), null, false);
            RimThreadedHarmony.Prefix(original, patched, nameof(ChanceToStartFireIn), null, false);
        }

        // 1.6 port: the 1.4 copy of vanilla TryAttachFire lacked the new "instigator" argument (Fire.instigator). Vanilla now runs unchanged;
        // this prefix only skips the call when the thing has no map (despawned on another thread) or a pawn has no job tracker, both of which
        // would throw inside vanilla.
        public static bool TryAttachFire(Thing t)
        {
            if (t == null || t.Map == null)
                return false;
            if (t is Pawn pawn && pawn.jobs == null)
                return false;
            return true;
        }
        public static bool ChanceToStartFireIn(ref float __result, IntVec3 c, Map map)
        {
            if (map == null)
            {
                __result = 0f;
                return false;
            }
            return true;
        }
    }
}
