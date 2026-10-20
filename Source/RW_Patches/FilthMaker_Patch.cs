using RimWorld;
using System;
using System.Collections.Generic;
using Verse;

namespace RimThreaded.RW_Patches
{
    class FilthMaker_Patch
    {
        public static readonly Type original = typeof(FilthMaker);
        public static readonly Type patched = typeof(FilthMaker_Patch);

        public static void RunDestructivePatches()
        {
            RimThreadedHarmony.Prefix(original, patched, nameof(TryMakeFilth), new Type[] { typeof(IntVec3), typeof(Map), typeof(ThingDef), typeof(IEnumerable<string>), typeof(bool), typeof(Filth).MakeByRefType(), typeof(FilthSourceFlags) }, false);
        }

        public static bool TryMakeFilth(ref bool __result, IntVec3 c, Map map, ThingDef filthDef, IEnumerable<string> sources, bool shouldPropagate, out Filth outFilth, FilthSourceFlags additionalFlags = FilthSourceFlags.None)
        {
            __result = false;
            outFilth = null;
            if (map == null)
                return false;
            return true;
        }


    }
}