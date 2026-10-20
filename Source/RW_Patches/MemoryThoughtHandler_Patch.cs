using RimWorld;
using System;
using System.Collections.Generic;
using Verse;

namespace RimThreaded.RW_Patches
{
    public class MemoryThoughtHandler_Patch
    {

        public static void RunDestructivePatches()
        {
            Type original = typeof(MemoryThoughtHandler);
            Type patched = typeof(MemoryThoughtHandler_Patch);
            RimThreadedHarmony.Prefix(original, patched, nameof(RemoveMemory));
            RimThreadedHarmony.Prefix(original, patched, nameof(NumMemoriesOfDef));
            RimThreadedHarmony.Prefix(original, patched, nameof(NumMemoriesInGroup));
            RimThreadedHarmony.Prefix(original, patched, nameof(TryGainMemory), new Type[] { typeof(Thought_Memory), typeof(Pawn) });
        }

        public static bool NumMemoriesOfDef(MemoryThoughtHandler __instance, ref int __result, ThoughtDef def)
        {
            int num = 0;
            List<Thought_Memory> memories = __instance.memories;
            for (int index = 0; index < memories.Count; ++index)
            {
                Thought_Memory memory = memories[index];
                if (memory != null && memory.def == def)
                    ++num;
            }
            __result = num;
            return false;
        }
        public static bool NumMemoriesInGroup(MemoryThoughtHandler __instance, ref int __result, Thought_Memory group)
        {
            int num = 0;
            List<Thought_Memory> memories = __instance.memories;
            for (int index = 0; index < memories.Count; ++index)
            {
                Thought_Memory memory = memories[index];
                if (memory != null && memory.GroupsWith(group))
                    ++num;
            }
            __result = num;
            return false;
        }
        public static bool RemoveMemory(MemoryThoughtHandler __instance, Thought_Memory th)
        {
            lock (__instance)
            {
                List<Thought_Memory> newMemories = new List<Thought_Memory>(__instance.memories);
                if (!newMemories.Remove(th))
                {
                    Log.Warning("Tried to remove memory thought of def " + th.def.defName + " but it's not here.");
                }
                else
                {
                    __instance.memories = newMemories;
                }
            }
            return false;
        }

        // 1.6 logic. The 1.4 copy overwrote newThought.otherPawn with the (often null) argument instead of "otherPawn ?? newThought.otherPawn",
        // lacked the developmental-stage filter for social thoughts, and dropped the replaceThoughts / Notify_NewThoughtInGroupAdded pass.
        // Serialized on the handler; the memory list is replaced on add (copy-on-write, like RemoveMemory) so readers never see it change.
        public static bool TryGainMemory(MemoryThoughtHandler __instance, Thought_Memory newThought, Pawn otherPawn = null)
        {
            Pawn pawn = __instance.pawn;
            bool showBubble;
            lock (__instance)
            {
                if (!ThoughtUtility.CanGetThought(pawn, newThought.def))
                    return false;
                if (newThought is Thought_MemorySocial)
                {
                    if (newThought.otherPawn == null && otherPawn == null)
                    {
                        Log.Error("Can't gain social thought " + newThought.def?.ToString() + " because its otherPawn is null and otherPawn passed to this method is also null. Social thoughts must have otherPawn.");
                        return false;
                    }
                    otherPawn = otherPawn ?? newThought.otherPawn;
                    if (!newThought.def.socialTargetDevelopmentalStageFilter.Has(otherPawn.DevelopmentalStage))
                        return false;
                }
                newThought.pawn = pawn;
                newThought.otherPawn = otherPawn;
                if (!newThought.TryMergeWithExistingMemory(out showBubble))
                    __instance.memories = new List<Thought_Memory>(__instance.memories) { newThought };
                if (newThought.def.stackLimitForSameOtherPawn >= 0)
                {
                    while (__instance.NumMemoriesInGroup(newThought) > newThought.def.stackLimitForSameOtherPawn)
                        __instance.RemoveMemory(__instance.OldestMemoryInGroup(newThought));
                }
                if (newThought.def.stackLimit >= 0)
                {
                    while (__instance.NumMemoriesOfDef(newThought.def) > newThought.def.stackLimit)
                        __instance.RemoveMemory(__instance.OldestMemoryOfDef(newThought.def));
                }
                if (newThought.def.thoughtToMake != null)
                    __instance.TryGainMemory(newThought.def.thoughtToMake, newThought.otherPawn);
                // RemoveMemory replaces __instance.memories, so iterating the current list is safe (vanilla copies only when replacing).
                List<Thought_Memory> list = __instance.memories;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] != newThought && list[i].GroupsWith(newThought))
                        list[i].Notify_NewThoughtInGroupAdded(newThought);
                    if (newThought.def.replaceThoughts != null && newThought.def.replaceThoughts.Contains(list[i].def))
                        __instance.RemoveMemory(list[i]);
                }
            }
            if (showBubble && newThought.def.showBubble && pawn.Spawned && PawnUtility.ShouldSendNotificationAbout(pawn))
                MoteMaker.MakeMoodThoughtBubble(pawn, newThought);
            return false;
        }

    }
}
