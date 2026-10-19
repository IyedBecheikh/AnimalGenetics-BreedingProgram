using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AnimalGeneticsBreeding;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AGBTest;

// Measures GUI time per frame and pawn-table rebuilds with the Genetics / Wildlife / Animals tab open,
// with and without pawn churn, with PerfFixes on and off.
public static class PerfBench
{
    private const int WarmFrames = 15;
    private const int MeasureFrames = 120;

    private static readonly Stopwatch guiWatch = new();
    private static readonly Stopwatch rebuildWatch = new();
    private static long guiTicks;
    private static long rebuildTicks;
    private static int rebuilds;
    private static bool patched;

    private static readonly AccessTools.FieldRef<PawnTable, bool> dirtyField =
        AccessTools.FieldRefAccess<PawnTable, bool>("dirty");

    private static readonly List<(string tab, bool churn, bool fix)> cases = [];
    private static int caseIndex = -1;
    private static int frame;
    private static Pawn churnPawn;
    private static List<PawnKindDef> wildKinds;

    public static bool Done { get; private set; }

    private static int tracesLogged;

    // Diagnostic: who marks the table dirty while a fixed, churning case runs?
    public static void TableSetDirtyPrefix()
    {
        if (caseIndex < 0 || caseIndex >= cases.Count || frame < WarmFrames || tracesLogged >= 3)
        {
            return;
        }

        var c = cases[caseIndex];
        if (!c.fix || !c.churn)
        {
            return;
        }

        tracesLogged++;
        var trace = System.Environment.StackTrace.Split('\n').Skip(2).Take(12);
        Log.Message($"[AGBPERF] SetDirty during {c.tab} (fix on, churn):\n" + string.Join("\n", trace));
    }

    // Window contents run inside Unity's deferred GUI.Window callback, so time the window body itself.
    public static void GuiPrefix(Window __instance)
    {
        if (__instance is MainTabWindow_PawnTable)
        {
            guiWatch.Restart();
        }
    }

    public static void GuiPostfix(Window __instance)
    {
        if (__instance is MainTabWindow_PawnTable)
        {
            guiWatch.Stop();
            guiTicks += guiWatch.ElapsedTicks;
        }
    }

    public static void RecachePrefix(PawnTable __instance, out bool __state)
    {
        __state = dirtyField(__instance);
        if (__state)
        {
            TableSetDirtyPrefix();
            rebuilds++;
            rebuildWatch.Restart();
        }
    }

    public static void RecachePostfix(bool __state)
    {
        if (__state)
        {
            rebuildWatch.Stop();
            rebuildTicks += rebuildWatch.ElapsedTicks;
        }
    }

    private static void Setup()
    {
        var harmony = new Harmony("iyedm.agbtest.perf");
        harmony.Patch(AccessTools.Method(typeof(Window), "InnerWindowOnGUI"),
            new HarmonyMethod(typeof(PerfBench), nameof(GuiPrefix)),
            new HarmonyMethod(typeof(PerfBench), nameof(GuiPostfix)));
        harmony.Patch(AccessTools.Method(typeof(PawnTable), "RecacheIfDirty"),
            new HarmonyMethod(typeof(PerfBench), nameof(RecachePrefix)),
            new HarmonyMethod(typeof(PerfBench), nameof(RecachePostfix)));
        patched = true;
        harmony.Patch(AccessTools.Method(typeof(PawnTable), nameof(PawnTable.SetDirty)),
            new HarmonyMethod(typeof(PerfBench), nameof(TableSetDirtyPrefix)));
        DrawProfiler.Patch();

        // Heavier, realistic case: Animal Genetics' gene columns shown in the Wildlife tab too.
        global::AnimalGenetics.CoreMod.Instance.Settings.showGenesInWildlifeTab = true;
        global::AnimalGenetics.HarmonyPatches.AnimalGeneticsAssemblyLoader.PatchUI();

        var map = Find.CurrentMap;
        wildKinds = DefDatabase<PawnKindDef>.AllDefs
            .Where(k => k.RaceProps is { Animal: true } && !k.RaceProps.Dryad && k.race.tradeTags != null).ToList();
        for (var i = 0; i < 300; i++)
        {
            SpawnWild(map);
        }

        foreach (var tab in new[] { "AnimalGenetics", "Wildlife", "Animals" })
        {
            foreach (var churn in new[] { false, true })
            {
                foreach (var fix in new[] { false, true })
                {
                    cases.Add((tab, churn, fix));
                }
            }
        }
    }

    private static Pawn SpawnWild(Map map)
    {
        var pawn = PawnGenerator.GeneratePawn(wildKinds.RandomElement());
        return (Pawn)GenSpawn.Spawn(pawn, CellFinder.RandomCell(map), map);
    }

    /// <summary>Call once per frame. Returns true when the whole benchmark is finished.</summary>
    public static void Update(System.Action<string> report)
    {
        if (Done)
        {
            return;
        }

        if (!patched)
        {
            Setup();
            NextCase();
            return;
        }

        var map = Find.CurrentMap;
        var c = cases[caseIndex];
        if (c.churn)
        {
            churnPawn?.Destroy();
            churnPawn = SpawnWild(map);
        }

        frame++;
        if (frame == WarmFrames)
        {
            DrawProfiler.Reset();
            DrawProfiler.Active = c.fix && !c.churn;
            guiTicks = 0;
            rebuildTicks = 0;
            rebuilds = 0;
        }

        if (frame < WarmFrames + MeasureFrames)
        {
            return;
        }

        var ms = guiTicks * 1000.0 / Stopwatch.Frequency / MeasureFrames;
        var perRebuild = rebuilds == 0 ? 0 : rebuildTicks * 1000.0 / Stopwatch.Frequency / rebuilds;
        var rows = Find.CurrentMap.mapPawns.AllPawnsSpawned.Count(p => p.RaceProps.Animal);
        if (DrawProfiler.Active)
        {
            DrawProfiler.Active = false;
            foreach (var line in DrawProfiler.Report(c.tab, MeasureFrames))
            {
                report(line);
            }
        }

        report($"[AGBPERF] tab={c.tab,-14} churn={(c.churn ? "yes" : "no "),-3} fix={(c.fix ? "on " : "off")} " +
               $"tab_ms_per_frame={ms,7:F2} rebuilds_per_frame={rebuilds / (float)MeasureFrames:F2} " +
               $"ms_per_rebuild={perRebuild:F2} animals={rows}");
        NextCase();
    }

    private static void NextCase()
    {
        caseIndex++;
        frame = 0;
        if (caseIndex >= cases.Count)
        {
            PerfFixes.Enabled = true;
            Find.MainTabsRoot.EscapeCurrentTab(false);
            Done = true;
            return;
        }

        var c = cases[caseIndex];
        tracesLogged = c.tab == "Wildlife" ? 0 : 3;
        var openWindow = Find.WindowStack.Windows.OfType<MainTabWindow_PawnTable>().Select(w => w.GetType().FullName);
        Log.Message($"[AGBPERF] case {c.tab}: open pawn-table windows = {string.Join(", ", openWindow)}");
        PerfFixes.Enabled = c.fix;
        Find.MainTabsRoot.SetCurrentTab(DefDatabase<MainButtonDef>.GetNamed(c.tab), false);
    }
}
