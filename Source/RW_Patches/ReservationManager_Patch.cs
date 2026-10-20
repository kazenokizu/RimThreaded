using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using static HarmonyLib.AccessTools;
using static Verse.AI.ReservationManager;

namespace RimThreaded.RW_Patches
{
    // 1.6 port. The 1.4 patch moved reservations out of ReservationManager.reservations into per-manager dictionaries and replaced 17 methods.
    // In 1.6 that is unsound: eight vanilla methods that were never replaced (IsReserved, ReserversOf, HasReservedOfDef, TryGetReserver,
    // OnlyReservationsForJobDef, IsReservedAndRespected, ReservedBy<TDriver>, LogCouldNotReserveError) still read the vanilla list, which the
    // patch no longer kept up to date; the copied Reserve also lacked the 1.6 interaction-cell logic, map events and the ignoreOtherReservations /
    // canReserversStartJobs arguments, and pooled Reservation objects were recycled while other threads could still hold them.
    // Now: vanilla storage and vanilla logic, every method that touches the list runs under lock(manager) (Monitor is re-entrant, so the
    // vanilla internal calls CanReserve -> TryGetReserver etc. are fine). The mod's hauling/harvest caches are refreshed after each change.
    // The old patch is kept in removed_1.6/.
    [StaticConstructorOnStartup]
    public class ReservationManager_Patch
    {
        static readonly (string name, Type[] args)[] LockedMethods =
        {
            ("ExposeData", Type.EmptyTypes),
            ("IsReserved", new[] { typeof(LocalTargetInfo) }),
            ("ReserversOf", new[] { typeof(LocalTargetInfo), typeof(HashSet<Pawn>) }),
            ("HasReservedOfDef", new[] { typeof(Pawn), typeof(ThingDef) }),
            ("CanReserve", new[] { typeof(Pawn), typeof(LocalTargetInfo), typeof(int), typeof(int), typeof(ReservationLayerDef), typeof(bool) }),
            ("CanReserveStack", new[] { typeof(Pawn), typeof(LocalTargetInfo), typeof(int), typeof(ReservationLayerDef), typeof(bool) }),
            ("Reserve", new[] { typeof(Pawn), typeof(Job), typeof(LocalTargetInfo), typeof(int), typeof(int), typeof(ReservationLayerDef), typeof(bool), typeof(bool), typeof(bool) }),
            ("Release", new[] { typeof(LocalTargetInfo), typeof(Pawn), typeof(Job) }),
            ("ReleaseAllForTarget", new[] { typeof(Thing) }),
            ("ReleaseClaimedBy", new[] { typeof(Pawn), typeof(Job) }),
            ("ReleaseAllClaimedBy", new[] { typeof(Pawn) }),
            ("FirstReservationFor", new[] { typeof(Pawn) }),
            ("TryGetReserver", new[] { typeof(LocalTargetInfo), typeof(Faction), typeof(Pawn).MakeByRefType() }),
            ("OnlyReservationsForJobDef", new[] { typeof(LocalTargetInfo), typeof(JobDef), typeof(bool) }),
            ("IsReservedAndRespected", new[] { typeof(LocalTargetInfo), typeof(Faction) }),
            ("FirstRespectedReserver", new[] { typeof(LocalTargetInfo), typeof(Pawn), typeof(ReservationLayerDef) }),
            ("ReservedBy", new[] { typeof(LocalTargetInfo), typeof(Pawn), typeof(Job) }),
            ("DebugString", Type.EmptyTypes),
            ("DebugDrawReservations", Type.EmptyTypes),
        };

