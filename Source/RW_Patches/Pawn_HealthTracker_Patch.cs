using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using Verse;
using static HarmonyLib.AccessTools;

namespace RimThreaded.RW_Patches
{

    public class Pawn_HealthTracker_Patch
    {
        internal static void RunDestructivePatches()
        {
            Type original = typeof(Pawn_HealthTracker);
            Type patched = typeof(Pawn_HealthTracker_Patch);
            RimThreadedHarmony.Transpile(original, patched, nameof(RemoveHediff));
            RimThreadedHarmony.Prefix(original, patched, nameof(RestorePartRecursiveInt));
            RimThreadedHarmony.Prefix(original, patched, nameof(CheckPredicateAfterAddingHediff));
            RimThreadedHarmony.Prefix(original, patched, nameof(Notify_Resurrected));
            // 1.6: HealthTick/HealthTickInterval left vanilla. They iterate per-pawn temp copies, and all list
            // mutation goes through the copy-on-write patches above (RemoveHediff / HediffSet.AddDirect).
            RimThreadedHarmony.Prefix(original, patched, nameof(SetDead)); //optional warning instead of error
            //RimThreadedHarmony.Transpile(original, patched, nameof(CheckForStateChange));
            //RimThreadedHarmony.Prefix(original, patched, nameof(CheckForStateChange));
            MethodInfo checkForStateChange = Method(original, "CheckForStateChange");
            MethodInfo checkForStateChangeMonitorEnterMethod = Method(patched, nameof(CheckForStateChangeMonitorEnter));
            RimThreadedHarmony.harmony.Patch(checkForStateChange, prefix: new HarmonyMethod(checkForStateChangeMonitorEnterMethod, 1000));
            RimThreadedHarmony.nonDestructivePrefixes.Add(checkForStateChangeMonitorEnterMethod);
            RimThreadedHarmony.harmony.Patch(checkForStateChange, finalizer: new HarmonyMethod(Method(patched, nameof(CheckForStateChangeMonitorExit)), -1000));
            MethodInfo postApplyDamage = Method(original, "PostApplyDamage");
            MethodInfo postApplyDamageEnterMethod = Method(patched, nameof(PostApplyDamageEnter));
            RimThreadedHarmony.nonDestructivePrefixes.Add(postApplyDamageEnterMethod);
            RimThreadedHarmony.harmony.Patch(postApplyDamage, prefix: new HarmonyMethod(postApplyDamageEnterMethod, 1000));
            RimThreadedHarmony.harmony.Patch(postApplyDamage, finalizer: new HarmonyMethod(Method(patched, nameof(PostApplyDamageExit)), -1000));
        }
        [ThreadStatic] static bool Pawn_HealthTrackerLockTaken;
        [ThreadStatic] static HediffSet Pawn_HealthTrackerHediffSet;
        [ThreadStatic] static bool Pawn_HealthTrackerLockTaken2;
        [ThreadStatic] static HediffSet Pawn_HealthTrackerHediffSet2;

        public static bool CheckForStateChangeMonitorEnter(Pawn_HealthTracker __instance, DamageInfo? dinfo, Hediff hediff)
        {
            Pawn_HealthTrackerLockTaken = false;
            Pawn_HealthTrackerHediffSet = __instance.hediffSet;
            Monitor.TryEnter(Pawn_HealthTrackerHediffSet, RimThreaded.halfTimeoutMS, ref Pawn_HealthTrackerLockTaken);
            if (Pawn_HealthTrackerLockTaken)
                return true;
            Log.Error("RimThreaded.CheckForStateChange was unable to be obtain lock for Pawn_HealthTracker: " + __instance + " within timeout(MS) : " + RimThreaded.halfTimeoutMS.ToString());
            return false;
        }
        public static void CheckForStateChangeMonitorExit(Pawn_HealthTracker __instance, DamageInfo? dinfo, Hediff hediff)
        {
            if (Pawn_HealthTrackerLockTaken)
                Monitor.Exit(Pawn_HealthTrackerHediffSet);
        }

        public static bool PostApplyDamageEnter(Pawn_HealthTracker __instance, DamageInfo dinfo, float totalDamageDealt)
        {
            Pawn_HealthTrackerLockTaken2 = false;
            Pawn_HealthTrackerHediffSet2 = __instance.hediffSet;
            Monitor.TryEnter(Pawn_HealthTrackerHediffSet2, RimThreaded.halfTimeoutMS, ref Pawn_HealthTrackerLockTaken2);
            if (Pawn_HealthTrackerLockTaken2)
                return true;
            Log.Error("RimThreaded.CheckForStateChange was unable to be obtain lock for Pawn_HealthTracker: " + __instance + " within timeout(MS) : " + RimThreaded.halfTimeoutMS.ToString());
            return false;
        }
        public static void PostApplyDamageExit(Pawn_HealthTracker __instance, DamageInfo dinfo, float totalDamageDealt)
        {
            if (Pawn_HealthTrackerLockTaken2)
                Monitor.Exit(Pawn_HealthTrackerHediffSet2);
        }

        public static bool SetDead(Pawn_HealthTracker __instance)
        {
            if (__instance.Dead)
                Log.Warning(__instance.pawn.ToString() + " set dead while already dead."); //changed
            __instance.healthState = PawnHealthState.Dead;
            return false;
        }

