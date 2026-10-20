using System;
using System.Collections.Generic;
using System.Threading;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimThreaded.RW_Patches
{

    public class FactionManager_Patch
    {
        internal static void RunDestructivePatches()
        {
            Type original = typeof(FactionManager);
            Type patched = typeof(FactionManager_Patch);
            RimThreadedHarmony.Prefix(original, patched, nameof(FactionManagerTick));
        }

        public static bool FactionManagerTick(FactionManager __instance)
        {
            // 1.6 vanilla order: goodwill situations, settlement proximity, faction ticks (here: on workers), removals.
            // The 1.4 copy never called GoodwillManagerTick, so periodic goodwill-situation recalculation stopped.
            __instance.goodwillSituationManager.GoodwillManagerTick();
            SettlementProximityGoodwillUtility.CheckSettlementProximityGoodwillChange();

            lock (__instance)
            {
                List<Faction> newList = __instance.toRemove;
                for (int num = newList.Count - 1; num >= 0; num--)
                {
                    Faction faction = newList[num];
                    newList.Remove(faction);
                    __instance.toRemove = newList;
                    __instance.Remove(faction);
                }
            }
            allFactionsTickList = new List<Faction>(__instance.allFactions); // snapshot: factions can be added while workers tick
            allFactionsTicks = allFactionsTickList.Count;
            return false;
        }
        public static List<Faction> allFactionsTickList;
        public static int allFactionsTicks;

        public static void FactionsPrepare()
        {
            try
            {
                World world = Find.World;
                world.factionManager.FactionManagerTick();
            }
            catch (Exception ex3)
            {
                Log.Error(ex3.ToString());
            }
        }

        public static void FactionsListTick()
        {
            while (true)
            {
                int index = Interlocked.Decrement(ref allFactionsTicks);
                if (index < 0) return;
                Faction faction = allFactionsTickList[index];
                try
                {
                    faction.FactionTick();
                }
                catch (Exception ex)
                {
                    Log.Error("Exception ticking faction: " + faction.ToStringSafe() + ": " + ex);
                }
            }
        }
    }
}
