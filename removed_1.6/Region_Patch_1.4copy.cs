using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Verse;
using static HarmonyLib.AccessTools;

namespace RimThreaded.RW_Patches
{

    public class Region_Patch
    {

        [ThreadStatic] public static Dictionary<Region, uint[]> regionClosedIndex; //newly defined
        public static object cachedSafeTemperatureRange = new object();
        public static Dictionary<Region, HashSet<IntVec3>> RegionCells = new Dictionary<Region, HashSet<IntVec3>>();

        internal static void RunDestructivePatches()
        {
            Type original = typeof(Region);
            Type patched = typeof(Region_Patch);
            RimThreadedHarmony.Prefix(original, patched, nameof(DangerFor));
            RimThreadedHarmony.Prefix(original, patched, nameof(get_District));
            RimThreadedHarmony.Prefix(original, patched, nameof(get_RandomCell));
            RimThreadedHarmony.Prefix(original, patched, nameof(get_CellCount));
            RimThreadedHarmony.Prefix(original, patched, nameof(get_Cells));
            RimThreadedHarmony.Prefix(original, patched, nameof(get_AnyCell));
        }
        public static bool get_Cells(Region __instance, ref IEnumerable<IntVec3> __result)
        {
            __result = GetRegionCells(__instance);
            return false;
        }
        public static bool get_RandomCell(Region __instance, ref IntVec3 __result)
        {
            __result = GetRegionCells(__instance).RandomElement();
            return false;
        }
        public static bool get_AnyCell(Region __instance, ref IntVec3 __result)
        {
            HashSet<IntVec3> cells = GetRegionCells(__instance);
            if (cells.Count == 0)
            {
                __result = __instance.extentsClose.RandomCell;
                return false;
            }
            __result = GetRegionCells(__instance).RandomElement();
            return false;
        }
        public static HashSet<IntVec3> GetRegionCells(Region region)
        {
            if (!RegionCells.TryGetValue(region, out HashSet<IntVec3> regionCells))
            {
                regionCells = new HashSet<IntVec3>();
                RegionGrid regions = region.Map.regionGrid;
                for (int z = region.extentsClose.minZ; z <= region.extentsClose.maxZ; z++)
                {
                    for (int x = region.extentsClose.minX; x <= region.extentsClose.maxX; x++)
                    {
                        IntVec3 intVec = new IntVec3(x, 0, z);
                        if (regions.GetRegionAt_NoRebuild_InvalidAllowed(intVec) == region)
                        {
                            regionCells.Add(intVec);
                        }
                    }
                }
                RegionCells[region] = regionCells;
            }
            return regionCells;
        }

        internal static void InitializeThreadStatics()
        {
            regionClosedIndex = new Dictionary<Region, uint[]>();
        }
        public static bool get_CellCount(Region __instance, ref int __result)
        {
            __result = GetRegionCells(__instance).Count;
            return false;
            /*
            if (__instance.cachedCellCount == -1)
            {
                __instance.cachedCellCount = 0;
                RegionGrid regionGrid = __instance.Map.regionGrid;
                for (int i = __instance.extentsClose.minZ; i <= __instance.extentsClose.maxZ; i++)
                {
                    for (int j = __instance.extentsClose.minX; j <= __instance.extentsClose.maxX; j++)
                    {
                        IntVec3 c = new IntVec3(j, 0, i);
                        if (regionGrid.GetRegionAt_NoRebuild_InvalidAllowed(c) == __instance)
                        {
                            __instance.cachedCellCount++;
                        }
                    }
                }
                __instance.cachedCellCount = Math.Max(1, __instance.cachedCellCount);
            }
            __result = __instance.cachedCellCount;
            return false;
            */
        }
        /*
        public static bool get_RandomCell(Region __instance, ref IntVec3 __result)
        {
            Map map = __instance.Map;
            CellIndices cellIndices = map.cellIndices;
            Region[] directGrid = map.regionGrid.DirectGrid;
            for (int i = 0; i < 1000; i++)
            {
                IntVec3 randomCell = __instance.extentsClose.RandomCell;
                if (directGrid[cellIndices.CellToIndex(randomCell)] == __instance)
                {
                    __result = randomCell;
                    return false;
                }
            }
            __result = __instance.AnyCell;
            return false;
        }
        */
        public static uint[] GetRegionClosedIndex(Region region)
        {
            if (regionClosedIndex.TryGetValue(region, out uint[] closedIndex)) return closedIndex;
            closedIndex = new uint[8];
            regionClosedIndex[region] = closedIndex;
            return closedIndex;
        }
        public static void SetRegionClosedIndex(Region region, uint[] value)
        {
            regionClosedIndex[region] = value;
            return;
        }

        internal static void AddFieldReplacements()
        {
            Dictionary<OpCode, MethodInfo> regionTraverserReplacements = new Dictionary<OpCode, MethodInfo>();
            regionTraverserReplacements.Add(OpCodes.Ldfld, Method(typeof(Region_Patch), nameof(GetRegionClosedIndex)));
            regionTraverserReplacements.Add(OpCodes.Stfld, Method(typeof(Region_Patch), nameof(SetRegionClosedIndex)));
            RimThreadedHarmony.replaceFields.Add(Field(typeof(Region), nameof(Region.closedIndex)), regionTraverserReplacements);
        }
        public static bool get_District(Region __instance, ref District __result)
        {
            __result = __instance.districtInt;
            return false;
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
