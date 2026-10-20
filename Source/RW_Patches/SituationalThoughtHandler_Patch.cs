using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimThreaded.RW_Patches
{
    // 1.6 port: the 1.4 patch replaced vanilla bodies with hand-copied code that no longer matches 1.6
    // (thoughtsDirty flag, UpdateAllMoodThoughts, in-place activeThoughts refresh). Vanilla 1.6 is now used as is,
    // and every method that reads or writes the per-pawn thought caches is serialized on the handler instance.
    // Lock order risk: two pawns' handlers could in theory lock each other if a thought worker reads the other
    // pawn's situational thoughts; none of the vanilla 1.6 workers do that, but modded ones might.
    public class SituationalThoughtHandler_Patch
    {
        internal static void RunDestructivePatches()
        {
            Type original = typeof(SituationalThoughtHandler);
            RimThreadedHarmony.TranspileMethodLock(original, nameof(SituationalThoughtHandler.AppendMoodThoughts), new Type[] { typeof(List<Thought>) });
            RimThreadedHarmony.TranspileMethodLock(original, nameof(SituationalThoughtHandler.AppendMoodThoughts), new Type[] { typeof(ThoughtDef), typeof(List<Thought>) });
            RimThreadedHarmony.TranspileMethodLock(original, nameof(SituationalThoughtHandler.AppendSocialThoughts));
            RimThreadedHarmony.TranspileMethodLock(original, nameof(SituationalThoughtHandler.SituationalThoughtInterval));
            RimThreadedHarmony.TranspileMethodLock(original, nameof(SituationalThoughtHandler.Notify_SituationalThoughtsDirty));
            RimThreadedHarmony.TranspileMethodLock(original, "UpdateAllMoodThoughts");
            RimThreadedHarmony.TranspileMethodLock(original, "CheckRecalculateSocialThoughts");
            RimThreadedHarmony.TranspileMethodLock(original, "RemoveExpiredThoughtsFromCache");
        }
    }
}
