using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;
using Verse.AI;
using static HarmonyLib.AccessTools;

namespace RimThreaded.RW_Patches
{
    // 1.6 port: the 1.4 copies of StartJob, DetermineNextJob and TryOpportunisticJob had drifted from 1.6 (StartJob lost the hediff report-string
    // override, curDriver.asleep, flight notification and preToilReservationsCanFail; DetermineNextJob lost the determiningNextJob re-entry guard
    // and ignoreQueue; TryOpportunisticJob was rewritten in 1.6). Vanilla now runs for those three. What stays:
    //  - TryFindAndStartJob: job determination + start is serialized across pawns (reservations are not atomic with job selection).
    //  - Notify_DamageTaken: skipped when the job/driver was cleared by another thread.
    // The old copy is kept in removed_1.6/.
    public class Pawn_JobTracker_Patch
    {
        static readonly object determineNextJobLock = new object();
        [ThreadStatic] static System.Collections.Generic.Stack<bool> acquiredStack;
        static System.Collections.Generic.Stack<bool> acquired => acquiredStack ?? (acquiredStack = new System.Collections.Generic.Stack<bool>());
        static volatile bool warnedTimeout;

        internal static void RunDestructivePatches()
        {
            Type original = typeof(Pawn_JobTracker);
            Type patched = typeof(Pawn_JobTracker_Patch);
            MethodInfo tryFind = Method(original, "TryFindAndStartJob");
            if (tryFind == null)
            {
                Log.Error("RimThreaded PATCH FAILED [Pawn_JobTracker.TryFindAndStartJob]: method not found");
            }
            else
            {
                MethodInfo enter = Method(patched, nameof(TryFindAndStartJobEnter));
                RimThreadedHarmony.nonDestructivePrefixes.Add(enter);
                RimThreadedHarmony.harmony.Patch(tryFind, prefix: new HarmonyMethod(enter, 1000));
                RimThreadedHarmony.harmony.Patch(tryFind, finalizer: new HarmonyMethod(Method(patched, nameof(TryFindAndStartJobExit)), -1000));
            }
            RimThreadedHarmony.Prefix(original, patched, nameof(Notify_DamageTaken), null, false);
        }

        public static bool TryFindAndStartJobEnter(Pawn_JobTracker __instance)
        {
            // Bounded wait: a worker holding this lock may itself be waiting for a main-thread dispatch, so the main thread must never wait forever.
            if (Monitor.TryEnter(determineNextJobLock, RimThreaded.halfTimeoutMS))
            {
                acquired.Push(true);
            }
            else
            {
                acquired.Push(false);
                if (!warnedTimeout)
                {
                    warnedTimeout = true;
                    Log.Warning("RimThreaded: job-finding lock not obtained within " + (int)RimThreaded.halfTimeoutMS.TotalMilliseconds + " ms; continuing without serialization (logged once).");
                }
            }
            return true;
        }

        public static void TryFindAndStartJobExit(Pawn_JobTracker __instance)
        {
            if (acquired.Count > 0 && acquired.Pop())
                Monitor.Exit(determineNextJobLock);
        }

        public static bool Notify_DamageTaken(Pawn_JobTracker __instance, DamageInfo dinfo)
        {
            return __instance.curJob != null && __instance.curDriver != null;
        }
    }
}
