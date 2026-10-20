using System;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace RimThreaded.RW_Patches
{
    class Messages_Patch
    {
        internal static void RunDestructivePatches()
        {
            Type original = typeof(Messages);
            Type patched = typeof(Messages_Patch);
            RimThreadedHarmony.Prefix(original, patched, nameof(Update));
            RimThreadedHarmony.Prefix(original, patched, nameof(Message), new Type[] { typeof(Message), typeof(bool) });
            RimThreadedHarmony.Prefix(original, patched, nameof(MessagesDoGUI));
            RimThreadedHarmony.Prefix(original, patched, nameof(CollidesWithAnyMessage));
            RimThreadedHarmony.Prefix(original, patched, nameof(Clear));
            RimThreadedHarmony.Prefix(original, patched, nameof(Notify_LoadedLevelChanged));
            RimThreadedHarmony.Prefix(original, patched, nameof(AcceptsMessage));
            RimThreadedHarmony.Prefix(original, patched, nameof(Notify_Mouseover));
        }
        public static bool Update()
        {
            lock (Messages.liveMessages)
            {
                if (Current.ProgramState == ProgramState.Playing && Messages.mouseoverMessageIndex >= 0 && Messages.mouseoverMessageIndex < Messages.liveMessages.Count)
                    Messages.liveMessages[Messages.mouseoverMessageIndex].lookTargets.TryHighlight();
                Messages.mouseoverMessageIndex = -1;
                Messages.liveMessages.RemoveAll(m => m.Expired);
            }
            return false;
        }
        // Sounds are played after the lock is released: from a worker thread PlayOneShotOnCamera waits for the main thread, and the main thread
        // takes this same lock in MessagesDoGUI (holding it while waiting would deadlock).
        public static bool Message(Message msg, bool historical = true)
        {
            if (!Messages.AcceptsMessage(msg.text, msg.lookTargets))
                return false;
            if (historical && Find.Archive != null)
                Find.Archive.Add(msg);
            lock (Messages.liveMessages)
            {
                Messages.liveMessages.Add(msg);
                while (Messages.liveMessages.Count > 12)
                    Messages.liveMessages.RemoveAt(0);
            }
            msg.def.sound?.PlayOneShotOnCamera();
            return false;
        }
        public static bool MessagesDoGUI()
        {
            Text.Font = GameFont.Small;
            int x = (int)Messages.MessagesTopLeftStandard.x;
            int y = (int)Messages.MessagesTopLeftStandard.y;
            if (Current.Game != null && Find.ActiveLesson.ActiveLessonVisible)
                y += (int)Find.ActiveLesson.Current.MessagesYOffset;
            lock (Messages.liveMessages)
            {
                for (int index = Messages.liveMessages.Count - 1; index >= 0; --index)
                {
                    Messages.liveMessages[index].Draw(x, y);
                    y += 26;
                }
            }
            return false;
        }
        public static bool CollidesWithAnyMessage(ref bool __result, Rect rect, out float messageAlpha)
        {
            bool flag = false;
            float a = 0.0f;
            lock (Messages.liveMessages)
            {
                for (int index = 0; index < Messages.liveMessages.Count; ++index)
                {
                    Message liveMessage = Messages.liveMessages[index];
                    if (rect.Overlaps(liveMessage.lastDrawRect))
                    {
                        flag = true;
                        a = Mathf.Max(a, liveMessage.Alpha);
                    }
                }
            }
            messageAlpha = a;
            __result = flag; // the 1.4 version returned flag as the prefix result, so on a hit vanilla ran again without the lock
            return false;
        }
        public static bool Clear()
        {
            lock (Messages.liveMessages)
            {
                Messages.liveMessages.Clear();
            }
            return false;
        }
        public static bool Notify_LoadedLevelChanged()
        {
            lock (Messages.liveMessages)
            {
                for (int index = 0; index < Messages.liveMessages.Count; ++index)
                    Messages.liveMessages[index].lookTargets = null;
            }
            return false;
        }

        // 1.6 logic: an identical live message is refreshed (timer reset, flash, sound) instead of being added again; only a same-frame duplicate is
        // dropped silently. The 1.4 copy only handled the same-frame case, so repeated messages stacked up. Sound is played outside the lock.
        public static bool AcceptsMessage(ref bool __result, string text, LookTargets lookTargets)
        {
            if (text.NullOrEmpty())
            {
                __result = false;
                return false;
            }
            Message refreshed = null;
            lock (Messages.liveMessages)
            {
                for (int i = 0; i < Messages.liveMessages.Count; i++)
                {
                    Message m = Messages.liveMessages[i];
                    if (m.text == text && LookTargets.SameTargets(m.lookTargets, lookTargets))
                    {
                        if (m.startingFrame == RealTime.frameCount)
                        {
                            __result = false;
                            return false;
                        }
                        m.ResetTimer();
                        m.Flash();
                        refreshed = m;
                        break;
                    }
                }
            }
            if (refreshed != null)
            {
                refreshed.def.sound.PlayOneShotOnCamera();
                __result = false;
                return false;
            }
            __result = true;
            return false;
        }

        public static bool Notify_Mouseover(Message msg)
        {
            lock (Messages.liveMessages)
            {
                Messages.mouseoverMessageIndex = Messages.liveMessages.IndexOf(msg);
            }
            return false;
        }
    }

}
