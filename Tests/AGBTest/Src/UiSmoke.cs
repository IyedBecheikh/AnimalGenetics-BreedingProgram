using System;
using System.Collections.Generic;
using System.Linq;
using AnimalGenetics;
using AnimalGeneticsBreeding;
using RimWorld;
using UnityEngine;
using Verse;

namespace AGBTest;

// Opens every window / tab / menu the mod adds, in many states, and lets each draw for a few frames.
// Any error from our code during this is caught by ErrorWatch.
public static class UiSmoke
{
    private const int FramesPerStep = 6;
    private static List<(string name, Action open)> steps;
    private static int index = -1;
    private static int nextFrame;
    private static int errorsAtStart;
    public static bool Done;

    private static void CloseAll()
    {
        Find.WindowStack.TryRemove(typeof(Dialog_BreedingProgram), false);
        Find.WindowStack.TryRemove(typeof(Dialog_GeneSplice), false);
        Find.WindowStack.TryRemove(typeof(Dialog_ModSettings), false);
        Find.WindowStack.TryRemove(typeof(FloatMenu), false);
        Find.MainTabsRoot.EscapeCurrentTab(false);
        Find.Selector.ClearSelection();
    }

    private static void Select(Thing t, Type tab = null)
    {
        Find.Selector.ClearSelection();
        if (t is not { Spawned: true })
        {
            return;
        }

        Find.Selector.Select(t, false);
        Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Inspect, false);
        if (tab != null)
        {
            ((MainTabWindow_Inspect)MainButtonDefOf.Inspect.TabWindow).OpenTabType = tab;
        }
    }

    private static void Build()
    {
        var map = Find.CurrentMap;
        steps = [];
        var species = map.mapPawns.SpawnedColonyAnimals.Where(p => p.AnimalGenetics() != null).Select(p => p.def).Distinct().ToList();
        foreach (var def in species)
        {
            steps.Add(($"program herd tab: {def.defName}", () => Find.WindowStack.Add(new Dialog_BreedingProgram(def))));
            steps.Add(($"program pairings tab: {def.defName}", () => Find.WindowStack.Add(new Dialog_BreedingProgram(def, true))));
        }

        foreach (var tab in new[] { "AnimalGenetics", "Animals", "Wildlife" })
        {
            steps.Add(($"main tab {tab}", () => Find.MainTabsRoot.SetCurrentTab(DefDatabase<MainButtonDef>.GetNamed(tab), false)));
        }

        steps.Add(("research tab", () => Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Research, false)));
        steps.Add(("mod settings", () =>
        {
            BreedingSettingsMod.Settings.inbreedingEnabled = true;
            Find.WindowStack.Add(new Dialog_ModSettings(LoadedModManager.GetMod<BreedingSettingsMod>()));
        }));

        var animals = map.mapPawns.AllPawnsSpawned.Where(p => p.RaceProps.Animal && p.AnimalGenetics() != null).ToList();
        var pedigreeTargets = new List<Pawn>
        {
            animals.FirstOrDefault(p => p.Name is NameSingle { Name: "Inbred test calf" }),
            animals.FirstOrDefault(p => p.Name is NameSingle { Name: "AI calf" }),
            animals.FirstOrDefault(p => p.Name is NameSingle { Name: "Lineage hybrid" }),
            animals.FirstOrDefault(p => p.Faction == null),
            animals.FirstOrDefault(p => p.def.defName == "Chicken")
        };
        foreach (var p in pedigreeTargets.Where(p => p != null))
        {
            steps.Add(($"pedigree tab: {p.LabelShort}", () => Select(p, typeof(ITab_Pedigree))));
            steps.Add(($"inspect + gizmos: {p.LabelShort}", () => Select(p)));
        }

        var sample = map.listerThings.ThingsOfDef(AGB_DefOf.AGB_GermlineSample).FirstOrDefault();
        steps.Add(("inspect: germline sample", () => Select(sample)));
        var bank = map.listerThings.ThingsOfDef(AGB_DefOf.AGB_Cryobank).FirstOrDefault();
        steps.Add(("inspect: cryobank", () => Select(bank)));
        var genome = map.listerThings.AllThings.FirstOrDefault(t => t.TryGetComp<CompGenomeProfile>() != null);
        if (genome != null)
        {
            steps.Add(("inspect: genome", () => Select(genome)));
        }

        var herd = map.mapPawns.SpawnedColonyAnimals.Where(p => p.def.defName == "Muffalo").ToList();
        steps.Add(("role float menu (multi-select)", () => Find.WindowStack.Add(new FloatMenu(BreedingUi.RoleOptions(herd)))));
        if (bank != null)
        {
            steps.Add(("gene splice dialog (empty)", () => Find.WindowStack.Add(new Dialog_GeneSplice(bank.TryGetComp<CompGeneSplice>()))));
        }
    }

    public static void Update(Action<bool, string> check)
    {
        if (Done || Time.frameCount < nextFrame)
        {
            return;
        }

        if (steps == null)
        {
            Build();
            errorsAtStart = ErrorWatch.Captured.Count;
            ErrorWatch.RecordAll = true;
        }

        CloseAll();
        index++;
        if (index >= steps.Count)
        {
            BreedingSettingsMod.Settings.inbreedingEnabled = false;
            ErrorWatch.RecordAll = false;
            var active = string.Join(", ", ModsConfig.ActiveModsInLoadOrder.Select(m => m.Name));
            Log.Message("[AGBTEST] UI smoke mod list: " + active);
            foreach (var other in ErrorWatch.AllDuringUi.Distinct().Take(15))
            {
                Log.Message("[AGBTEST] INFO error/warning while our UI was open (any source): " + other);
            }

            check(true, $"UI smoke: {ErrorWatch.AllDuringUi.Count} errors/warnings from any mod while our screens were open (info)");
            var newErrors = ErrorWatch.Captured.Count - errorsAtStart;
            check(newErrors == 0, $"UI smoke: {steps.Count} windows/tabs/menus drew with {newErrors} errors from the mod");
            Done = true;
            return;
        }

        try
        {
            steps[index].open();
        }
        catch (Exception e)
        {
            check(false, $"UI smoke step '{steps[index].name}' threw: {e.Message}");
        }

        nextFrame = Time.frameCount + FramesPerStep;
    }
}
