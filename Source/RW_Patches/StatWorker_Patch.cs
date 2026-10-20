using RimWorld;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Verse;
using static UnityEngine.Random;

namespace RimThreaded.RW_Patches
{
    public class StatWorker_Patch
    {
        internal static void RunDestructivePatches()
        {
            Type original = typeof(StatWorker);
            Type patched = typeof(StatWorker_Patch);
            RimThreadedHarmony.Prefix(original, patched, nameof(GetValue), new Type[] {typeof(Thing), typeof(bool), typeof(int) });
        }
        // 1.6: immutableStatCache is already a ConcurrentDictionary; temporaryStatCache is a plain Dictionary,
        // so every access to it (reads included) is done under a lock. StatRequest evaluation stays outside the lock.
        public static bool GetValue(StatWorker __instance, ref float __result, Thing thing, bool applyPostProcess = true, int cacheStaleAfterTicks = -1)
        {
            if (__instance.stat.immutable)
            {
                ConcurrentDictionary<Thing, float> immutableStatCache = __instance.immutableStatCache;
                if (immutableStatCache.TryGetValue(thing, out __result))
                {
                    return false;
                }
                __result = __instance.GetValue(StatRequest.For(thing), applyPostProcess);
                immutableStatCache[thing] = __result;
                return false;
            }
            Dictionary<Thing, StatCacheEntry> temporaryStatCache = __instance.temporaryStatCache;
            int ticksGame = Find.TickManager.TicksGame;
            if (cacheStaleAfterTicks != -1 && temporaryStatCache != null)
            {
                lock (temporaryStatCache)
                {
                    if (temporaryStatCache.TryGetValue(thing, out StatCacheEntry cached) && ticksGame - cached.gameTick < cacheStaleAfterTicks)
                    {
                        __result = cached.statValue;
                        return false;
                    }
                }
            }
            __result = __instance.GetValue(StatRequest.For(thing));
            if (temporaryStatCache != null)
            {
                lock (temporaryStatCache)
                {
                    temporaryStatCache[thing] = new StatCacheEntry(__result, ticksGame);
                }
            }
            return false;
        }
    }
}
