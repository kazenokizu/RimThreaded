using System;
using System.Collections.Generic;
using RimWorld.Planet;
using Verse;

namespace RimThreaded.RW_Patches
{
    class World_Patch
    {
        [ThreadStatic] public static List<ThingDef> tmpNaturalRockDefs = new List<ThingDef>();
        [ThreadStatic] public static List<PlanetTile> tmpNeighbors = new List<PlanetTile>();
        [ThreadStatic] public static List<Rot4> tmpOceanDirs = new List<Rot4>();

        internal static void InitializeThreadStatics()
        {
            tmpNaturalRockDefs = new List<ThingDef>();
            tmpNeighbors = new List<PlanetTile>();
            tmpOceanDirs = new List<Rot4>();
        }

    }
}
