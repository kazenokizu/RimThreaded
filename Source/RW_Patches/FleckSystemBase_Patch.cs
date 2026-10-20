using System;
using Verse;

namespace RimThreaded.RW_Patches
{
    // 1.6 port: the 1.4 patch re-implemented Tick/Update/CreateFleck/Draw with reader-writer locks around the two data lists.
    // 1.6 changed the draw path (Draw -> ForceDraw + DrawBatch merging) and Tick/Update now share tmpRemoveIndices, so vanilla is kept
    // and every method touching dataRealtime/dataGametime/tmpRemoveIndices is serialized on the fleck system instance instead.
    // (Locks are re-entrant, so a fleck that spawns another fleck from TimeInterval is fine.)
    class FleckSystemBase_Patch
    {
        internal static void RunDestructivePatches()
        {
            LockAll(typeof(FleckSystemBase<FleckStatic>));
            LockAll(typeof(FleckSystemBase<FleckThrown>));
            LockAll(typeof(FleckSystemBase<FleckSplash>));
        }

        private static void LockAll(Type original)
        {
            RimThreadedHarmony.TranspileMethodLock(original, nameof(FleckSystem.Update));
            RimThreadedHarmony.TranspileMethodLock(original, nameof(FleckSystem.Tick));
            RimThreadedHarmony.TranspileMethodLock(original, nameof(FleckSystem.ForceDraw));
            RimThreadedHarmony.TranspileMethodLock(original, nameof(FleckSystem.CreateFleck));
            RimThreadedHarmony.TranspileMethodLock(original, nameof(FleckSystem.MergeWith));
            RimThreadedHarmony.TranspileMethodLock(original, nameof(FleckSystem.RemoveAllFlecks));
        }
    }
}
