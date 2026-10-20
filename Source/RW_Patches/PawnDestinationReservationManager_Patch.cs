using System;
using System.Reflection;
using Verse;

namespace RimThreaded.RW_Patches
{
    // 1.6 port. The 1.4 patch was partly copy-on-write: Reserve and GetPawnDestinationSetFor still mutated in place, while the unpatched vanilla
    // IsReserved foreach-enumerates reservedDestinations (throws if a faction key is added concurrently) and the readers were unlocked.
    // Now vanilla logic, every declared instance method serialized on the manager. The old patch is in removed_1.6/.
    class PawnDestinationReservationManager_Patch
    {
        internal static void RunDestructivePatches()
        {
            Type original = typeof(PawnDestinationReservationManager);
            foreach (MethodInfo m in original.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (m.IsAbstract || m.IsGenericMethodDefinition || m.GetMethodBody() == null) continue;
                Type[] args = Array.ConvertAll(m.GetParameters(), p => p.ParameterType);
                RimThreadedHarmony.TranspileMethodLock(original, m.Name, args);
            }
        }
    }
}
