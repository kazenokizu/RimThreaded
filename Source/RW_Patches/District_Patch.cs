using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;
using static HarmonyLib.AccessTools;

namespace RimThreaded.RW_Patches
{
    // One monitor for all Room and District state. 1.4 locked each Room and each District separately, which could deadlock:
    // District.Notify_RoofChanged (district lock) -> Room.Notify_RoofChanged (room lock), while Room.UpdateRoomStatsAndRole (room lock) ->
    // stat workers -> District.OpenRoofCountStopAt (district lock). A single re-entrant monitor has no lock order to invert.
    public static class RoomDistrictLock
    {
        public static readonly object Monitor = new object();

        public static void Patch(Type type, string name, Type[] args = null)
        {
            MethodInfo target = args == null ? Method(type, name) : Method(type, name, args);
            if (target == null)
            {
                Log.Error("RimThreaded PATCH FAILED [RoomDistrictLock] " + type.Name + "." + name + ": method not found");
                return;
            }
            try
            {
                MethodInfo enter = Method(typeof(RoomDistrictLock), nameof(Enter));
                RimThreadedHarmony.nonDestructivePrefixes.Add(enter);
                RimThreadedHarmony.harmony.Patch(target, prefix: new HarmonyMethod(enter, 1000),
                    finalizer: new HarmonyMethod(Method(typeof(RoomDistrictLock), nameof(Exit)), -1000));
            }
            catch (Exception e)
            {
                Log.Error("RimThreaded PATCH FAILED [RoomDistrictLock] " + type.Name + "." + name + ": " + e.GetType().Name + ": " + e.Message);
            }
        }

        public static void Enter(out bool __state)
        {
            System.Threading.Monitor.Enter(Monitor);
            __state = true;
        }

        public static void Exit(bool __state)
        {
            if (__state) System.Threading.Monitor.Exit(Monitor);
        }
    }

    // 1.6 port: the 1.4 District copies lacked the 1.6 vacuum caches (cachedExposedCount/cachedExposedState), the enumerator Dispose() calls,
    // Map?. null-safety and AnimalPenConnectedDistrictsCalculator.InvalidateDistrictCache (commented out in the copy). Vanilla now runs
    // serialized on RoomDistrictLock. The old copy is in removed_1.6/.
    class District_Patch
    {
        internal static void RunDestructivePatches()
        {
            Type original = typeof(District);
            RoomDistrictLock.Patch(original, nameof(District.AddRegion));
            RoomDistrictLock.Patch(original, nameof(District.RemoveRegion));
            RoomDistrictLock.Patch(original, nameof(District.Notify_RoofChanged));
            RoomDistrictLock.Patch(original, nameof(District.Notify_TerrainChanged));
            RoomDistrictLock.Patch(original, nameof(District.Notify_RoomShapeOrContainedBedsChanged));
            RoomDistrictLock.Patch(original, nameof(District.OpenRoofCountStopAt));
            RoomDistrictLock.Patch(original, nameof(District.ExposedVacuumCount));
        }
    }
}
