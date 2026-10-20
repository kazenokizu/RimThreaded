using System;

namespace RimThreaded
{
    // Called at the start of every field-replaced method (RimThreadedHarmony.ReplaceFieldsTranspiler). Threads RimThreaded does not
    // create - the .NET thread pool used by vanilla's own Parallel.ForEach (PathFinderMapData.GatherData), loader and engine threads -
    // never ran InitializeAllThreadStatics, so their replacement fields were null (1.7 test run: NullReferenceException in
    // ConnectivitySource.UpdateIncrementally on a pool thread). Kept in its own class without a static constructor so the fast path
    // is one thread-static read and never waits on RimThreaded's type initializer.
    public static class ThreadStaticsGate
    {
        [ThreadStatic] internal static bool ready;

        public static void EnsureThreadStatics()
        {
            if (!ready)
                RimThreaded.InitializeAllThreadStatics();
        }
    }
}
