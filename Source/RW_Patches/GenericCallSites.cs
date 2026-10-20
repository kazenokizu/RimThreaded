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
            // generic methods that use SimplePool<T> (a Queue shared by all threads; call sites with concrete T are redirected to
            // SimplePool_Patch<T> by RimThreadedHarmony.replaceGenericTypes, these two cannot be). 1.7 Player.log run 10:
            // NullReferenceException in GenCollection.RemoveAll<TKey,TValue> from SituationalThoughtHandler on a worker thread.
            Map(GenericDefFirstParam(typeof(GenCollection), "RemoveAll", typeof(Dictionary<,>)), nameof(DictionaryRemoveAll));
            Map(GenericDefFirstParam(typeof(GenList), "ClearAndPoolValueLists", typeof(Dictionary<,>)), nameof(ClearAndPoolValueLists));
        }

        static MethodInfo GenericDefFirstParam(Type type, string name, Type firstParamGenericDef)
        {
            foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (m.Name != name || !m.IsGenericMethodDefinition) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length > 0 && ps[0].ParameterType.IsGenericType && ps[0].ParameterType.GetGenericTypeDefinition() == firstParamGenericDef)
                    return m;
            }
            return null;
        }

        // GenCollection.RemoveAll<TKey,TValue>(Dictionary, Predicate): vanilla body with a local key list instead of SimplePool.
        public static int DictionaryRemoveAll<TKey, TValue>(Dictionary<TKey, TValue> dictionary, Predicate<KeyValuePair<TKey, TValue>> predicate)
        {
            List<TKey> keys = null;
            foreach (KeyValuePair<TKey, TValue> pair in dictionary)
            {
                if (predicate(pair))
                {
                    if (keys == null) keys = new List<TKey>();
                    keys.Add(pair.Key);
                }
            }
            if (keys == null) return 0;
            for (int i = 0; i < keys.Count; i++)
                dictionary.Remove(keys[i]);
            return keys.Count;
        }

        // GenList.ClearAndPoolValueLists<T,V>: same, returning the lists to the thread-safe pool the redirected Get calls use.
        public static void ClearAndPoolValueLists<T, V>(Dictionary<T, List<V>> source)
        {
            foreach (List<V> list in source.Values)
            {
                list.Clear();
                SimplePool_Patch<List<V>>.Return(list);
            }
            source.Clear();
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
