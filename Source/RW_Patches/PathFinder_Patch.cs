using System;
using Verse;
using Verse.AI;
using static RimThreaded.RimThreaded;
using static System.Threading.Thread;

namespace RimThreaded.RW_Patches
{
    // RimWorld 1.6 rewrote pathfinding on top of Unity's job system (PathFinder.PathFinderTick schedules jobs,
    // pawns post PathRequests and poll them). The 1.4 PathFinder/PawnPathPool thread-static patches no longer apply.
    // What still has to be made safe for worker threads:
    //  * PushRequest mutates PathFinder.workQueue from every pawn that starts a path        -> lock
    //  * PawnPathPool.GetPath/ReturnPath use one Stack<PawnPath> per map                   -> lock
    //  * FindPathNow (synchronous) schedules/runs Unity jobs and uses shared batch state    -> run on the main thread
    public class PathFinder_Patch
    {
        static readonly Func<object[], object> FuncFindPathNow = p =>
            ((PathFinder)p[0]).FindPathNow((IntVec3)p[1], (LocalTargetInfo)p[2], (TraverseParms)p[3],
                (PathFinderCostTuning?)p[4], (PathEndMode)p[5], (PathRequest.IPathGridCustomizer)p[6]);

        public static void RunNonDestructivePatches()
        {
            Type original = typeof(PathFinder);
            RimThreadedHarmony.TranspileMethodLock(original, nameof(PathFinder.PushRequest));
            Type poolType = typeof(PawnPathPool);
            RimThreadedHarmony.TranspileMethodLock(poolType, nameof(PawnPathPool.GetPath));
            RimThreadedHarmony.TranspileMethodLock(poolType, nameof(PawnPathPool.ReturnPath));
        }

        public static void RunDestructivePatches()
        {
            Type original = typeof(PathFinder);
            Type patched = typeof(PathFinder_Patch);
            RimThreadedHarmony.Prefix(original, patched, nameof(PathFinder.FindPathNow),
                new Type[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(TraverseParms), typeof(PathFinderCostTuning?), typeof(PathEndMode), typeof(PathRequest.IPathGridCustomizer) },
                false);
        }

        public static bool FindPathNow(PathFinder __instance, ref PawnPath __result, IntVec3 start, LocalTargetInfo target,
            TraverseParms traverseParms, PathFinderCostTuning? tuning, PathEndMode peMode, PathRequest.IPathGridCustomizer customizer)
        {
            if (!CurrentThread.IsBackground || !allWorkerThreads.TryGetValue(CurrentThread, out ThreadInfo threadInfo))
                return true;
            threadInfo.safeFunctionRequest = new object[] { FuncFindPathNow, new object[] { __instance, start, target, traverseParms, tuning, peMode, customizer } };
            mainThreadWaitHandle.Set();
            threadInfo.eventWaitStart.WaitOne();
            __result = (PawnPath)threadInfo.safeFunctionResult;
            return false;
        }
    }
}
