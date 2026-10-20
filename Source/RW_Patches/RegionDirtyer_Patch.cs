using System;
using Verse;

namespace RimThreaded.RW_Patches
{
    // 1.6 port. The 1.4 patch replaced every RegionDirtyer method and kept dirty cells in its own ConcurrentQueue per map. In 1.6 that hides the
    // dirty state from vanilla readers (RegionAndRoomUpdater.AnythingToRebuild / TryRebuildDirtyRegionsAndRooms read RegionDirtyer.AnyDirty and
    // DirtyCells), and the copies lacked 1.6 logic: Region.ListerThings.Clear(), VacuumComponent.SetDrawerDirty(), the newWalkability argument,
    // the TryCacheRegionTempInfo(c2, ...) fix and the merged DirtyRegionForThing path.
    // Now: vanilla bodies and vanilla storage, each method serialized on the RegionDirtyer instance. RegionAndRoomUpdater_Patch holds the same
    // monitor for the whole rebuild, so DirtyCells is never modified while the rebuild iterates or clears it. The old copy is in removed_1.6/.
    public class RegionDirtyer_Patch
    {
        internal static void InitializeThreadStatics() { }

        public static void RunDestructivePatches()
        {
            Type original = typeof(RegionDirtyer);
            RimThreadedHarmony.TranspileMethodLock(original, "Notify_WalkabilityChanged", new[] { typeof(IntVec3), typeof(bool) });
            RimThreadedHarmony.TranspileMethodLock(original, "Notify_ThingAffectingRegionsSpawned", new[] { typeof(Thing) });
            RimThreadedHarmony.TranspileMethodLock(original, "Notify_ThingAffectingRegionsDespawned", new[] { typeof(Thing) });
            RimThreadedHarmony.TranspileMethodLock(original, "DirtyRegionForThing", new[] { typeof(Thing) });
            RimThreadedHarmony.TranspileMethodLock(original, "SetAllClean", Type.EmptyTypes);
            RimThreadedHarmony.TranspileMethodLock(original, "SetRegionDirty", new[] { typeof(Region), typeof(bool) });
            RimThreadedHarmony.TranspileMethodLock(original, "SetAllDirty", Type.EmptyTypes);
        }
    }
}
