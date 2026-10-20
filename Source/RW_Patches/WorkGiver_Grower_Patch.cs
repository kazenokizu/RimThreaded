using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using Verse.AI;

namespace RimThreaded.RW_Patches
{
    class WorkGiver_Grower_Patch
    {
        [ThreadStatic] public static ThingDef wantedPlantDef;

        public static Dictionary<Map, List<HashSet<IntVec3>[]>> awaitingPlantCellsMapDict = new Dictionary<Map, List<HashSet<IntVec3>[]>>();

        internal static IntVec3 ClosestLocationReachable(WorkGiver_Grower workGiver_Grower, Pawn pawn)
        {
            Danger maxDanger = pawn.NormalMaxDanger();
            //wantedPlantDef = null;
            //List<Zone> zonesList = pawn.Map.zoneManager.AllZones;
            //for (int j = 0; j < zonesList.Count; j++)
            //{

            //if (growZone.cells.Count == 0)
            //{
            //Log.ErrorOnce("Grow zone has 0 cells: " + growZone, -563487);
            //}
            bool forced = false;
            Map map = pawn.Map;
            ZoneManager zoneManager = pawn.Map.zoneManager;
            foreach (IntVec3 actionableLocation in JumboCell.GetClosestActionableLocations(pawn, map, RimThreaded.plantSowing_Cache))
            {
                List<Thing> thingsAtLocation = actionableLocation.GetThingList(map);
                foreach (Thing thingAtLocation in thingsAtLocation)
                {
                    if (thingAtLocation is Building_PlantGrower building_PlantGrower)
                    {
                        if (building_PlantGrower == null || !workGiver_Grower.ExtraRequirements(building_PlantGrower, pawn)
                            || building_PlantGrower.IsForbidden(pawn)
                            || !pawn.CanReach(building_PlantGrower, PathEndMode.OnCell, maxDanger)
                            //|| building_PlantGrower.IsBurning()
                            )
                        {
                            continue;
                        }

                        //foreach (IntVec3 item in building_PlantGrower.OccupiedRect())
                        //{
                        //return item; //TODO ADD check
                        //}
                        return actionableLocation;
                    }
                }
                if (!(zoneManager.ZoneAt(actionableLocation) is Zone_Growing growZone))
                {
                    continue;
                }
                if (!workGiver_Grower.ExtraRequirements(growZone, pawn))
                {
                    continue;
                }
                if (!JobOnCellTest(workGiver_Grower.def, pawn, actionableLocation, forced))
                {
                    continue;
                }
                //!growZone.ContainsStaticFire && 
                if (!workGiver_Grower.HasJobOnCell(pawn, actionableLocation))
                {
                    continue;
                }
                if (!pawn.CanReach(actionableLocation, PathEndMode.OnCell, maxDanger))
                {
                    continue;
                }
                return actionableLocation;

            }
            //wantedPlantDef = null;
            return IntVec3.Invalid;
        }
        // Boolean twin of 1.6 WorkGiver_GrowerSow.JobOnCell: true when vanilla would produce a job. Same checks, same order.
        // Cells that cannot be worked right now are re-registered in the JumboCell cache as in the 1.4 version.
        private static bool JobOnCellTest(WorkGiverDef def, Pawn pawn, IntVec3 c, bool forced = false)
        {
            Map map = pawn.Map;
            if (c.IsForbidden(pawn))
            {
                JumboCell.ReregisterObject(map, c, RimThreaded.plantSowing_Cache);
                return false;
            }
            if (c.GetVacuum(map) >= 0.5f)
            {
                return false;
            }
            ThingDef localWantedPlantDef = WorkGiver_Grower.CalculateWantedPlantDef(c, map);
            wantedPlantDef = localWantedPlantDef;
            if (localWantedPlantDef == null)
            {
                return false;
            }
            if (!PlantUtility.GrowthSeasonNow(c, map, localWantedPlantDef))
            {
                return false;
            }
            List<Thing> thingList = c.GetThingList(map);
            Zone_Growing zone_Growing = c.GetZone(map) as Zone_Growing;
            bool flag = false;
            for (int i = 0; i < thingList.Count; i++)
            {
                Thing thing = thingList[i];
                if (thing.def == localWantedPlantDef)
                {
                    JumboCell.ReregisterObject(map, c, RimThreaded.plantSowing_Cache);
                    return false;
                }
                if ((thing is Blueprint || thing is Frame) && thing.Faction == pawn.Faction)
                {
                    flag = true;
                }
            }
            if (flag)
            {
                Thing edifice = c.GetEdifice(map);
                if (edifice == null || edifice.def.fertility < 0f)
                {
                    return false;
                }
            }
            if (localWantedPlantDef.plant.diesToLight)
            {
                if (!c.Roofed(map) && !map.GameConditionManager.IsAlwaysDarkOutside)
                {
                    return false;
                }
                if (map.glowGrid.GroundGlowAt(c, ignoreCavePlants: true) > 0f)
                {
                    return false;
                }
            }
            if (localWantedPlantDef.plant.interferesWithRoof && c.Roofed(map))
            {
                return false;
            }
            Plant plant = c.GetPlant(map);
            if (plant != null && plant.def.plant.blockAdjacentSow)
            {
                if (!pawn.CanReserve(plant, 1, -1, null, forced) || plant.IsForbidden(pawn))
                {
                    return false;
                }
                if (zone_Growing != null && !zone_Growing.allowCut)
                {
                    return false;
                }
                if (!forced && plant.TryGetComp<CompPlantPreventCutting>(out CompPlantPreventCutting comp) && comp.PreventCutting)
                {
                    return false;
                }
                return PlantUtility.PawnWillingToCutPlant_Job(plant, pawn);
            }
            Thing thing2 = PlantUtility.AdjacentSowBlocker(localWantedPlantDef, c, map);
            if (thing2 != null)
            {
                if (thing2 is Plant plant2 && pawn.CanReserveAndReach(plant2, PathEndMode.Touch, Danger.Deadly, 1, -1, null, forced) && !plant2.IsForbidden(pawn))
                {
                    IPlantToGrowSettable plantToGrowSettable = plant2.Position.GetPlantToGrowSettable(plant2.Map);
                    if (plantToGrowSettable == null || plantToGrowSettable.GetPlantDefToGrow() != plant2.def)
                    {
                        Zone_Growing zone_Growing2 = c.GetZone(map) as Zone_Growing;
                        Zone_Growing zone_Growing3 = plant2.Position.GetZone(map) as Zone_Growing;
                        if ((zone_Growing2 != null && !zone_Growing2.allowCut) || (zone_Growing3 != null && !zone_Growing3.allowCut && plant2.def == zone_Growing3.GetPlantDefToGrow()))
                        {
                            return false;
                        }
                        if (!forced && thing2.TryGetComp(out CompPlantPreventCutting comp2) && comp2.PreventCutting)
                        {
                            return false;
                        }
                        if (PlantUtility.TreeMarkedForExtraction(plant2))
                        {
                            return false;
                        }
                        return PlantUtility.PawnWillingToCutPlant_Job(plant2, pawn);
                    }
                }
                JumboCell.ReregisterObject(map, c, RimThreaded.plantSowing_Cache);
                return false;
            }
            int sowMinSkill = localWantedPlantDef.plant.sowMinSkill;
            if (sowMinSkill > 0 && ((pawn.skills != null && pawn.skills.GetSkill(SkillDefOf.Plants).Level < sowMinSkill) || (pawn.IsColonyMech && pawn.RaceProps.mechFixedSkillLevel < sowMinSkill)))
            {
                return false;
            }
            for (int j = 0; j < thingList.Count; j++)
            {
                Thing thing3 = thingList[j];
                if (!thing3.def.BlocksPlanting())
                {
                    continue;
                }
                if (!pawn.CanReserve(thing3, 1, -1, null, forced))
                {
                    JumboCell.ReregisterObject(map, c, RimThreaded.plantSowing_Cache);
                    return false;
                }
                if (thing3.def.category == ThingCategory.Plant)
                {
                    if (thing3.IsForbidden(pawn))
                    {
                        JumboCell.ReregisterObject(map, c, RimThreaded.plantSowing_Cache);
                        return false;
                    }
                    if (zone_Growing != null && !zone_Growing.allowCut)
                    {
                        return false;
                    }
                    if (!forced && plant.TryGetComp<CompPlantPreventCutting>(out CompPlantPreventCutting comp3) && comp3.PreventCutting)
                    {
                        return false;
                    }
                    if (!PlantUtility.PawnWillingToCutPlant_Job(thing3, pawn))
                    {
                        return false;
                    }
                    return !PlantUtility.TreeMarkedForExtraction(thing3);
                }
                if (thing3.def.EverHaulable)
                {
                    return true; // vanilla: HaulAIUtility.HaulAsideJobFor(pawn, thing3), which can still return null
                }
                JumboCell.ReregisterObject(map, c, RimThreaded.plantSowing_Cache);
                return false;
            }
            if (!localWantedPlantDef.CanNowPlantAt(c, map) || !PlantUtility.GrowthSeasonNow(c, map, localWantedPlantDef))
            {
                JumboCell.ReregisterObject(map, c, RimThreaded.plantSowing_Cache);
                return false;
            }
            if (!pawn.CanReserve(c, 1, -1, null, forced))
            {
                JumboCell.ReregisterObject(map, c, RimThreaded.plantSowing_Cache);
                return false;
            }
            return true;
        }
    }
}
