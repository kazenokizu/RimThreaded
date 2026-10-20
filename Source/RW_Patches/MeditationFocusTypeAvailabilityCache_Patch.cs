using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimThreaded.RW_Patches
{
    // 1.6 port. The static cache is read from any thread; the 1.4 patch locked only some writes, read without a lock, and did not cover 1.6's
    // Notify_PawnDiedOrDestroyed (called from vanilla Pawn.Destroy), which removes keys. Now every change publishes new dictionaries
    // (copy-on-write) under one lock, so lock-free readers always see a complete dictionary. The value is computed outside the lock with the
    // vanilla PawnCanUseInt. The old patch is in removed_1.6/.
    public class MeditationFocusTypeAvailabilityCache_Patch
    {
        static readonly object writeLock = new object();

        internal static void RunDestructivePatches()
        {
            Type original = typeof(MeditationFocusTypeAvailabilityCache);
            Type patched = typeof(MeditationFocusTypeAvailabilityCache_Patch);
            RimThreadedHarmony.Prefix(original, patched, nameof(PawnCanUse));
            RimThreadedHarmony.Prefix(original, patched, nameof(ClearFor));
            RimThreadedHarmony.Prefix(original, patched, nameof(Notify_PawnDiedOrDestroyed));
        }

        public static bool PawnCanUse(ref bool __result, Pawn p, MeditationFocusDef type)
        {
            Dictionary<Pawn, Dictionary<MeditationFocusDef, bool>> outer = MeditationFocusTypeAvailabilityCache.pawnCanUseMeditationTypeCached;
            if (outer.TryGetValue(p, out Dictionary<MeditationFocusDef, bool> inner) && inner.TryGetValue(type, out bool cached))
            {
                __result = cached;
                return false;
            }
            bool value = MeditationFocusTypeAvailabilityCache.PawnCanUseInt(p, type);
            lock (writeLock)
            {
                outer = MeditationFocusTypeAvailabilityCache.pawnCanUseMeditationTypeCached;
                Dictionary<MeditationFocusDef, bool> newInner = outer.TryGetValue(p, out inner)
                    ? new Dictionary<MeditationFocusDef, bool>(inner)
                    : new Dictionary<MeditationFocusDef, bool>();
                newInner[type] = value;
                MeditationFocusTypeAvailabilityCache.pawnCanUseMeditationTypeCached = new Dictionary<Pawn, Dictionary<MeditationFocusDef, bool>>(outer)
                {
                    [p] = newInner
                };
            }
            __result = value;
            return false;
        }

        public static bool ClearFor(Pawn p)
        {
            lock (writeLock)
            {
                Dictionary<Pawn, Dictionary<MeditationFocusDef, bool>> outer = MeditationFocusTypeAvailabilityCache.pawnCanUseMeditationTypeCached;
                if (outer.ContainsKey(p))
                {
                    MeditationFocusTypeAvailabilityCache.pawnCanUseMeditationTypeCached = new Dictionary<Pawn, Dictionary<MeditationFocusDef, bool>>(outer)
                    {
                        [p] = new Dictionary<MeditationFocusDef, bool>()
                    };
                }
            }
            return false;
        }

        public static bool Notify_PawnDiedOrDestroyed(Pawn p)
        {
            lock (writeLock)
            {
                Dictionary<Pawn, Dictionary<MeditationFocusDef, bool>> outer = MeditationFocusTypeAvailabilityCache.pawnCanUseMeditationTypeCached;
                if (outer.ContainsKey(p))
                {
                    Dictionary<Pawn, Dictionary<MeditationFocusDef, bool>> copy = new Dictionary<Pawn, Dictionary<MeditationFocusDef, bool>>(outer);
                    copy.Remove(p);
                    MeditationFocusTypeAvailabilityCache.pawnCanUseMeditationTypeCached = copy;
                }
            }
            return false;
        }
    }
}
