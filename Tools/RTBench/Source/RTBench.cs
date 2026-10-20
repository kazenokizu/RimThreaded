using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using RimWorld;
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
        internal static int colonists, meals, rawFood;
        internal static bool noIncidents;

        // noincidents=1: no raids, events or quest incidents, so every run measures a colony of the same size
        public static bool BlockIncident(ref bool __result) { __result = false; return false; }

        // what happened during the run: incidents that fired and every death with its cause
        internal static int incidents, deaths, colonistDeaths;
        public static void IncidentDone(IncidentWorker __instance, bool __result)
        {
            if (!__result) return;
            System.Threading.Interlocked.Increment(ref incidents);
            Log.Message("[RTBENCH] incident " + __instance.def?.defName + " at tick " + Find.TickManager.TicksGame);
        }
        public static void PawnKilled(Pawn __instance, DamageInfo? dinfo, Hediff exactCulprit)
        {
            System.Threading.Interlocked.Increment(ref deaths);
            bool colonist = __instance.Faction == Faction.OfPlayer && __instance.RaceProps.Humanlike;
            if (colonist) System.Threading.Interlocked.Increment(ref colonistDeaths);
            if (colonist || __instance.RaceProps.Humanlike)
                Log.Message("[RTBENCH] death " + __instance.LabelShort + (colonist ? " (colonist)" : " (" + __instance.Faction?.Name + ")")
                    + " cause=" + (exactCulprit?.def?.defName ?? dinfo?.Def?.defName ?? "unknown") + " at tick " + Find.TickManager.TicksGame);
        }

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
                else if (k == "colonists") colonists = int.Parse(v);
                else if (k == "meals") meals = int.Parse(v);
                else if (k == "rawfood") rawFood = int.Parse(v);
                else if (k == "noincidents") noIncidents = v == "1" || v == "true";
            }
            Log.Message("[RTBENCH] config: save=" + (save ?? "(none)") + " warmup=" + warmup + " duration=" + duration + " interval=" + interval + " colonists=+" + colonists + " meals=" + meals + " rawfood=" + rawFood + " noincidents=" + noIncidents
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
            if (noIncidents)
                h.Patch(HarmonyLib.AccessTools.Method(typeof(RimWorld.IncidentWorker), "TryExecute"), prefix: new HarmonyLib.HarmonyMethod(typeof(RTBenchStartup), nameof(BlockIncident)));
            h.Patch(HarmonyLib.AccessTools.Method(typeof(IncidentWorker), "TryExecute"), postfix: new HarmonyLib.HarmonyMethod(typeof(RTBenchStartup), nameof(IncidentDone)));
            h.Patch(HarmonyLib.AccessTools.Method(typeof(Pawn), "Kill"), postfix: new HarmonyLib.HarmonyMethod(typeof(RTBenchStartup), nameof(PawnKilled)));
            if (!string.IsNullOrEmpty(save))
                LongEventHandler.ExecuteWhenFinished(() => GameDataSaveLoader.LoadGame(save));
        }
    }

    public static partial class Diag { }

    public class RTBenchComponent : GameComponent
    {
        // Test copy only (the save on disk is never written): names the faction/settlements so the naming dialog (opened every 1000 ticks
        // otherwise) stays closed, then adds colonists and food so the colony is large and stays alive for the whole run.
        static void PrepareColony(Map map)
        {
            if (!Faction.OfPlayer.HasName) Faction.OfPlayer.Name = "RTBench Colony";
            foreach (RimWorld.Planet.Settlement s in Find.WorldObjects.Settlements)
                if (s.Faction == Faction.OfPlayer) s.namedByPlayer = true;
            Pawn anchor = map.mapPawns.FreeColonistsSpawned.Count > 0 ? map.mapPawns.FreeColonistsSpawned[0] : null;
            IntVec3 center = anchor != null ? anchor.Position : map.Center;
            for (int i = 0; i < RTBenchStartup.colonists; i++)
            {
                Pawn p = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                GenSpawn.Spawn(p, CellFinder.RandomClosewalkCellNear(center, map, 8), map);
            }
            int left = RTBenchStartup.meals;
            while (left > 0)
            {
                Thing meal = ThingMaker.MakeThing(ThingDefOf.MealSurvivalPack);
                meal.stackCount = Math.Min(left, meal.def.stackLimit);
                left -= meal.stackCount;
                GenPlace.TryPlaceThing(meal, center, map, ThingPlaceMode.Near);
            }
            left = RTBenchStartup.rawFood;
            while (left > 0)
            {
                Thing food = ThingMaker.MakeThing(ThingDefOf.RawPotatoes);
                food.stackCount = Math.Min(left, food.def.stackLimit);
                left -= food.stackCount;
                GenPlace.TryPlaceThing(food, center, map, ThingPlaceMode.Near);
            }
            Log.Message("[RTBENCH] colony prepared: +" + RTBenchStartup.colonists + " colonists, " + RTBenchStartup.meals + " meals, " + RTBenchStartup.rawFood + " raw potatoes; colonists now " + map.mapPawns.FreeColonistsCount + ", pawns " + map.mapPawns.AllPawnsSpawnedCount);
        }

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
                PrepareColony(Find.CurrentMap);
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
                Log.Message($"[RTBENCH] RESULT ticks={tm.TicksGame - startTicks} seconds={span:F1} avg_tps={(tm.TicksGame - startTicks) / span:F1} avg_fps={frames / span:F1} colonists_end={Find.CurrentMap?.mapPawns.FreeColonistsCount} pawns_end={Find.CurrentMap?.mapPawns.AllPawnsSpawnedCount} incidents={RTBenchStartup.incidents} deaths={RTBenchStartup.deaths} colonist_deaths={RTBenchStartup.colonistDeaths}");
                Root.Shutdown();
            }
        }
    }
}