        internal static void RunDestructivePatches()
        {
            Type original = typeof(ReservationManager);
            Type patched = typeof(ReservationManager_Patch);
            foreach (var (name, args) in LockedMethods)
                RimThreadedHarmony.TranspileMethodLock(original, name, args);

            // ReservedBy<TDriver> is generic and is not patched (Harmony's handling of generic methods on Unity's Mono is not reliable). Its only
            // caller in the game, RestUtility.IsValidBedFor -> ReservationUtility.HasReserved<JobDriver_TakeToBed>, is redirected to a helper
            // that takes the manager's lock.
            RimThreadedHarmony.Transpile(typeof(RestUtility), patched, nameof(RestUtility.IsValidBedFor));

            // Vanilla returns a lazy Select over the live list, which would be enumerated outside the lock.
            RimThreadedHarmony.Prefix(original, patched, nameof(AllReservedThings));

            // Cache maintenance (runs after the locked body).
            RimThreadedHarmony.Postfix(original, patched, "Reserve", nameof(PostReserve));
            RimThreadedHarmony.Postfix(original, patched, "Release", nameof(PostRelease));
            RimThreadedHarmony.Postfix(original, patched, "ReleaseAllForTarget", nameof(PostReleaseAllForTarget));
            PatchCapture(original, "ReleaseClaimedBy", nameof(CaptureClaimedBy), nameof(RefreshCaptured));
            PatchCapture(original, "ReleaseAllClaimedBy", nameof(CaptureAllClaimedBy), nameof(RefreshCaptured));
        }

        static void PatchCapture(Type original, string methodName, string prefixName, string postfixName)
        {
            try
            {
                MethodInfo target = Method(original, methodName);
                MethodInfo prefix = Method(typeof(ReservationManager_Patch), prefixName);
                RimThreadedHarmony.nonDestructivePrefixes.Add(prefix);
                RimThreadedHarmony.harmony.Patch(target, prefix: new HarmonyMethod(prefix), postfix: new HarmonyMethod(Method(typeof(ReservationManager_Patch), postfixName)));
            }
            catch (Exception e)
            {
                Log.Error("RimThreaded PATCH FAILED [Capture] ReservationManager." + methodName + ": " + e.GetType().Name + ": " + e.Message);
            }
        }

