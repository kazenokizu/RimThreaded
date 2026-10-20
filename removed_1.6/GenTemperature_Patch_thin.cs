using System;
using Verse;

namespace RimThreaded.RW_Patches
{
    // 1.6: the 1.4 season/amplitude caches were keyed by tile id only, but tiles are now PlanetTile (layer + id), and the vanilla
    // temperature functions are pure reads, so they are left alone. Only the thread-static scratch array remains.
    public class GenTemperature_Patch
    {
        [ThreadStatic] public static Room[] beqRooms;

        public static void InitializeThreadStatics()
        {
            beqRooms = new Room[4];
        }
    }
}
