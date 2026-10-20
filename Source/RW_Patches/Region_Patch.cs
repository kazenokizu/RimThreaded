using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Verse;
using static HarmonyLib.AccessTools;

namespace RimThreaded.RW_Patches
{

    public class Region_Patch
    {

        // Per-thread closedIndex arrays for RegionTraverser (Region.closedIndex is a field replacement). A ConditionalWeakTable, so regions
        // discarded by a rebuild are collected; the 1.4 Dictionary kept every region each worker ever traversed.
        [ThreadStatic] public static ConditionalWeakTable<Region, uint[]> regionClosedIndex;
        public static object cachedSafeTemperatureRange = new object();

        // 1.6 port: the 1.4 per-region cell sets (Region.Cells / CellCount / District replacements fed by RegionGrid.SetRegionAt) are gone:
        // the global dictionary was mutated from any thread without a lock and kept every region ever created. Vanilla Cells/CellCount scan the
        // grid without rebuilding. RandomCell/AnyCell keep vanilla logic but read the grid without the DirectGrid rebuild trigger, so a worker
        // never starts a region rebuild mid-tick. The old copy is in removed_1.6/.
        internal static void RunDestructivePatches()
        {
            Type original = typeof(Region);
            Type patched = typeof(Region_Patch);
            RimThreadedHarmony.Prefix(original, patched, nameof(DangerFor));
            RimThreadedHarmony.Prefix(original, patched, nameof(get_RandomCell));
            RimThreadedHarmony.Prefix(original, patched, nameof(get_AnyCell));
        }

        public static bool get_RandomCell(Region __instance, ref IntVec3 __result)
        {
            RegionGrid grid = __instance.Map.regionGrid;
            for (int i = 0; i < 1000; i++)
            {
                IntVec3 randomCell = __instance.extentsClose.RandomCell;
                if (grid.GetRegionAt_NoRebuild_InvalidAllowed(randomCell) == __instance)
                {
                    __result = randomCell;
                    return false;
                }
            }
            __result = __instance.AnyCell;
            return false;
        }

        public static bool get_AnyCell(Region __instance, ref IntVec3 __result)
        {
            RegionGrid grid = __instance.Map.regionGrid;
            foreach (IntVec3 item in __instance.extentsClose)
            {
                if (grid.GetRegionAt_NoRebuild_InvalidAllowed(item) == __instance)
                {
                    __result = item;
                    return false;
                }
            }
            Log.Error("Couldn't find any cell in region " + __instance.ToString());
            __result = __instance.extentsClose.RandomCell;
            return false;
        }

        internal static void InitializeThreadStatics()
        {
            regionClosedIndex = new ConditionalWeakTable<Region, uint[]>();
        }

        static ConditionalWeakTable<Region, uint[]> ClosedIndexTable => regionClosedIndex ?? (regionClosedIndex = new ConditionalWeakTable<Region, uint[]>());

        public static uint[] GetRegionClosedIndex(Region region)
        {
            return ClosedIndexTable.GetValue(region, r => new uint[8]);
        }

        public static void SetRegionClosedIndex(Region region, uint[] value)
        {
            ConditionalWeakTable<Region, uint[]> table = ClosedIndexTable;
            table.Remove(region);
            table.Add(region, value);
        }

        internal static void AddFieldReplacements()
        {
            Dictionary<OpCode, MethodInfo> regionTraverserReplacements = new Dictionary<OpCode, MethodInfo>();
            regionTraverserReplacements.Add(OpCodes.Ldfld, Method(typeof(Region_Patch), nameof(GetRegionClosedIndex)));
            regionTraverserReplacements.Add(OpCodes.Stfld, Method(typeof(Region_Patch), nameof(SetRegionClosedIndex)));
            RimThreadedHarmony.replaceFields.Add(Field(typeof(Region), nameof(Region.closedIndex)), regionTraverserReplacements);
        }

        private static readonly Dictionary<Pawn, FloatRange> safeTempRanges = new Dictionary<Pawn, FloatRange>();
        private static int safeTempRangesForTick = -1;

        // 1.6: cachedDangers / cachedSafeTemperatureRanges are readonly, so they are cleared and filled in place under locks.
        public static bool DangerFor(Region __instance, ref Danger __result, Pawn p)
        {
            bool playing = Current.ProgramState == ProgramState.Playing;
            int tick = GenTicks.TicksGame;
            if (playing)
            {
                lock (__instance)
                {
                    if (__instance.cachedDangersForTick != tick)
                    {
                        __instance.cachedDangers.Clear();
                        __instance.cachedDangersForTick = tick;
                    }
                    else if (__instance.cachedDangers.TryGetValue(p, out Danger cached))
                    {
                        __result = cached;
                        return false;
                    }
                }
            }
            Room room = __instance.Room;
            if (room == null || __instance.District == null)
            {
                __result = Danger.Unspecified;
                return false;
            }
            float temperature = room.Temperature;
            FloatRange range;
            if (playing)
            {
                lock (cachedSafeTemperatureRange)
                {
                    if (safeTempRangesForTick != tick)
                    {
                        safeTempRanges.Clear();
                        safeTempRangesForTick = tick;
                    }
                    if (!safeTempRanges.TryGetValue(p, out range))
                    {
                        range = p.SafeTemperatureRange();
                        safeTempRanges[p] = range;
                    }
                }
            }
            else
            {
                range = p.SafeTemperatureRange();
            }
            Danger danger = range.Includes(temperature) ? Danger.None : (range.ExpandedBy(80f).Includes(temperature) ? Danger.Some : Danger.Deadly);
            if (room.Vacuum > 0.5f && p.ConcernedByVacuum)
            {
                danger = Danger.Deadly;
            }
            if (playing)
            {
                lock (__instance)
                {
                    __instance.cachedDangers[p] = danger;
                }
            }
            __result = danger;
            return false;
        }

    }
}