        public static IEnumerable<CodeInstruction> IsValidBedFor(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            MethodInfo target = typeof(ReservationUtility).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == nameof(ReservationUtility.HasReserved) && m.IsGenericMethodDefinition)
                .MakeGenericMethod(typeof(JobDriver_TakeToBed));
            MethodInfo replacement = Method(typeof(ReservationManager_Patch), nameof(HasReservedTakeToBedLocked));
            int replaced = 0;
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.opcode == System.Reflection.Emit.OpCodes.Call && ci.operand is MethodInfo mi && mi == target)
                {
                    ci.operand = replacement;
                    replaced++;
                }
                yield return ci;
            }
            if (replaced != 1) Log.Error("RimThreaded PATCH FAILED [RestUtility.IsValidBedFor]: expected 1 HasReserved<JobDriver_TakeToBed> call, replaced " + replaced);
        }

        public static bool HasReservedTakeToBedLocked(Pawn p, LocalTargetInfo target, LocalTargetInfo? targetAIsNot, LocalTargetInfo? targetBIsNot, LocalTargetInfo? targetCIsNot)
        {
            if (!p.Spawned)
                return false;
            Map map = p.Map;
            if (map == null)
                return false;
            ReservationManager rm = map.reservationManager;
            lock (rm)
            {
                return rm.ReservedBy<JobDriver_TakeToBed>(target, p, targetAIsNot, targetBIsNot, targetCIsNot);
            }
        }

        // Kept so RimThreaded.InitializeAllThreadStatics keeps compiling; nothing thread-static is needed any more.
        internal static void InitializeThreadStatics() { }

        public static bool AllReservedThings(ReservationManager __instance, ref IEnumerable<Thing> __result)
        {
            lock (__instance)
            {
                List<Thing> things = new List<Thing>(__instance.reservations.Count);
                foreach (Reservation r in __instance.reservations)
                    things.Add(r.Target.Thing);
                __result = things;
            }
            return false;
        }

        static void RefreshCache(Map map, Thing thing)
        {
            if (thing == null) return;
            if (thing.def.EverHaulable)
                HaulingCache.ReregisterHaulableItem(thing);
            if (thing is Plant plant)
                JumboCell.ReregisterObject(map, plant.Position, RimThreaded.plantHarvest_Cache);
        }

        public static void PostReserve(ReservationManager __instance, bool __result, LocalTargetInfo target)
        {
            if (__result) RefreshCache(__instance.map, target.Thing);
        }

        public static void PostRelease(ReservationManager __instance, LocalTargetInfo target)
        {
            RefreshCache(__instance.map, target.Thing);
        }

        public static void PostReleaseAllForTarget(ReservationManager __instance, Thing t)
        {
            RefreshCache(__instance.map, t);
        }

        public static void CaptureClaimedBy(ReservationManager __instance, Pawn claimant, Job job, out List<Thing> __state)
        {
            __state = Capture(__instance, r => r.Claimant == claimant && r.Job == job);
        }

        public static void CaptureAllClaimedBy(ReservationManager __instance, Pawn claimant, out List<Thing> __state)
        {
            __state = claimant == null ? null : Capture(__instance, r => r.Claimant == claimant);
        }

        static List<Thing> Capture(ReservationManager rm, Func<Reservation, bool> match)
        {
            List<Thing> result = null;
            lock (rm)
            {
                foreach (Reservation r in rm.reservations)
                {
                    Thing t = r.Target.Thing;
                    if (t != null && match(r))
                        (result ?? (result = new List<Thing>())).Add(t);
                }
            }
            return result;
        }

        public static void RefreshCaptured(ReservationManager __instance, List<Thing> __state)
        {
            if (__state == null) return;
            foreach (Thing t in __state)
                RefreshCache(__instance.map, t);
        }

        // ---- helpers used by HaulingCache / PlantHarvest_Cache / WorkGiver_GrowerHarvest_Patch (snapshots taken under the manager's lock) ----

        public static List<Reservation> getReservationTargetList(ReservationManager __instance, LocalTargetInfo target)
        {
            List<Reservation> result = new List<Reservation>();
            lock (__instance)
            {
                List<Reservation> all = __instance.reservations;
                for (int i = 0; i < all.Count; i++)
                    if (all[i].Target == target)
                        result.Add(all[i]);
            }
            return result;
        }

        public static Pawn getFirstPawnReservingTarget(ReservationManager __instance, LocalTargetInfo target)
        {
            List<Reservation> list = getReservationTargetList(__instance, target);
            return list.Count > 0 ? list[0].Claimant : null;
        }

        // Same logic as the private vanilla ReservationManager.RespectsReservationsOf(Pawn, Pawn) in 1.6.
        public static bool RespectsReservationsOf(Pawn newClaimant, Pawn oldClaimant)
        {
            if (newClaimant == oldClaimant) return true;
            if (newClaimant.Faction == null || oldClaimant.Faction == null) return false;
            if (newClaimant.Faction == oldClaimant.Faction) return true;
            if (!newClaimant.Faction.HostileTo(oldClaimant.Faction)) return true;
            if (oldClaimant.HostFaction != null && oldClaimant.HostFaction == newClaimant.HostFaction) return true;
            if (newClaimant.HostFaction != null)
            {
                if (oldClaimant.HostFaction != null) return true;
                if (newClaimant.HostFaction == oldClaimant.Faction) return true;
            }
            return false;
        }

        // "Could any pawn reserve this?" used by the harvest cache: vanilla CanReserve without a claimant.
        public static bool IsUnreserved(ReservationManager __instance, LocalTargetInfo target, int maxPawns = 1, int stackCount = -1, ReservationLayerDef layer = null, bool ignoreOtherReservations = false)
        {
            Map mapInstance = __instance.map;
            if (!target.IsValid || target.ThingDestroyed)
                return false;
            if (target.HasThing && target.Thing.SpawnedOrAnyParentSpawned && target.Thing.MapHeld != mapInstance)
                return false;
            int num = !target.HasThing ? 1 : target.Thing.stackCount;
            int num2 = stackCount == -1 ? num : stackCount;
            if (num2 > num)
                return false;
            if (ignoreOtherReservations)
                return true;
            if (mapInstance.physicalInteractionReservationManager.IsReserved(target))
                return false;
            int num3 = 0;
            int num4 = 0;
            foreach (Reservation reservation in getReservationTargetList(__instance, target))
            {
                if (reservation.Layer != layer)
                    continue;
                if (reservation.MaxPawns != maxPawns)
                    return false;
                num3++;
                num4 = reservation.StackCount != -1 ? num4 + reservation.StackCount : num4 + num;
                if (num3 >= maxPawns || num2 + num4 > num)
                    return false;
            }
            return true;
        }
    }
}
