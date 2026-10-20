using System;
using Verse;
using static RimThreaded.RimThreaded;
using static System.Threading.Thread;

namespace RimThreaded.RW_Patches
{
    // ContentFinder<T>.Get refuses to load from any thread but the main one ("Tried to get a resource ... from a different thread",
    // then "MatFrom with null sourceTex"). On 1.7 the old prefix on ContentFinder<Texture2D>.Get (ContentFinder_Texture2D_Patch)
    // has no effect, like every patch on a generic class instantiation (1.7 Player.log run 13: thought and speech bubble textures
    // requested from worker threads, ~2,000 errors). Every call site ContentFinder<X>.Get in the game is redirected here
    // (RimThreadedHarmony.replaceGenericTypes); a RimThreaded worker hands the load to the main thread, any other thread runs vanilla.
    public static class ContentFinder_Patch<T> where T : class
    {
        static readonly Func<object[], object> FuncGet = p => ContentFinder<T>.Get((string)p[0], (bool)p[1]);

        public static T Get(string itemPath, bool reportFailure)
        {
            if (!CurrentThread.IsBackground || !allWorkerThreads.TryGetValue(CurrentThread, out ThreadInfo threadInfo))
                return ContentFinder<T>.Get(itemPath, reportFailure);
            threadInfo.safeFunctionRequest = new object[] { FuncGet, new object[] { itemPath, reportFailure } };
            mainThreadWaitHandle.Set();
            threadInfo.eventWaitStart.WaitOne();
            return (T)threadInfo.safeFunctionResult;
        }
    }
}
