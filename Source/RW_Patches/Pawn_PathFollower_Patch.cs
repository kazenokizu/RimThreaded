using System;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimThreaded.RW_Patches
{
    class Pawn_PathFollower_Patch
    {
        internal static void RunDestructivePatches()
        {
            Type original = typeof(Pawn_PathFollower);
            Type patched = typeof(Pawn_PathFollower_Patch);
            RimThreadedHarmony.Prefix(original, patched, nameof(CostToMoveIntoCell), new Type[] { typeof(Pawn), typeof(IntVec3) });
        }

        // 1.6 vanilla body of Pawn_PathFollower.CostToMoveIntoCell(Pawn, IntVec3); only difference is the
        // null-safe access to pawn.jobs.curDriver (the driver can be cleared by another thread between the checks).
        public static bool CostToMoveIntoCell(ref float __result, Pawn pawn, IntVec3 c)
        {
            float num = ((c.x != pawn.Position.x && c.z != pawn.Position.z) ? pawn.TicksPerMoveDiagonal : pawn.TicksPerMoveCardinal);
            int? pawnCellBaseCostOverride = Pawn_PathFollower.GetPawnCellBaseCostOverride(pawn, c);
            num += (float)pawn.Map.pathing.For(pawn).pathGrid.CalculatedCostAt(c, perceivedStatic: false, pawn.Position, pawnCellBaseCostOverride);
            Building edifice = c.GetEdifice(pawn.Map);
            if (edifice != null)
            {
                num += (float)(int)edifice.PathWalkCostFor(pawn);
            }
            TerrainDef terrain = c.GetTerrain(pawn.Map);
            if (terrain.tags != null)
            {
                foreach (string tag in terrain.tags)
                {
                    if (pawn.kindDef.moveSpeedFactorByTerrainTag.TryGetValue(tag, out float value))
                    {
                        num /= value;
                    }
                }
            }
            if (num > 450f)
            {
                num = 450f;
            }
            if (pawn.CurJob != null)
            {
                Pawn locomotionUrgencySameAs = pawn?.jobs?.curDriver?.locomotionUrgencySameAs; //changed
                if (locomotionUrgencySameAs != null && locomotionUrgencySameAs != pawn && locomotionUrgencySameAs.Spawned)
                {
                    float num2 = Pawn_PathFollower.CostToMoveIntoCell(locomotionUrgencySameAs, c);
                    if (num < num2)
                    {
                        num = num2;
                    }
                }
                else
                {
                    switch (pawn.jobs.curJob.locomotionUrgency)
                    {
                        case LocomotionUrgency.Amble:
                            num *= 3f;
                            if (num < 60f)
                            {
                                num = 60f;
                            }
                            break;
                        case LocomotionUrgency.Walk:
                            num *= 2f;
                            if (num < 50f)
                            {
                                num = 50f;
                            }
                            break;
                        case LocomotionUrgency.Jog:
                            num *= 1f;
                            break;
                        case LocomotionUrgency.Sprint:
                            num = Mathf.RoundToInt(num * 0.75f);
                            break;
                    }
                }
            }
            __result = Mathf.Max(num, 1f);
            return false;
        }
    }
}
