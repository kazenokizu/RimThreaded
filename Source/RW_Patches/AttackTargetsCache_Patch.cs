using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimThreaded.RW_Patches
{
    // 1.6 port. The 1.4 patch kept targets in private per-cache dictionaries, but the unpatched vanilla Notify_FactionHostilityChanged iterates
    // the vanilla allTargets set (empty under that patch), so faction hostility changes never re-sorted targets; Notify_FactionAdded/Removed
    // also only touched vanilla storage; GetPotentialTargetsFor lacked the 1.6 slave-rebellion branch.
    // Now: vanilla storage and logic. Mutations run under lock(cache) and publish new sets/dictionaries (copy-on-write), so the live set that
    // TargetsHostileToFaction returns is never modified while a reader iterates it. Methods that used vanilla's static scratch lists (shared by
    // every map and thread) use local lists. The old patch is in removed_1.6/.
    public class AttackTargetsCache_Patch
    {
        public static void RunDestructivesPatches()
        {
            Type original = typeof(AttackTargetsCache);
            Type patched = typeof(AttackTargetsCache_Patch);
            RimThreadedHarmony.Prefix(original, patched, nameof(RegisterTarget));
            RimThreadedHarmony.Prefix(original, patched, nameof(DeregisterTarget));
            RimThreadedHarmony.Prefix(original, patched, nameof(GetPotentialTargetsFor));
            RimThreadedHarmony.Prefix(original, patched, nameof(Notify_FactionHostilityChanged));
            RimThreadedHarmony.Prefix(original, patched, nameof(Notify_FactionAdded));
            RimThreadedHarmony.Prefix(original, patched, nameof(Notify_FactionRemoved));
            RimThreadedHarmony.TranspileMethodLock(original, nameof(AttackTargetsCache.UpdateTarget));
        }

        public static bool RegisterTarget(AttackTargetsCache __instance, IAttackTarget target)
        {
            lock (__instance)
            {
                if (__instance.allTargets.Contains(target))
                {
                    Log.Warning("Tried to register the same target twice " + target.ToStringSafe() + " in " + __instance.GetType());
                    return false;
                }
                Thing thing = target.Thing;
                if (!thing.Spawned)
                {
                    Log.Warning("Tried to register unspawned thing " + thing.ToStringSafe() + " in " + __instance.GetType());
                    return false;
                }
                if (thing.Map != __instance.map)
                {
                    Log.Warning("Tried to register attack target " + thing.ToStringSafe() + " but its Map is not this one.");
                    return false;
                }
                __instance.allTargets = new HashSet<IAttackTarget>(__instance.allTargets) { target };
                Dictionary<Faction, HashSet<IAttackTarget>> byFaction = new Dictionary<Faction, HashSet<IAttackTarget>>(__instance.targetsHostileToFaction);
                List<Faction> factions = Find.FactionManager.AllFactionsListForReading;
                for (int i = 0; i < factions.Count; i++)
                {
                    Faction f = factions[i];
                    if (thing.HostileTo(f))
                    {
                        HashSet<IAttackTarget> set = byFaction.TryGetValue(f, out HashSet<IAttackTarget> old) ? new HashSet<IAttackTarget>(old) : new HashSet<IAttackTarget>();
                        set.Add(target);
                        byFaction[f] = set;
                    }
                }
                __instance.targetsHostileToFaction = byFaction;
                if (target is Pawn pawn)
                {
                    if (pawn.InAggroMentalState)
                        __instance.pawnsInAggroMentalState = new HashSet<Pawn>(__instance.pawnsInAggroMentalState) { pawn };
                    if (pawn.Faction == null && pawn.RaceProps.Humanlike)
                        __instance.factionlessHumanlikes = new HashSet<Pawn>(__instance.factionlessHumanlikes) { pawn };
                }
            }
            return false;
        }

        public static bool DeregisterTarget(AttackTargetsCache __instance, IAttackTarget target)
        {
            lock (__instance)
            {
                if (!__instance.allTargets.Contains(target))
                {
                    Log.Warning("Tried to deregister " + target?.ToString() + " but it's not in " + __instance.GetType());
                    return false;
                }
                HashSet<IAttackTarget> all = new HashSet<IAttackTarget>(__instance.allTargets);
                all.Remove(target);
                __instance.allTargets = all;
                Dictionary<Faction, HashSet<IAttackTarget>> byFaction = null;
                foreach (KeyValuePair<Faction, HashSet<IAttackTarget>> kv in __instance.targetsHostileToFaction)
                {
                    if (kv.Value.Contains(target))
                    {
                        if (byFaction == null) byFaction = new Dictionary<Faction, HashSet<IAttackTarget>>(__instance.targetsHostileToFaction);
                        HashSet<IAttackTarget> set = new HashSet<IAttackTarget>(kv.Value);
                        set.Remove(target);
                        byFaction[kv.Key] = set;
                    }
                }
                if (byFaction != null) __instance.targetsHostileToFaction = byFaction;
                if (target is Pawn pawn)
                {
                    if (__instance.pawnsInAggroMentalState.Contains(pawn))
                    {
                        HashSet<Pawn> s = new HashSet<Pawn>(__instance.pawnsInAggroMentalState);
                        s.Remove(pawn);
                        __instance.pawnsInAggroMentalState = s;
                    }
                    if (__instance.factionlessHumanlikes.Contains(pawn))
                    {
                        HashSet<Pawn> s = new HashSet<Pawn>(__instance.factionlessHumanlikes);
                        s.Remove(pawn);
                        __instance.factionlessHumanlikes = s;
                    }
                }
            }
            return false;
        }

        // Vanilla fills a static list shared by all maps and threads; same 1.6 logic into a fresh list.
        public static bool GetPotentialTargetsFor(AttackTargetsCache __instance, ref List<IAttackTarget> __result, IAttackTargetSearcher th)
        {
            Thing thing = th.Thing;
            List<IAttackTarget> targets = new List<IAttackTarget>();
            Faction faction = thing.Faction;
            if (faction != null)
            {
                foreach (IAttackTarget item in __instance.TargetsHostileToFaction(faction))
                {
                    if (thing.HostileTo(item.Thing))
                        targets.Add(item);
                }
            }
            foreach (Pawn item2 in __instance.pawnsInAggroMentalState)
            {
                if (thing.HostileTo(item2))
                    targets.Add(item2);
            }
            foreach (Pawn factionlessHumanlike in __instance.factionlessHumanlikes)
            {
                if (thing.HostileTo(factionlessHumanlike))
                    targets.Add(factionlessHumanlike);
            }
            Pawn pawn = th as Pawn;
            if (pawn != null && PrisonBreakUtility.IsPrisonBreaking(pawn))
            {
                Faction hostFaction = pawn.guest.HostFaction;
                List<Pawn> list = __instance.map.mapPawns.SpawnedPawnsInFaction(hostFaction);
                for (int i = 0; i < list.Count; i++)
                {
                    if (thing.HostileTo(list[i]))
                        targets.Add(list[i]);
                }
            }
            if (pawn != null && ModsConfig.IdeologyActive && SlaveRebellionUtility.IsRebelling(pawn))
            {
                Faction faction2 = pawn.Faction;
                List<Pawn> list2 = __instance.map.mapPawns.SpawnedPawnsInFaction(faction2);
                for (int j = 0; j < list2.Count; j++)
                {
                    if (thing.HostileTo(list2[j]))
                        targets.Add(list2[j]);
                }
            }
            __result = targets;
            return false;
        }

        public static bool Notify_FactionHostilityChanged(AttackTargetsCache __instance, Faction f1, Faction f2)
        {
            List<IAttackTarget> toUpdate = new List<IAttackTarget>();
            lock (__instance)
            {
                foreach (IAttackTarget allTarget in __instance.allTargets)
                {
                    Thing thing = allTarget.Thing;
                    Pawn pawn = thing as Pawn;
                    if (thing.Faction == f1 || thing.Faction == f2 || (pawn != null && pawn.HostFaction == f1) || (pawn != null && pawn.HostFaction == f2))
                        toUpdate.Add(allTarget);
                }
                for (int i = 0; i < toUpdate.Count; i++)
                    __instance.UpdateTarget(toUpdate[i]);
            }
            return false;
        }

        public static bool Notify_FactionAdded(AttackTargetsCache __instance, Faction faction)
        {
            lock (__instance)
            {
                if (!__instance.targetsHostileToFaction.ContainsKey(faction))
                {
                    __instance.targetsHostileToFaction = new Dictionary<Faction, HashSet<IAttackTarget>>(__instance.targetsHostileToFaction)
                    {
                        [faction] = new HashSet<IAttackTarget>()
                    };
                }
            }
            return false;
        }

        public static bool Notify_FactionRemoved(AttackTargetsCache __instance, Faction faction)
        {
            lock (__instance)
            {
                if (__instance.targetsHostileToFaction.ContainsKey(faction))
                {
                    Dictionary<Faction, HashSet<IAttackTarget>> copy = new Dictionary<Faction, HashSet<IAttackTarget>>(__instance.targetsHostileToFaction);
                    copy.Remove(faction);
                    __instance.targetsHostileToFaction = copy;
                }
            }
            return false;
        }
    }
}
