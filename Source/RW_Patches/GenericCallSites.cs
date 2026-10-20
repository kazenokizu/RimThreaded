using System;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using Verse;
using static HarmonyLib.AccessTools;

namespace RimThreaded.RW_Patches
{
    // Generic vanilla methods that use replaced (per-thread) fields. Field replacement cannot reach their instantiations, and on the 1.7
    // runtime Harmony prefixes on instantiations (the old ThingOwnerUtility_Patch.GetAllThingsRecursively_Pawn/_Thing) were not used:
    // the Player.log shows the vanilla body running. Every call site in the game is redirected here instead (RimThreadedHarmony
    // .replaceGenericMethods); the replacements are generic with the same type parameters.
    // Found by scanning Assembly-CSharp (1.6 and 1.7) for generic methods whose IL touches a field listed in replacements_*.json.
    public static class GenericCallSites
    {
        internal static void Register()
        {
            Map(GenericDef(typeof(ThingOwnerUtility), nameof(ThingOwnerUtility.GetAllThingsRecursively), 6), nameof(GetAllThingsRecursively));
            Map(GenericDef(typeof(Room), nameof(Room.ContainedThings), 0), nameof(ContainedThings));
            Map(GenericDef(typeof(CreepJoinerUtility), nameof(CreepJoinerUtility.GetRandom), 4), nameof(CreepJoinerGetRandom));
            Map(GenericDef(typeof(PawnStyleItemChooser), "ChooseStyleItem", 3), nameof(ChooseStyleItem));
        }

        static MethodInfo GenericDef(Type type, string name, int parameterCount)
        {
            foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (m.Name == name && m.IsGenericMethodDefinition && m.GetParameters().Length == parameterCount)
                    return m;
            return null;
        }

        static void Map(MethodInfo vanillaGeneric, string replacementName)
        {
            if (vanillaGeneric == null)
            {
                Log.Error("RimThreaded: generic call-site target for " + replacementName + " not found");
                return;
            }
            MethodInfo def = vanillaGeneric.IsGenericMethodDefinition ? vanillaGeneric : vanillaGeneric.GetGenericMethodDefinition();
            MethodInfo replacement = typeof(GenericCallSites).GetMethod(replacementName, BindingFlags.Public | BindingFlags.Static);
            RimThreadedHarmony.replaceGenericMethods[def] = replacement;
        }

        // ThingOwnerUtility.GetAllThingsRecursively<T>(Map, ...): vanilla body with this thread's working lists.
        public static void GetAllThingsRecursively<T>(Map map, ThingRequest request, List<T> outThings, bool allowUnreal = true,
            Predicate<IThingHolder> passCheck = null, bool alsoGetSpawnedThings = true) where T : Thing
        {
            outThings.Clear();
            if (alsoGetSpawnedThings)
            {
                List<Thing> list = map.listerThings.ThingsMatching(request);
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] is T t)
                        outThings.Add(t);
                }
            }
            List<IThingHolder> holders = new List<IThingHolder>();
            List<Thing> things = new List<Thing>();
            map.GetChildHolders(holders);
            for (int j = 0; j < holders.Count; j++)
            {
                things.Clear();
                ThingOwnerUtility.GetAllThingsRecursively(holders[j], things, allowUnreal, passCheck);
                for (int k = 0; k < things.Count; k++)
                {
                    if (things[k] is T t2 && request.Accepts(t2))
                        outThings.Add(t2);
                }
            }
        }

        // Room.ContainedThings<T>(): vanilla iterator with its own de-duplication set instead of the room's shared one.
        public static IEnumerable<T> ContainedThings<T>(Room room) where T : Thing
        {
            HashSet<Thing> seen = new HashSet<Thing>();
            foreach (Region region in room.Regions)
            {
                foreach (T item in region.ListerThings.GetThingsOfType<T>())
                {
                    if (seen.Add(item))
                        yield return item;
                }
            }
        }

        static readonly object creepJoinerLock = new object();
        static readonly object styleItemLock = new object();

        // These two use one static scratch list each; calls are serialized (both are rare: creepjoiner generation, pawn styling).
        public static T CreepJoinerGetRandom<T>(List<T> defs, float combatPoints, List<CreepJoinerBaseDef> requires, List<CreepJoinerBaseDef> exclude) where T : CreepJoinerBaseDef
        {
            lock (creepJoinerLock)
                return CreepJoinerUtility.GetRandom(defs, combatPoints, requires, exclude);
        }

        public static T ChooseStyleItem<T>(Pawn pawn, T fallback, TattooType? tattooType = null) where T : StyleItemDef
        {
            lock (styleItemLock)
                return PawnStyleItemChooser.ChooseStyleItem(pawn, fallback, tattooType);
        }
    }
}
