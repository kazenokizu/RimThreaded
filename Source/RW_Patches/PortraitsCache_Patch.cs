using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using static HarmonyLib.AccessTools;

namespace RimThreaded.RW_Patches
{
    // PortraitsCache keeps one Dictionary<PortraitParams, Dictionary<Pawn, CachedPortrait>>. The colonist bar reads and rewrites it every
    // frame (Get: Remove + Add), while SetDirty runs on worker threads whenever a pawn's look changes (apparel, hediffs). Concurrent
    // Remove/Add corrupted a dictionary (1.7 Player.log run 12: IndexOutOfRangeException in Dictionary.TryInsert under
    // ColonistBarColonistDrawer.DrawColonist, every frame afterwards). The public entry points now share one lock; the private
    // helpers are only called from them. Worker threads only ever wait for a frame's portrait work, never the other way round.
    public class PortraitsCache_Patch
    {
        static readonly object cacheLock = new object();

        internal static void RunDestructivePatches()
        {
            Type original = typeof(PortraitsCache);
            HarmonyMethod enter = new HarmonyMethod(Method(typeof(PortraitsCache_Patch), nameof(Enter)), Priority.First);
            HarmonyMethod exit = new HarmonyMethod(Method(typeof(PortraitsCache_Patch), nameof(Exit)), Priority.Last);
            RimThreadedHarmony.nonDestructivePrefixes.Add(enter.method);
            foreach (string name in new[] { "Get", "SetDirty", "PortraitsCacheUpdate", "Clear" })
            {
                MethodInfo target = Method(original, name);
                if (target == null)
                {
                    Log.Error("RimThreaded PATCH FAILED [PortraitsCache." + name + "]: method not found");
                    continue;
                }
                RimThreadedHarmony.harmony.Patch(target, prefix: enter, finalizer: exit);
            }
        }

        public static void Enter(out bool __state)
        {
            __state = false;
            Monitor.Enter(cacheLock, ref __state);
        }

        public static void Exit(bool __state)
        {
            if (__state) Monitor.Exit(cacheLock);
        }
    }
}
