using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimThreaded.RW_Patches
{
    // 1.6 port of MapPawns.
    // 1) 1.6 added MapPawns.AssertMainThread(): FactionDictionary.GetPawnList, AllPawnsUnspawned and FreeColonistsAndPrisonersSpawned throw
    //    "Accessing map pawns off main thread" on any other thread, because vanilla hands out pooled result lists. RimThreaded workers query
    //    MapPawns constantly, so the assert is disabled and every pooled result is made safe instead:
    //    - plain List<Pawn> result pools are thread-static (replacements_1.6.json, now including the lists 1.5/1.6 added);
    //    - the FactionDictionary result pools (private nested type) are bypassed: these queries return a fresh list;
    //    - the live registries (pawnsSpawned, per-faction spawned lists, prisoners, slaves, shamblers) are copy-on-write under lock(MapPawns),
    //      so a list handed to a reader is never modified afterwards.
    // 2) The 1.4 copies had drifted: no slavesOfColonySpawned (Ideology), no null-faction support, no Anomaly subhuman filters, and
    //    FreeHumanlikesOfFaction wrote its result into the spawned-only cache. Logic below follows 1.6. The old copy is in removed_1.6/.
    public class MapPawns_Patch
    {
        internal static void RunDestructivePatches()
        {
            Type original = typeof(MapPawns);
            Type patched = typeof(MapPawns_Patch);
            RimThreadedHarmony.Prefix(original, patched, nameof(AssertMainThread));
            RimThreadedHarmony.Prefix(original, patched, nameof(EnsureFactionsListsInit));
            RimThreadedHarmony.Prefix(original, patched, nameof(SpawnedPawnsInFaction));
            RimThreadedHarmony.Prefix(original, patched, nameof(PawnsInFaction));
            RimThreadedHarmony.Prefix(original, patched, nameof(FreeHumanlikesOfFaction));
            RimThreadedHarmony.Prefix(original, patched, nameof(FreeHumanlikesSpawnedOfFaction));
            RimThreadedHarmony.Prefix(original, patched, nameof(SpawnedBabiesInFaction));
            RimThreadedHarmony.Prefix(original, patched, nameof(FactionsOnMap));
            RimThreadedHarmony.Prefix(original, patched, nameof(RegisterPawn));
            RimThreadedHarmony.Prefix(original, patched, nameof(DeRegisterPawn));
            RimThreadedHarmony.Prefix(original, patched, nameof(RegisterShambler));
            RimThreadedHarmony.Prefix(original, patched, nameof(DeregisterShambler));
        }

        public static bool AssertMainThread() => false;

        public static bool EnsureFactionsListsInit(MapPawns __instance)
        {
            List<Faction> factions = Find.FactionManager.AllFactionsListForReading;
            Dictionary<Faction, List<Pawn>> current = __instance.pawnsInFactionSpawned.pawnList;
            bool missing = false;
            for (int i = 0; i < factions.Count; i++)
            {
                if (!current.ContainsKey(factions[i])) { missing = true; break; }
            }
            if (!missing) return false;
            lock (__instance)
            {
                current = __instance.pawnsInFactionSpawned.pawnList;
                Dictionary<Faction, List<Pawn>> copy = null;
                for (int i = 0; i < factions.Count; i++)
                {
                    if (!current.ContainsKey(factions[i]))
                    {
                        if (copy == null) copy = new Dictionary<Faction, List<Pawn>>(current);
                        copy[factions[i]] = new List<Pawn>(32);
                    }
                }
                if (copy != null) __instance.pawnsInFactionSpawned.pawnList = copy;
            }
            return false;
        }

        static List<Pawn> LiveSpawnedList(MapPawns mp, Faction faction)
        {
            if (faction == null) return mp.pawnsInFactionSpawned.nullFactionPawns;
            return mp.pawnsInFactionSpawned.pawnList.TryGetValue(faction, out List<Pawn> list) ? list : new List<Pawn>();
        }

        public static bool SpawnedPawnsInFaction(MapPawns __instance, ref List<Pawn> __result, Faction faction)
        {
            EnsureFactionsListsInit(__instance);
            __result = LiveSpawnedList(__instance, faction);
            return false;
        }

        public static bool PawnsInFaction(MapPawns __instance, ref List<Pawn> __result, Faction faction)
        {
            List<Pawn> result = new List<Pawn>(32);
            List<Pawn> allPawns = __instance.AllPawns;
            for (int i = 0; i < allPawns.Count; i++)
            {
                if (allPawns[i].Faction == faction)
                    result.Add(allPawns[i]);
            }
            __result = result;
            return false;
        }

        public static bool FreeHumanlikesOfFaction(MapPawns __instance, ref List<Pawn> __result, Faction faction)
        {
            List<Pawn> result = new List<Pawn>();
            List<Pawn> allPawns = __instance.AllPawns;
            for (int i = 0; i < allPawns.Count; i++)
            {
                Pawn p = allPawns[i];
                if ((!ModsConfig.AnomalyActive || !p.IsSubhuman) && p.Faction == faction && (p.HostFaction == null || p.IsSlave) && p.RaceProps.Humanlike)
                    result.Add(p);
            }
            __result = result;
            return false;
        }

        public static bool FreeHumanlikesSpawnedOfFaction(MapPawns __instance, ref List<Pawn> __result, Faction faction)
        {
            List<Pawn> result = new List<Pawn>();
            List<Pawn> list = __instance.SpawnedPawnsInFaction(faction);
            for (int i = 0; i < list.Count; i++)
            {
                Pawn p = list[i];
                if ((!ModsConfig.AnomalyActive || !p.IsSubhuman) && p.HostFaction == null && p.RaceProps.Humanlike)
                    result.Add(p);
            }
            __result = result;
            return false;
        }

        public static bool SpawnedBabiesInFaction(MapPawns __instance, ref List<Pawn> __result, Faction faction)
        {
            List<Pawn> result = new List<Pawn>();
            List<Pawn> list = __instance.SpawnedPawnsInFaction(faction);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].DevelopmentalStage.Baby())
                    result.Add(list[i]);
            }
            __result = result;
            return false;
        }

        // Vanilla derives this from the pooled PawnsInFaction results; with fresh results the same answer is the set of factions of the map's pawns.
        public static bool FactionsOnMap(MapPawns __instance, ref List<Faction> __result)
        {
            List<Faction> result = new List<Faction>();
            List<Pawn> allPawns = __instance.AllPawns;
            for (int i = 0; i < allPawns.Count; i++)
            {
                Faction f = allPawns[i].Faction;
                if (!result.Contains(f))
                    result.Add(f);
            }
            __result = result;
            return false;
        }

        static List<Pawn> With(List<Pawn> list, Pawn p)
        {
            List<Pawn> copy = new List<Pawn>(list.Count + 1);
            copy.AddRange(list);
            copy.Add(p);
            return copy;
        }

        static List<Pawn> Without(List<Pawn> list, Pawn p)
        {
            if (!list.Contains(p)) return list;
            List<Pawn> copy = new List<Pawn>(list);
            copy.Remove(p);
            return copy;
        }

        public static bool RegisterPawn(MapPawns __instance, Pawn p)
        {
            if (p.Dead)
            {
                Log.Warning("Tried to register dead pawn " + p?.ToString() + " in " + __instance.GetType()?.ToString() + ".");
                return false;
            }
            if (!p.Spawned)
            {
                Log.Warning("Tried to register despawned pawn " + p?.ToString() + " in " + __instance.GetType()?.ToString() + ".");
                return false;
            }
            if (p.Map != __instance.map)
            {
                Log.Warning("Tried to register pawn " + p?.ToString() + " but his Map is not this one.");
                return false;
            }
            if (!p.mindState.Active)
            {
                return false;
            }
            EnsureFactionsListsInit(__instance);
            lock (__instance)
            {
                if (!__instance.pawnsSpawned.Contains(p))
                    __instance.pawnsSpawned = With(__instance.pawnsSpawned, p);
                if (p.Faction != null)
                {
                    Dictionary<Faction, List<Pawn>> byFaction = __instance.pawnsInFactionSpawned.pawnList;
                    if (!byFaction.TryGetValue(p.Faction, out List<Pawn> list))
                    {
                        byFaction = new Dictionary<Faction, List<Pawn>>(byFaction);
                        list = new List<Pawn>(32);
                        byFaction[p.Faction] = list;
                        __instance.pawnsInFactionSpawned.pawnList = byFaction;
                    }
                    if (!list.Contains(p))
                    {
                        List<Pawn> newList = With(list, p);
                        if (p.Faction == Faction.OfPlayer)
                        {
                            newList.InsertionSort(delegate (Pawn a, Pawn b)
                            {
                                int num = a.playerSettings?.joinTick ?? 0;
                                int value = b.playerSettings?.joinTick ?? 0;
                                return num.CompareTo(value);
                            });
                        }
                        Dictionary<Faction, List<Pawn>> copy = new Dictionary<Faction, List<Pawn>>(__instance.pawnsInFactionSpawned.pawnList);
                        copy[p.Faction] = newList;
                        __instance.pawnsInFactionSpawned.pawnList = copy;
                    }
                }
                if (p.IsPrisonerOfColony && !__instance.prisonersOfColonySpawned.Contains(p))
                    __instance.prisonersOfColonySpawned = With(__instance.prisonersOfColonySpawned, p);
                if (p.IsSlaveOfColony && !__instance.slavesOfColonySpawned.Contains(p))
                    __instance.slavesOfColonySpawned = With(__instance.slavesOfColonySpawned, p);
            }
            __instance.DoListChangedNotifications();
            return false;
        }

        public static bool DeRegisterPawn(MapPawns __instance, Pawn p)
        {
            EnsureFactionsListsInit(__instance);
            lock (__instance)
            {
                __instance.pawnsSpawned = Without(__instance.pawnsSpawned, p);
                Dictionary<Faction, List<Pawn>> byFaction = __instance.pawnsInFactionSpawned.pawnList;
                Dictionary<Faction, List<Pawn>> copy = null;
                foreach (KeyValuePair<Faction, List<Pawn>> kv in byFaction)
                {
                    if (kv.Value.Contains(p))
                    {
                        if (copy == null) copy = new Dictionary<Faction, List<Pawn>>(byFaction);
                        copy[kv.Key] = Without(kv.Value, p);
                    }
                }
                if (copy != null) __instance.pawnsInFactionSpawned.pawnList = copy;
                __instance.prisonersOfColonySpawned = Without(__instance.prisonersOfColonySpawned, p);
                __instance.slavesOfColonySpawned = Without(__instance.slavesOfColonySpawned, p);
            }
            __instance.DoListChangedNotifications();
            return false;
        }

        public static bool RegisterShambler(MapPawns __instance, Pawn p)
        {
            if (ModLister.CheckAnomaly("Shambler registration"))
            {
                lock (__instance)
                {
                    if (!__instance.shamblersSpawned.Contains(p))
                        __instance.shamblersSpawned = With(__instance.shamblersSpawned, p);
                }
            }
            return false;
        }

        public static bool DeregisterShambler(MapPawns __instance, Pawn p)
        {
            if (ModLister.CheckAnomaly("Shambler deregistration"))
            {
                lock (__instance)
                {
                    __instance.shamblersSpawned = Without(__instance.shamblersSpawned, p);
                }
            }
            return false;
        }
    }
}
