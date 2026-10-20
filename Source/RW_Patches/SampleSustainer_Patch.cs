using System;
using UnityEngine;
using Verse.Sound;
using static RimThreaded.RimThreaded;
using static System.Threading.Thread;

namespace RimThreaded.RW_Patches
{

    public class SampleSustainer_Patch
    {

        static readonly Func<object[], object> safeFunction = parameters =>
            SampleSustainer.TryMakeAndPlay(
                (SubSustainer)parameters[0],
                (AudioClip)parameters[1],
                (float)parameters[2],
                (float)parameters[3]);

        public static void RunDestructivePatches()
        {
            Type original = typeof(SampleSustainer);
            Type patched = typeof(SampleSustainer_Patch);
            RimThreadedHarmony.Prefix(original, patched, "TryMakeAndPlay");
        }

        // 1.6 added startTime (source.time is set to it before Play); the 1.4 copy dropped it, so dispatched sounds always restarted at 0.
        public static bool TryMakeAndPlay(ref SampleSustainer __result, SubSustainer subSus, AudioClip clip, float scheduledEndTime, float startTime = 0f)
        {
            if (!CurrentThread.IsBackground || !allWorkerThreads.TryGetValue(CurrentThread, out ThreadInfo threadInfo))
                return true;
            threadInfo.safeFunctionRequest = new object[] { safeFunction, new object[] { subSus, clip, scheduledEndTime, startTime } };
            mainThreadWaitHandle.Set();
            threadInfo.eventWaitStart.WaitOne();
            __result = (SampleSustainer)threadInfo.safeFunctionResult;
            return false;
        }


    }
}
