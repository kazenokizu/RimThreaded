using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;
using static HarmonyLib.AccessTools;

namespace RimThreaded.RW_Patches
{
    class Thing_Patch
    {
        internal static void RunDestructivePatches()
        {
            Type original = typeof(Thing);
            Type patched = typeof(Thing_Patch);
            RimThreadedHarmony.Prefix(original, patched, nameof(get_Map));
            RimThreadedHarmony.Transpile(original, patched, nameof(TakeDamage));
        }

        // Vanilla: bool flag = SpawnedOrAnyParentSpawned; Map map = MapHeld; ... if (dinfo.Def.harmsHealth && flag) map.damageWatcher.Notify_DamageTaken(...)
        // If another thread despawns the thing between those two reads, map is null and the NRE aborts the damage. Add "&& map != null".
        public static IEnumerable<CodeInstruction> TakeDamage(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            List<CodeInstruction> list = instructions.ToList();
            MethodInfo getMapHeld = Method(typeof(Thing), "get_MapHeld");
            int mapLocal = -1;
            for (int i = 0; i + 1 < list.Count; i++)
            {
                if (list[i].Calls(getMapHeld) && list[i + 1].IsStloc())
                {
                    mapLocal = i + 1;
                    break;
                }
            }
            bool patched = false;
            for (int i = 0; i < list.Count; i++)
            {
                yield return list[i];
                // ... ldfld harmsHealth; ldloc flag; and; brfalse.s L  => insert: ldloc map; brfalse L
                if (!patched && mapLocal >= 0 && list[i].opcode == OpCodes.And && i + 1 < list.Count && list[i + 1].opcode == OpCodes.Brfalse_S
                    && i >= 2 && list[i - 2].opcode == OpCodes.Ldfld && list[i - 2].operand is FieldInfo f && f.Name == "harmsHealth")
                {
                    yield return list[i + 1];
                    CodeInstruction guard = new CodeInstruction(OpCodes.Ldloc, list[mapLocal].operand);
                    guard = list[mapLocal].opcode == OpCodes.Stloc_0 ? new CodeInstruction(OpCodes.Ldloc_0)
                          : list[mapLocal].opcode == OpCodes.Stloc_1 ? new CodeInstruction(OpCodes.Ldloc_1)
                          : list[mapLocal].opcode == OpCodes.Stloc_2 ? new CodeInstruction(OpCodes.Ldloc_2)
                          : list[mapLocal].opcode == OpCodes.Stloc_3 ? new CodeInstruction(OpCodes.Ldloc_3)
                          : new CodeInstruction(OpCodes.Ldloc_S, list[mapLocal].operand);
                    yield return guard;
                    yield return new CodeInstruction(OpCodes.Brfalse_S, list[i + 1].operand);
                    i++;
                    patched = true;
                }
            }
            if (!patched) Log.Error("RimThreaded PATCH FAILED [Thing.TakeDamage]: damageWatcher guard pattern not found in " + __originalMethod?.Name);
        }

        public static bool get_Map(Thing __instance, ref Map __result)
        {
            __result = null;
            int mapIndexOrState = __instance.mapIndexOrState;
            if (mapIndexOrState >= 0)
                __result = Find.Maps[mapIndexOrState];
            //else
            //{
            //    lock (lastMapIndex)
            //    {
            //        if (lastMapIndex.TryGetValue(__instance, out sbyte lastIndex))
            //        {
            //            __result = Find.Maps[lastIndex];
            //        }
            //    }
            //}
            return false;
        }


    }
}
