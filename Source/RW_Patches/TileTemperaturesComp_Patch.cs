using System;
using RimWorld.Planet;

namespace RimThreaded.RW_Patches
{
    // 1.6 keys the temperature cache by PlanetTile (layer + tile id) and stores it in per-layer dictionaries, so the 1.4
    // array based replacement no longer applies. Vanilla 1.6 is kept as is; the lazily filled cache is serialized on the component.
    public class TileTemperaturesComp_Patch
    {
        internal static void RunDestructivePatches()
        {
            Type original = typeof(TileTemperaturesComp);
            RimThreadedHarmony.TranspileMethodLock(original, "RetrieveCachedData");
            RimThreadedHarmony.TranspileMethodLock(original, nameof(TileTemperaturesComp.WorldComponentTick));
            RimThreadedHarmony.TranspileMethodLock(original, nameof(TileTemperaturesComp.ClearCaches));
        }
    }
}
