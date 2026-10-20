using RimWorld;
using Verse;
using System;
using System.Text.RegularExpressions;

namespace RimThreaded.RW_Patches
{
    class ColoredText_Patch
    {
        [ThreadStatic] public static Regex ColonistCountRegex;

        // Same pattern vanilla 1.6 ColoredText.ResetStaticData builds. Guarded: this runs inside RimThreaded.InitializeAllThreadStatics, and an
        // exception there would also skip every generated thread-static initializer (Harmony postfixes) that follows it.
        public static void InitializeThreadStatics()
        {
            FactionDef playerColony = FactionDefOf.PlayerColony;
            if (playerColony == null)
                return;
            ColonistCountRegex = new Regex("\\d+\\.?\\d* " + "(" + playerColony.pawnsPlural + "|" + playerColony.pawnSingular + ")");
        }

    }
}