        // 1.6 port: instead of re-implementing RemoveHediff (which changed in 1.5/1.6: DirtyCache, renderer dirtying,
        // tmpRemovedHediffs bookkeeping), the vanilla method is kept and only the unsafe in-place
        // "hediffSet.hediffs.Remove(hediff)" is swapped for a copy-on-write removal.
        public static IEnumerable<CodeInstruction> RemoveHediff(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = instructions.ToList();
            FieldInfo hediffsField = Field(typeof(HediffSet), nameof(HediffSet.hediffs));
            MethodInfo listRemove = Method(typeof(List<Hediff>), nameof(List<Hediff>.Remove), new Type[] { typeof(Hediff) });
            bool patched = false;
            for (int i = 0; i < list.Count; i++)
            {
                if (!patched && i >= 2 && list[i].opcode == OpCodes.Callvirt && (MethodInfo)list[i].operand == listRemove
                    && list[i - 1].opcode == OpCodes.Ldarg_1
                    && list[i - 2].opcode == OpCodes.Ldfld && (FieldInfo)list[i - 2].operand == hediffsField)
                {
                    // stack before: hediffSet, hediffSet.hediffs, hediff   ->   hediffSet, hediff
                    list.RemoveAt(i - 2); // drop "ldfld hediffs"; label (if any) moves to the next instruction
                    i--;
                    list[i] = new CodeInstruction(OpCodes.Call, Method(typeof(Pawn_HealthTracker_Patch), nameof(RemoveHediffCopyOnWrite)));
                    patched = true;
                }
            }
            if (!patched)
            {
                Log.Error("RimThreaded: could not patch Pawn_HealthTracker.RemoveHediff (1.6 IL pattern not found)");
            }
            return list;
        }

        public static bool RemoveHediffCopyOnWrite(HediffSet hediffSet, Hediff hediff)
        {
            lock (hediffSet)
            {
                List<Hediff> newHediffs = new List<Hediff>(hediffSet.hediffs);
                bool removed = newHediffs.Remove(hediff);
                hediffSet.hediffs = newHediffs;
                return removed;
            }
        }
        public static bool RestorePartRecursiveInt(Pawn_HealthTracker __instance, BodyPartRecord part, Hediff diffException = null)
        {
            lock (__instance.hediffSet)
            {
                List<Hediff> newHediffs = new List<Hediff>(__instance.hediffSet.hediffs); //added
                //List<Hediff> hediffs = __instance.hediffSet.hediffs; //removed
                for (int index = newHediffs.Count - 1; index >= 0; --index)
                {
                    Hediff hediff = newHediffs[index];
                    if (hediff.Part == part && hediff != diffException && !hediff.def.keepOnBodyPartRestoration)
                    {
                        newHediffs.RemoveAt(index);
                        __instance.hediffSet.hediffs = newHediffs; //added
                        hediff.PostRemoved();
                    }
                }
                for (int index = 0; index < part.parts.Count; ++index)
                    __instance.RestorePartRecursiveInt(part.parts[index], diffException);
            }
            return false;
        }
        public static bool CheckPredicateAfterAddingHediff(Pawn_HealthTracker __instance, ref bool __result, Hediff hediff, Func<bool> pred)
        {
            lock (__instance.hediffSet) //added
            {
                List<Hediff> newHediffs = new List<Hediff>(__instance.hediffSet.hediffs); //added
                HashSet<Hediff> missing = __instance.CalculateMissingPartHediffsFromInjury(hediff);
                newHediffs.Add(hediff);
                if (missing != null)
                    newHediffs.AddRange(missing);
                __instance.hediffSet.hediffs = newHediffs;//added
                __instance.hediffSet.DirtyCache();
                int num = pred() ? 1 : 0;
                if (missing != null)
                    newHediffs.RemoveAll(x => missing.Contains(x));
                newHediffs.Remove(hediff);
                __instance.hediffSet.hediffs = newHediffs;//added
                __instance.hediffSet.DirtyCache();
                __result = num != 0;
            }
            return false;
        }
        public static bool Notify_Resurrected(Pawn_HealthTracker __instance)
        {
            lock (__instance.hediffSet) //added
            {
                List<Hediff> newHediffs = new List<Hediff>(__instance.hediffSet.hediffs); //added
                __instance.healthState = PawnHealthState.Mobile;
                newHediffs.RemoveAll(x => x.def.everCurableByItem && x.TryGetComp<HediffComp_Immunizable>() != null);
                newHediffs.RemoveAll(x => x.def.everCurableByItem && x is Hediff_Injury && !x.IsPermanent());
                newHediffs.RemoveAll(x =>
                {
                    if (!x.def.everCurableByItem)
                        return false;
                    if (x.def.lethalSeverity >= 0.0)
                        return true;
                    return x.def.stages != null && x.def.stages.Any(y => y.lifeThreatening);
                });
                newHediffs.RemoveAll(x => x.def.everCurableByItem && x is Hediff_Injury && x.IsPermanent() && (double)__instance.hediffSet.GetPartHealth(x.Part) <= 0.0);
                __instance.hediffSet.hediffs = newHediffs; //added
                while (true)
                {
                    Hediff_MissingPart hediffMissingPart = __instance.hediffSet.GetMissingPartsCommonAncestors().Where(x => !__instance.hediffSet.PartOrAnyAncestorHasDirectlyAddedParts(x.Part)).FirstOrDefault();
                    if (hediffMissingPart != null)
                        __instance.RestorePart(hediffMissingPart.Part, checkStateChange: false);
                    else
                        break;
                }
                __instance.hediffSet.DirtyCache();
                if (__instance.ShouldBeDead())
                    __instance.hediffSet.hediffs.RemoveAll(h => !h.def.keepOnBodyPartRestoration);
                __instance.Notify_HediffChanged(null);
            }
            return false;
        }

    }
}