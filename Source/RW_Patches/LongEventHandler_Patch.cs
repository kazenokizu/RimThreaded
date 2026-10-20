using System;
using Verse;
using System.Collections.Concurrent;

namespace RimThreaded.RW_Patches
{

    public class LongEventHandler_Patch
    {

        public static ConcurrentQueue<Action> toExecuteWhenFinished2 = new ConcurrentQueue<Action>();

        internal static void RunDestructivePatches()
        {
            Type original = typeof(LongEventHandler);
            Type patched = typeof(LongEventHandler_Patch);
            RimThreadedHarmony.Prefix(original, patched, "ExecuteToExecuteWhenFinished");
            RimThreadedHarmony.Prefix(original, patched, "ExecuteWhenFinished");
        }
        public static void RunNonDestructivePatches()
        {
            Type original = typeof(LongEventHandler);
            Type patched = typeof(LongEventHandler_Patch);
            RimThreadedHarmony.Prefix(original, patched, "RunEventFromAnotherThread", null, false);
        }

        // 1.6/1.7 port. The 1.4 version kept every action in its own queue AND let vanilla add it to toExecuteWhenFinished, relying on its
        // ExecuteToExecuteWhenFinished prefix to drain the mod queue and clear the vanilla list. On 1.7 vanilla's unpatched body also ran
        // (LongEventHandler.UpdateCurrentAsynchronousEvent -> ExecuteToExecuteWhenFinished without the prefix), so every action ran twice:
        // in the test run a queued GameDataSaveLoader.LoadGame executed twice and the second load failed ("Error while loading a map").
        // Now each action has exactly one home: actions from RimThreaded worker threads go to the mod queue (the reason this patch exists:
        // the vanilla list is not thread-safe), everything else stays vanilla. The mod queue is drained on the main thread by the
        // ExecuteToExecuteWhenFinished prefix (when it runs) and after every threaded tick (RimThreaded.MainThreadWaitLoop).
        public static bool ExecuteToExecuteWhenFinished()
        {
            DrainWorkerActions();
            return true;
        }

        internal static void DrainWorkerActions()
        {
            while (toExecuteWhenFinished2.TryDequeue(out Action action))
            {
                try
                {
                    action();
                }
                catch (Exception arg)
                {
                    Log.Error("Could not execute post-long-event action. Exception: " + arg);
                }
            }
        }

        public static bool ExecuteWhenFinished(Action action)
        {
            if (System.Threading.Thread.CurrentThread.IsBackground && RimThreaded.allWorkerThreads.ContainsKey(System.Threading.Thread.CurrentThread))
            {
                toExecuteWhenFinished2.Enqueue(action);
                return false;
            }
            return true;
        }

        public static bool RunEventFromAnotherThread(Action action)
        {
            RimThreaded.InitializeAllThreadStatics();
            return true;
        }

    }



}
