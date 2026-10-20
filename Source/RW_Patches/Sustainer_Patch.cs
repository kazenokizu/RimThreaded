using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Verse;
using Verse.Sound;
using static HarmonyLib.AccessTools;


namespace RimThreaded.RW_Patches
{
    class Sustainer_Patch
    {

        internal static void RunNonDestructivePatches()
        {
            Type original = typeof(Sustainer);
            Type patched = typeof(Sustainer_Patch);
            ConstructorInfo oMethod = original.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(c => c.GetParameters().Length > 0 && c.GetParameters()[0].ParameterType == typeof(SoundDef));
            if (oMethod == null)
            {
                Log.Error("RimThreaded PATCH FAILED [Sustainer ctor]: constructor (SoundDef, SoundInfo) not found");
                return;
            }
            HarmonyMethod transpilerMethod = new HarmonyMethod(Method(patched, nameof(TranspileCtor)));
            RimThreadedHarmony.harmony.Patch(oMethod, transpiler: transpilerMethod);
        }

        public static IEnumerable<CodeInstruction> TranspileCtor(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> ciList = instructions.ToList();
            MethodInfo getSoundRoot = Method(typeof(Find), "get_SoundRoot");
            FieldInfo sustainerManagerField = Field(typeof(SoundRoot), nameof(SoundRoot.sustainerManager));
            MethodInfo register = Method(typeof(SustainerManager), nameof(SustainerManager.RegisterSustainer));
            MethodInfo updateScopes = Method(typeof(SustainerManager), nameof(SustainerManager.UpdateAllSustainerScopes));
            int replaced = 0;
            int i = 0;
            while (i < ciList.Count)
            {
                // Find.SoundRoot.sustainerManager.RegisterSustainer(this)  =>  RegisterSustainer(this)
                if (i + 3 < ciList.Count &&
                    ciList[i].Calls(getSoundRoot) &&
                    ciList[i + 1].LoadsField(sustainerManagerField) &&
                    ciList[i + 2].opcode == OpCodes.Ldarg_0 &&
                    ciList[i + 3].Calls(register))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0).WithLabels(ciList[i].labels);
                    yield return new CodeInstruction(OpCodes.Call, Method(typeof(Sustainer_Patch), nameof(SustainerManagerRegisterSustainer)));
                    i += 4;
                    replaced++;
                    continue;
                }
                // Find.SoundRoot.sustainerManager.UpdateAllSustainerScopes()  =>  UpdateAllSustainerScopes()
                if (i + 2 < ciList.Count &&
                    ciList[i].Calls(getSoundRoot) &&
                    ciList[i + 1].LoadsField(sustainerManagerField) &&
                    ciList[i + 2].Calls(updateScopes))
                {
                    yield return new CodeInstruction(OpCodes.Call, Method(typeof(Sustainer_Patch), nameof(SustainerManagerUpdateAllSustainerScopes))).WithLabels(ciList[i].labels);
                    i += 3;
                    replaced++;
                    continue;
                }
                yield return ciList[i];
                i++;
            }
            if (replaced != 2) Log.Error("RimThreaded PATCH FAILED [Sustainer ctor]: expected 2 call sites, replaced " + replaced);
        }
        public static void SustainerManagerRegisterSustainer(Sustainer sustainer)
        {
            SoundRoot soundRoot = Find.SoundRoot;
            if (soundRoot == null)
            {
                Log.Error("SoundRoot is null");
                return;
            }
            SustainerManager sustainerManager = soundRoot.sustainerManager;
            if (sustainerManager == null)
            {
                Log.Error("SustainerManager is null");
                return;
            }
            sustainerManager.RegisterSustainer(sustainer);
        }
        public static void SustainerManagerUpdateAllSustainerScopes()
        {
            SoundRoot soundRoot = Find.SoundRoot;
            if (soundRoot == null)
            {
                Log.Error("SoundRoot is null");
                return;
            }
            SustainerManager sustainerManager = soundRoot.sustainerManager;
            if (sustainerManager == null)
            {
                Log.Error("SustainerManager is null");
                return;
            }
            sustainerManager.UpdateAllSustainerScopes();
        }
    }
}
