using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Verse;

namespace RTBench
{
    // Unattended test/benchmark helper. Does nothing unless <save data folder>/rtbench.txt exists, so it is only active in the
    // separate test profile (-savedatafolder). rtbench.txt lines: save=<name>, warmup=<seconds>, duration=<seconds>, interval=<seconds>.
    [StaticConstructorOnStartup]
    public static class RTBenchStartup
    {
        internal static string save;
        internal static double warmup = 30, duration = 120, interval = 10;
        internal static bool active;

        static int rootPlayStarts, loadGameCalls;
        public static void RootPlayStart() { Log.Message("[RTBENCH] Root_Play.Start #" + (++rootPlayStarts) + "\n" + new StackTrace(1, false)); }
        public static void LoadGameCalled(string saveFileName) { Log.Message("[RTBENCH] GameDataSaveLoader.LoadGame(" + saveFileName + ") #" + (++loadGameCalls) + " thread " + System.Threading.Thread.CurrentThread.ManagedThreadId + "\n" + new StackTrace(1, false)); }

        static RTBenchStartup()
        {
            string cfg = Path.Combine(GenFilePaths.SaveDataFolderPath, "rtbench.txt");
            if (!File.Exists(cfg)) return;
            active = true;
            foreach (string raw in File.ReadAllLines(cfg))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                if (k == "save") save = v;
                else if (k == "warmup") warmup = double.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
                else if (k == "duration") duration = double.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
                else if (k == "interval") interval = double.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
            }
            Log.Message("[RTBENCH] config: save=" + (save ?? "(none)") + " warmup=" + warmup + " duration=" + duration + " interval=" + interval
                + " rimthreaded=" + (ModLister.GetActiveModWithIdentifier("majorhoff.rimthreaded") != null));
            // diagnostics: where null references are thrown (first chance, before any catch hides the frames); first 20 only
            int nre = 0;
            AppDomain.CurrentDomain.FirstChanceException += (o, e) =>
            {
                if (e.Exception is NullReferenceException && System.Threading.Interlocked.Increment(ref nre) <= 20)
                    Log.Warning("[RTBENCH] first-chance NullReferenceException on thread " + System.Threading.Thread.CurrentThread.ManagedThreadId
                        + " (" + System.Threading.Thread.CurrentThread.Name + "):\n" + new StackTrace(1, false));
            };
            var h = new HarmonyLib.Harmony("nolo.rtbench");
            h.Patch(HarmonyLib.AccessTools.Method(typeof(Root_Play), "Start"), prefix: new HarmonyLib.HarmonyMethod(typeof(RTBenchStartup), nameof(RootPlayStart)));
            h.Patch(HarmonyLib.AccessTools.Method(typeof(GameDataSaveLoader), "LoadGame", new[] { typeof(string) }), prefix: new HarmonyLib.HarmonyMethod(typeof(RTBenchStartup), nameof(LoadGameCalled)));
            if (!string.IsNullOrEmpty(save))
                LongEventHandler.ExecuteWhenFinished(() => GameDataSaveLoader.LoadGame(save));
        }
    }

    public static partial class Diag { }

    public class RTBenchComponent : GameComponent
    {
        Stopwatch clock;
        int startTicks, lastTicks, frames, lastFrames;
        double lastMark;
        bool measuring, done;

        public RTBenchComponent(Game game) { }

        public override void GameComponentUpdate()
        {
            if (!RTBenchStartup.active || done || Find.CurrentMap == null) return;
            TickManager tm = Find.TickManager;
            if (clock == null)
            {
                clock = Stopwatch.StartNew();
                // uncapped speed in both vanilla and RimThreaded (1.7: dev ultra-speed boost)
                typeof(TickManager).GetField("UltraSpeedBoost", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)?.SetValue(null, true);
                // no automatic drop to 1x during combat (vanilla dev setting; applies with and without RimThreaded)
                DebugViewSettings.neverForceNormalSpeed = true;
                Log.Message("[RTBENCH] map ready at tick " + tm.TicksGame + "; colonists " + Find.CurrentMap.mapPawns.FreeColonistsCount + ", pawns " + Find.CurrentMap.mapPawns.AllPawnsSpawnedCount);
            }
            // unattended: dialogs that force a pause (letters with choices, quest offers, game-over screens) would stop the clock
            for (int i = Find.WindowStack.Windows.Count - 1; i >= 0; i--)
            {
                Window w = Find.WindowStack.Windows[i];
                if (w.forcePause || w.preventCameraMotion && w.absorbInputAroundWindow)
                {
                    Log.Message("[RTBENCH] closing blocking window " + w.GetType().FullName);
                    w.Close(false);
                }
            }
            if (tm.CurTimeSpeed != TimeSpeed.Ultrafast) tm.CurTimeSpeed = TimeSpeed.Ultrafast;
            frames++;
            double t = clock.Elapsed.TotalSeconds;
            if (!measuring)
            {
                if (t < RTBenchStartup.warmup) return;
                measuring = true;
                startTicks = lastTicks = tm.TicksGame;
                frames = lastFrames = 0;
                lastMark = t;
                Log.Message("[RTBENCH] measuring from tick " + startTicks);
                return;
            }
            if (t - lastMark >= RTBenchStartup.interval)
            {
                double dt = t - lastMark;
                Log.Message($"[RTBENCH] t={t - RTBenchStartup.warmup:F0}s tps={(tm.TicksGame - lastTicks) / dt:F1} fps={(frames - lastFrames) / dt:F1} forcedSlow={tm.slower.ForcedNormalSpeed} speed={tm.CurTimeSpeed} paused={tm.Paused} windows={Find.WindowStack.Count} colonists={Find.CurrentMap?.mapPawns.FreeColonistsCount}");
                lastTicks = tm.TicksGame; lastFrames = frames; lastMark = t;
            }
            if (t - RTBenchStartup.warmup >= RTBenchStartup.duration)
            {
                done = true;
                double span = t - RTBenchStartup.warmup;
                Log.Message($"[RTBENCH] RESULT ticks={tm.TicksGame - startTicks} seconds={span:F1} avg_tps={(tm.TicksGame - startTicks) / span:F1} avg_fps={frames / span:F1}");
                Root.Shutdown();
            }
        }
    }
}
