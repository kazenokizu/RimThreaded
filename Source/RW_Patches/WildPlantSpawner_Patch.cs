using System;
using System.Threading;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimThreaded.RW_Patches
{
    // Multithreaded WildPlantSpawner tick. The main thread (Map.MapPostTick -> WildPlantSpawnerTick) only queues
    // one batch per map; worker threads then process the batch cell by cell (WildPlantSpawnerListTick).
    //
    // 1.6 port + fixes relative to the 1.4 version:
    //  * cell range, wrap-around and tmp/calculated hand-over now mirror vanilla 1.6 WildPlantSpawnerTickInternal exactly
    //  * fertility comes from map.fertilityGrid, the chance from OverrideDensityForFertilityCurve / CachedChanceFromDensity,
    //    the regrow time from map.BiomeAt(cell), as in 1.6
    //  * the 1.4 code accumulated into a *copy* of the work struct and cast each 0..1 cell value to int before scaling,
    //    so the whole-map desired plant count was wrong; sums are now fixed-point longs accumulated in the shared array element
    //  * the batch is finalized by the worker that completes the last ticket of that map, not the one that took it
    public class WildPlantSpawner_Patch
    {
        static readonly Type original = typeof(WildPlantSpawner);
        static readonly Type patched = typeof(WildPlantSpawner_Patch);
        private const double FixedPoint = 1000000.0;

        internal static void RunDestructivePatches()
        {
            RimThreadedHarmony.Prefix(original, patched, "WildPlantSpawnerTickInternal");
        }

        public static bool WildPlantSpawnerTickInternal(WildPlantSpawner __instance)
        {
            if (DebugSettings.fastEcology || DebugSettings.fastEcologyRegrowRateOnly)
            {
                return true; // vanilla loops 2000 times; keep it single threaded
            }
            Map map = __instance.map;
            int area = map.Area;
            int num = Mathf.CeilToInt(area * 0.0001f);
            float densityFactor = __instance.CurrentPlantDensityFactor;
            // vanilla CacheWholeMapNumDesiredPlants
            if (!__instance.hasWholeMapNumDesiredPlantsCalculated)
            {
                __instance.calculatedWholeMapNumDesiredPlants = __instance.CurrentWholeMapNumDesiredPlants;
                __instance.calculatedWholeMapNumNonZeroFertilityCells = __instance.CurrentWholeMapNumNonZeroFertilityCells;
                __instance.hasWholeMapNumDesiredPlantsCalculated = true;
            }
            // warm every lazily built cache here on the main thread so workers only read them
            _ = __instance.HaveAnyPlantsWhichIgnoreFertility;
            SimpleCurve fertilityCurve = __instance.OverrideDensityForFertilityCurve;
            float chanceFromDensity = __instance.CachedChanceFromDensity;
            _ = __instance.AllWildPlants;
            _ = __instance.MutatorWildPlants;
            __instance.CachePlantCommonalitiesIfShould();
            _ = __instance.PlantsCommonalitiesSum;
            _ = __instance.CavePlantsCommonalitiesSum;
            map.cellsInRandomOrder.Get(0); // creates the list if needed

            lock (queueLock)
            {
                int index = wildPlantSpawnerCount;
                if (index >= wildPlantSpawners.Length)
                {
                    Log.ErrorOnce("RimThreaded: too many WildPlantSpawner batches in one tick", 80311);
                    return true;
                }
                if (__instance.cycleIndex >= area + num)
                {
                    __instance.cycleIndex = 0; // corrupt save value, vanilla would reset on the first iteration
                }
                WildPlantSpawnerStructure[] arr = wildPlantSpawners;
                arr[index].spawner = __instance;
                arr[index].map = map;
                arr[index].randomCells = map.cellsInRandomOrder;
                arr[index].area = area;
                arr[index].cycleStart = __instance.cycleIndex;
                arr[index].ticketCount = num;
                arr[index].completedTickets = 0;
                arr[index].densityFactor = densityFactor;
                arr[index].desiredPlants = __instance.calculatedWholeMapNumDesiredPlants;
                arr[index].fertilityCurve = fertilityCurve;
                arr[index].chanceFromDensity = chanceFromDensity;
                arr[index].desiredBeforeWrap = 0;
                arr[index].desiredAfterWrap = 0;
                arr[index].fertileBeforeWrap = 0;
                arr[index].fertileAfterWrap = 0;
                wildPlantSpawnerTicksCount += num;
                arr[index].ticks = wildPlantSpawnerTicksCount;
                wildPlantSpawnerCount = index + 1;
            }
            return false;
        }

        private static readonly object queueLock = new object();
        public static int wildPlantSpawnerCount = 0;
        public static int wildPlantSpawnerTicksCompleted = 0;
        public static int wildPlantSpawnerTicksCount = 0;

        public struct WildPlantSpawnerStructure
        {
            public int ticks; // cumulative ticket count including this batch
            public int ticketCount;
            public int completedTickets;
            public int cycleStart;
            public int area;
            public Map map;
            public MapCellsInRandomOrder randomCells;
            public WildPlantSpawner spawner;
            public float densityFactor;
            public float desiredPlants;
            public SimpleCurve fertilityCurve;
            public float chanceFromDensity;
            public long desiredBeforeWrap;
            public long desiredAfterWrap;
            public int fertileBeforeWrap;
            public int fertileAfterWrap;
        }
        public static WildPlantSpawnerStructure[] wildPlantSpawners = new WildPlantSpawnerStructure[9999];

        public static void WildPlantSpawnerListTick()
        {
            while (true)
            {
                int ticketIndex = Interlocked.Increment(ref wildPlantSpawnerTicksCompleted) - 1;
                if (ticketIndex >= wildPlantSpawnerTicksCount) return;
                int batch = 0;
                while (ticketIndex >= wildPlantSpawners[batch].ticks)
                {
                    batch++;
                }
                ref WildPlantSpawnerStructure work = ref wildPlantSpawners[batch];
                int k = ticketIndex - (work.ticks - work.ticketCount);
                try
                {
                    ProcessCell(ref work, k);
                }
                catch (Exception ex)
                {
                    Log.Error("Exception ticking WildPlantSpawner: " + ex);
                }
                finally
                {
                    if (Interlocked.Increment(ref work.completedTickets) == work.ticketCount)
                    {
                        try
                        {
                            Finish(ref work);
                        }
                        catch (Exception ex)
                        {
                            Log.Error("Exception finishing WildPlantSpawner batch: " + ex);
                        }
                    }
                }
            }
        }

        private static void ProcessCell(ref WildPlantSpawnerStructure work, int k)
        {
            WildPlantSpawner spawner = work.spawner;
            Map map = work.map;
            int position = work.cycleStart + k;
            bool wrapped = position >= work.area;
            IntVec3 cell = work.randomCells.Get(wrapped ? (position - work.area) % work.area : position);
            long desired = (long)(spawner.GetDesiredPlantsCountAt(cell, work.densityFactor) * FixedPoint);
            float fertility = map.fertilityGrid.FertilityAt(cell);
            if (wrapped)
            {
                Interlocked.Add(ref work.desiredAfterWrap, desired);
                if (fertility > 0f) Interlocked.Increment(ref work.fertileAfterWrap);
            }
            else
            {
                Interlocked.Add(ref work.desiredBeforeWrap, desired);
                if (fertility > 0f) Interlocked.Increment(ref work.fertileBeforeWrap);
            }
            float mtb = spawner.GoodRoofForCavePlant(cell) ? 130f : map.BiomeAt(cell).wildPlantRegrowDays;
            float curveChance = work.fertilityCurve.Evaluate(fertility);
            if (Rand.Chance(curveChance > 0f ? curveChance : work.chanceFromDensity) && Rand.MTBEventOccurs(mtb, 60000f, 10000) && spawner.CanRegrowAt(cell))
            {
                spawner.CheckSpawnWildPlantAt(cell, work.densityFactor, work.desiredPlants);
            }
        }

        // Equivalent of the bookkeeping vanilla does inside its loop, applied once all cells of the batch are done.
        private static void Finish(ref WildPlantSpawnerStructure work)
        {
            WildPlantSpawner spawner = work.spawner;
            float before = (float)(work.desiredBeforeWrap / FixedPoint);
            float after = (float)(work.desiredAfterWrap / FixedPoint);
            if (work.cycleStart + work.ticketCount > work.area)
            {
                // vanilla: when cycleIndex >= area -> calculated = tmp (including cells before the wrap); tmp restarts
                spawner.calculatedWholeMapNumDesiredPlants = spawner.calculatedWholeMapNumDesiredPlantsTmp + before;
                spawner.calculatedWholeMapNumNonZeroFertilityCells = spawner.calculatedWholeMapNumNonZeroFertilityCellsTmp + work.fertileBeforeWrap;
                spawner.calculatedWholeMapNumDesiredPlantsTmp = after;
                spawner.calculatedWholeMapNumNonZeroFertilityCellsTmp = work.fertileAfterWrap;
                spawner.cycleIndex = work.cycleStart + work.ticketCount - work.area;
            }
            else
            {
                spawner.calculatedWholeMapNumDesiredPlantsTmp += before;
                spawner.calculatedWholeMapNumNonZeroFertilityCellsTmp += work.fertileBeforeWrap;
                spawner.cycleIndex = work.cycleStart + work.ticketCount;
            }
            work.spawner = null;
            work.map = null;
            work.randomCells = null;
            work.fertilityCurve = null;
        }
    }
}
