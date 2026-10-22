using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AnimalGenetics;
using AnimalGeneticsBreeding;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AGBTest;

// Integration test harness: runs inside a -quicktest game, logs [AGBTEST] lines, saves, reloads, verifies, quits.
public class AGBTestComponent(Game game) : GameComponent
{
    private static readonly string Dir = GenFilePaths.SaveDataFolderPath;
    private static string Marker => Path.Combine(Dir, "agbtest_marker.txt");
    private static string Results => Path.Combine(Dir, "agbtest_results.txt");
    private bool started;
    private bool verifyPending;
    private static int fails;
    private static bool finished;
    private static int shotStage;
    private static Thing spliceBank;
    private static int shotFrame;

    // Screenshot pass: Breeding Program window, Genetics tab, selected animal. Runs per frame, not per tick.
    public override void GameComponentUpdate()
    {
        if (finished || shotStage == 0 || Time.frameCount < shotFrame)
        {
            return;
        }

        var dir = Path.Combine(Dir, "shots");
        Directory.CreateDirectory(dir);
        var muffalo = Find.CurrentMap.mapPawns.SpawnedColonyAnimals.Where(p => p.def.defName == "Muffalo").ToList();
        switch (shotStage)
        {
            case 1:
                Find.WindowStack.Add(new Dialog_BreedingProgram(muffalo[0].def));
                break;
            case 2:
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, "1_program.png"));
                break;
            case 3:
                Find.WindowStack.TryRemove(typeof(Dialog_BreedingProgram));
                Find.MainTabsRoot.SetCurrentTab(DefDatabase<MainButtonDef>.GetNamed("AnimalGenetics"));
                break;
            case 4:
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, "2_genetics_tab.png"));
                break;
            case 5:
                Find.MainTabsRoot.SetCurrentTab(DefDatabase<MainButtonDef>.GetNamed("Animals"));
                break;
            case 6:
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, "3_animals_tab.png"));
                break;
            case 7:
                Find.MainTabsRoot.EscapeCurrentTab(false);
                Find.Selector.ClearSelection();
                Find.Selector.Select(muffalo[0]);
                Find.CameraDriver.JumpToCurrentMapLoc(muffalo[0].Position);
                break;
            case 8:
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, "4_selected.png"));
                break;
            case 9:
                Find.Selector.ClearSelection();
                if (Phase2Tests.Grandchild != null)
                {
                    Find.Selector.Select(Phase2Tests.Grandchild);
                    Find.CameraDriver.JumpToCurrentMapLoc(Phase2Tests.Grandchild.Position);
                }

                Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Inspect, false);
                ((MainTabWindow_Inspect)MainButtonDefOf.Inspect.TabWindow).OpenTabType = typeof(ITab_Pedigree);
                break;
            case 10:
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, "5_pedigree.png"));
                break;
            case 11:
                Find.MainTabsRoot.EscapeCurrentTab(false);
                Find.Selector.ClearSelection();
                Find.WindowStack.Add(new Dialog_BreedingProgram(muffalo[0].def, true));
                break;
            case 12:
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, "6_pairings.png"));
                break;
            case 13:
                Find.WindowStack.TryRemove(typeof(Dialog_BreedingProgram));
                break;
            case 14:
            {
                // Clean close-up: a fresh cryobank with a sample in it, and a loose sample next to it.
                Find.Selector.ClearSelection();
                var map = Find.CurrentMap;
                var cell = CellFinder.RandomClosewalkCellNear(map.Center, map, 30, c =>
                    c.Standable(map) && GenRadial.RadialCellsAround(c, 3f, true)
                        .All(x => x.InBounds(map) && x.Standable(map) && x.GetFirstPawn(map) == null &&
                                  x.GetFirstItem(map) == null && x.GetFirstBuilding(map) == null));
                var bank = GenSpawn.Spawn(ThingMaker.MakeThing(AGB_DefOf.AGB_Cryobank), cell, map);
                bank.SetFaction(Faction.OfPlayer);
                bank.TryGetComp<CompPowerTrader>().PowerOn = true;
                var donor = Phase3Tests.Donor ?? muffalo[0];
                GenSpawn.Spawn(CompGermlineSample.MakeFrom(donor), cell, map);
                spliceBank = bank;
                var second = muffalo.FirstOrDefault(p => p.gender == Gender.Male && p != donor);
                if (second != null)
                {
                    GenSpawn.Spawn(CompGermlineSample.MakeFrom(second), cell, map);
                }

                var loose = GenSpawn.Spawn(CompGermlineSample.MakeFrom(donor), cell + IntVec3.East * 2, map);
                var stackCells = new[] { cell + IntVec3.South * 2, cell + IntVec3.South * 2 + IntVec3.East,
                    cell + IntVec3.South * 2 + IntVec3.East * 2 };
                var counts = new[] { 1, 6, 25 };
                for (var i = 0; i < 3; i++)
                {
                    var serum = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("AGB_SerumStabilizing"));
                    serum.stackCount = counts[i];
                    GenSpawn.Spawn(serum, stackCells[i], map);
                }
                Find.Selector.Select(loose);
                Find.CameraDriver.SetRootPosAndSize(cell.ToVector3Shifted() + new Vector3(1f, 0f, -1f), 4f);
                break;
            }
            case 15:
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, "7_sample.png"));
                break;
            case 16:
                Find.Selector.ClearSelection();
                var female = muffalo.FirstOrDefault(p => p.gender == Gender.Female && p.ageTracker.CurLifeStage.reproductive &&
                                                         p.HerdRole() != HerdRole.Cull);
                if (female != null)
                {
                    Find.Selector.Select(female);
                    Find.CameraDriver.JumpToCurrentMapLoc(female.Position);
                }

                break;
            case 17:
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, "8_female_gizmos.png"));
                break;
            case 18:
            {
                var comp = spliceBank?.TryGetComp<CompGeneSplice>();
                var stored = comp?.StoredSamples ?? [];
                if (comp != null && stored.Count >= 2)
                {
                    var dialog = new Dialog_GeneSplice(comp);
                    HarmonyLib.Traverse.Create(dialog).Field("target").SetValue(stored[0]);
                    HarmonyLib.Traverse.Create(dialog).Field("donor").SetValue(stored[1]);
                    HarmonyLib.Traverse.Create(dialog).Field("gene").SetValue(StatDefOf.MoveSpeed);
                    Find.WindowStack.Add(dialog);
                }

                break;
            }
            case 19:
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, "9_gene_splice.png"));
                break;
            case 20:
            {
                Find.WindowStack.TryRemove(typeof(Dialog_GeneSplice));
                Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Architect, false);
                var architect = (MainTabWindow_Architect)MainButtonDefOf.Architect.TabWindow;
                var panels = HarmonyLib.Traverse.Create(architect).Field("desPanelsCached").GetValue<List<ArchitectCategoryTab>>();
                var misc = panels?.FirstOrDefault(p => p.def == AGB_DefOf.AGB_Cryobank.designationCategory);
                if (misc != null)
                {
                    HarmonyLib.Traverse.Create(architect).Field("selectedDesPanel").SetValue(misc);
                }

                break;
            }
            case 23:
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, "10_build_menu.png"));
                break;
            case 24:
                Find.MainTabsRoot.EscapeCurrentTab(false);
                break;
            case 21:
            case 22:
                break; // let the Architect panel draw a few frames
            case 25:
                UiSmoke.Update(Check);
                if (!UiSmoke.Done)
                {
                    return; // stays in this stage until every step has drawn
                }

                break;
            case 26:
                if (!File.Exists(Path.Combine(Dir, "agbtest_perf.flag")))
                {
                    Finish();
                    return;
                }

                PerfBench.Update(line =>
                {
                    Log.Message(line);
                    File.AppendAllText(Results, line + "\n");
                });
                if (PerfBench.Done)
                {
                    Finish();
                }

                return; // stays in this stage, one benchmark step per frame
            default:
                Finish();
                return;
        }

        shotStage++;
        shotFrame = Time.frameCount + 30;
    }

    public override void LoadedGame()
    {
        if (File.Exists(Marker))
        {
            verifyPending = true;
        }
    }

    public override void GameComponentTick()
    {
        if (finished)
        {
            return;
        }

        try
        {
            if (verifyPending)
            {
                verifyPending = false;
                Verify();
                shotStage = 1;
                shotFrame = Time.frameCount + 30;
                return;
            }

            if (started && Phase3Tests.Active)
            {
                Phase3Tests.Tick();
                if (!Phase3Tests.Active)
                {
                    Phase45Tests.Start(Check, Find.CurrentMap, savedMales, savedFemales);
                }

                return;
            }

            if (started && Phase45Tests.Active)
            {
                Phase45Tests.Tick();
                if (!Phase45Tests.Active)
                {
                    ScenarioTests.Start(Check, Find.CurrentMap);
                }

                return;
            }

            if (started && ScenarioTests.Active)
            {
                ScenarioTests.Tick();
                if (!ScenarioTests.Active)
                {
                    SaveAndReload();
                }

                return;
            }

            if (started || File.Exists(Marker) || Find.TickManager.TicksGame < 120)
            {
                return;
            }

            started = true;
            RunTests();
        }
        catch (Exception e)
        {
            Check(false, "exception: " + e);
            Finish();
        }
    }

    private static void Check(bool ok, string what)
    {
        if (!ok)
        {
            fails++;
        }

        var line = $"[AGBTEST] {(ok ? "PASS" : "FAIL")} {what}";
        Log.Message(line);
        File.AppendAllText(Results, line + "\n");
    }

    private static void Finish()
    {
        finished = true;
        Check(ErrorWatch.Captured.Count == 0, $"no errors or warnings from the mod during the whole run ({ErrorWatch.Captured.Count})");
        foreach (var e in ErrorWatch.Captured.Distinct().Take(10))
        {
            File.AppendAllText(Results, "[AGBTEST]   logged: " + e.Replace("\n", " | ") + "\n");
        }

        File.AppendAllText(Results, $"[AGBTEST] DONE fails={fails}\n");
        if (File.Exists(Marker))
        {
            File.Delete(Marker);
        }

        Application.Quit();
    }

    private static Pawn Spawn(Map map, PawnKindDef kind, Gender gender, float age)
    {
        var request = new PawnGenerationRequest(kind, Faction.OfPlayer, PawnGenerationContext.NonPlayer,
            fixedGender: gender, fixedBiologicalAge: age, fixedChronologicalAge: age);
        var pawn = PawnGenerator.GeneratePawn(request);
        GenSpawn.Spawn(pawn, CellFinder.RandomClosewalkCellNear(map.Center, map, 8), map);
        return pawn;
    }

    private static List<Pawn> Slaughter(Map map)
    {
        map.autoSlaughterManager.Notify_ConfigChanged();
        return map.autoSlaughterManager.AnimalsToSlaughter.Where(p => p.def.defName == "Muffalo").ToList();
    }

    private static string Ids(IEnumerable<Pawn> pawns)
    {
        return string.Join(",", pawns.Select(p => p.thingIDNumber).OrderBy(i => i));
    }

    private static void RunTests()
    {
        File.WriteAllText(Results, "");
        var map = Find.CurrentMap;
        var kind = DefDatabase<PawnKindDef>.GetNamed("Muffalo");
        var def = kind.race;

        // Another mod patching ours before defs load must not break Animal Genetics (Quality Bionics Remastered did)
        Check(EarlyPatchSim.Patched > 50 && EarlyPatchSim.FirstFailure == null,
            $"early-patch simulation patched {EarlyPatchSim.Patched} methods (first failure: {EarlyPatchSim.FirstFailure ?? "none"})");
        var agOk = true;
        try
        {
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
                typeof(AnimalGenetics.HarmonyPatches.AnimalGeneticsAssemblyLoader).TypeHandle);
            agOk = Constants.AffectedStats.Count == 9 && Constants.AffectedStats.All(s => s != null) &&
                   Constants.AffectedStats.Contains(GrowthGene.Stat);
        }
        catch (Exception)
        {
            agOk = false;
        }

        Check(agOk, "Animal Genetics' Constants and loader initialized after early patching");

        // Defs, textures, columns
        Check(DefDatabase<PawnTableDef>.GetNamed("Animals").columns.Any(c => c.defName == "AGB_BreedingScore"),
            "Animals tab has score column");
        Check(DefDatabase<PawnTableDef>.GetNamed("Genetics").columns.Any(c => c.defName == "AGB_HerdRole"),
            "Genetics tab has role column");
        Check(Textures.RoleBreeder != null && Textures.RoleCull != null && Textures.RoleKeep != null &&
              Textures.RoleNone != null, "role textures load");

        GrowthTests.Run(Check, map);

        // Clear any existing muffalo so counts are deterministic
        foreach (var p in map.mapPawns.SpawnedColonyAnimals.Where(p => p.def == def).ToList())
        {
            p.Destroy();
        }

        var males = Enumerable.Range(0, 4).Select(i => Spawn(map, kind, Gender.Male, 4f + i)).ToList();
        var females = Enumerable.Range(0, 4).Select(i => Spawn(map, kind, Gender.Female, 4f + i)).ToList();
        Check(males.All(BreedingUtility.IsManagedAnimal), "spawned muffalo are managed animals");
        Check(males.All(m => m.Breeding() != null), "CompBreeding present");

        var scores = males.Concat(females).Select(p => p.BreedingScoreOf()).ToList();
        Check(scores.Distinct().Count() > 1, $"scores vary: {string.Join(" ", scores.Select(BreedingScore.Format))}");
        Log.Message("[AGBTEST] score explain sample:\n" + BreedingUi.ExplainScore(males[0]));

        // Mating: default open
        Check(males.Concat(females).All(BreedingRules.CanMate), "open mode: everyone can mate");
        females[0].Breeding().SetRole(HerdRole.Cull);
        Check(!BreedingRules.CanMate(females[0]), "cull female can't mate");
        Check(!PawnUtility.FertileMateTarget(males[0], females[0]), "FertileMateTarget blocks cull female");
        Check(PawnUtility.FertileMateTarget(males[0], females[1]), "FertileMateTarget allows normal female");

        // Mating: best males
        var program = BreedingProgram.GetOrCreate(def);
        program.matingMode = MatingMode.BestMales;
        program.bestMalesCount = 1;
        BreedingProgram.Notify_RolesChanged();
        var byScore = males.OrderByDescending(m => m.BreedingScoreOf()).ToList();
        Check(BreedingRules.CanMate(byScore[0]) && BreedingRules.IsTopMale(byScore[0]), "best male can mate");
        Check(!BreedingRules.CanMate(byScore[1]) && !BreedingRules.CanMate(byScore[3]), "other males can't mate");
        byScore[3].Breeding().SetRole(HerdRole.Breeder);
        Check(BreedingRules.CanMate(byScore[3]), "breeder-tagged worst male can mate");

        var giver = new JobGiver_Mate();
        var tryGive = typeof(JobGiver_Mate).GetMethod("TryGiveJob",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Check(tryGive.Invoke(giver, [byScore[1]]) == null, "JobGiver_Mate returns no job for blocked male");

        // Auto-slaughter: protected roles + top male
        var config = map.autoSlaughterManager.configs.First(c => c.animal == def);
        config.maxMales = 2;
        var chosen = Slaughter(map);
        var expected = new[] { byScore[1], byScore[2] };
        Check(Ids(chosen) == Ids(expected),
            $"slaughter skips top male + breeder (got {Ids(chosen)}, want {Ids(expected)})");
        Check(!chosen.Contains(females[0]), "cull female not slaughtered when slaughterCulled off");
        program.slaughterCulled = true;
        Check(Slaughter(map).Contains(females[0]), "cull female slaughtered when slaughterCulled on");

        // Gene-aware ordering vs vanilla ordering
        program.matingMode = MatingMode.Open;
        program.slaughterCulled = false;
        foreach (var p in males.Concat(females))
        {
            p.Breeding().SetRole(HerdRole.None);
        }

        chosen = Slaughter(map);
        Check(Ids(chosen) == Ids(byScore.Skip(2)), $"gene-aware: two worst males go (got {Ids(chosen)})");
        program.geneAwareSlaughter = false;
        chosen = Slaughter(map);
        Check(Ids(chosen) == Ids(males.OrderByDescending(m => m.ageTracker.AgeBiologicalTicks).Take(2)),
            $"vanilla order: two oldest males go (got {Ids(chosen)})");
        program.geneAwareSlaughter = true;

        males[0].Breeding().SetRole(HerdRole.Keep);
        chosen = Slaughter(map);
        Check(!chosen.Contains(males[0]), "keep-tagged male never slaughtered");

        program.protectTopPerSex = 4;
        Check(Slaughter(map).Count == 0, "protect top 4 per sex protects all males");
        program.protectTopPerSex = 0;

        // Below-score culling
        config.maxMales = -1;
        var femaleScores = females.Select(f => f.BreedingScoreOf()).OrderBy(s => s).ToList();
        program.slaughterBelowScore = true;
        program.minScore = (femaleScores[1] + femaleScores[2]) / 2f;
        chosen = Slaughter(map);
        var belowFemales = females.Where(f => f.BreedingScoreOf() < program.minScore).ToList();
        Check(belowFemales.All(chosen.Contains) && belowFemales.Count == 2, "below-score females slaughtered");
        program.slaughterBelowScore = false;

        // Weights change the score
        var before = males[1].BreedingScoreOf();
        program.ApplyPreset(BreedingPresets.All.First(p => p.Key == "PackAnimal"));
        var after = males[1].BreedingScoreOf();
        Log.Message($"[AGBTEST] preset PackAnimal score {before:F3} -> {after:F3}");

        Phase2Tests.Run(Check, males, females, map);
        savedMales = males;
        savedFemales = females;
        Phase3Tests.Start(Check, map, males, females);
    }

    private static List<Pawn> savedMales, savedFemales;

    private static void SaveAndReload()
    {
        var males = savedMales;
        var females = savedFemales;
        // Persistence setup
        BreedingProgram.GetOrCreate(DefDatabase<ThingDef>.GetNamed("Muffalo")).SetWeight(DefDatabase<StatDef>.GetNamed("MoveSpeed"), 4f);
        BreedingProgram.GetOrCreate(DefDatabase<ThingDef>.GetNamed("Muffalo")).matingMode = MatingMode.BestMales;
        BreedingProgram.GetOrCreate(DefDatabase<ThingDef>.GetNamed("Muffalo")).bestMalesCount = 2;
        males[0].Breeding().SetRole(HerdRole.Keep);
        females[0].Breeding().SetRole(HerdRole.Cull);
        females[1].Breeding().SetRole(HerdRole.Breeder);
        File.WriteAllText(Marker, $"{males[0].thingIDNumber},{females[0].thingIDNumber},{females[1].thingIDNumber}," + $"{Phase2Tests.Grandchild?.thingIDNumber ?? -1},{Phase3Tests.Donor?.thingIDNumber ?? -1},{Phase45Tests.Hybrid?.thingIDNumber ?? -1}");
        GameDataSaveLoader.SaveGame("agbtest");
        Check(true, "saved");
        GameDataSaveLoader.LoadGame("agbtest");
    }

    private static void Verify()
    {
        var ids = File.ReadAllText(Marker).Split(',').Select(int.Parse).ToList();
        var animals = Find.CurrentMap.mapPawns.SpawnedColonyAnimals;
        HerdRole RoleOf(int id) => animals.First(p => p.thingIDNumber == id).HerdRole();
        Check(RoleOf(ids[0]) == HerdRole.Keep && RoleOf(ids[1]) == HerdRole.Cull && RoleOf(ids[2]) == HerdRole.Breeder,
            "roles survive save/load");
        var grandchild = animals.FirstOrDefault(p => p.thingIDNumber == ids[3]);
        Check(grandchild != null && Mathf.Abs(Kinship.Inbreeding(grandchild.AnimalGenetics()) - 0.25f) < 0.001f,
            "pedigree (F = 0.25) survives save/load");
        Phase2Tests.Grandchild = grandchild;
        var program = BreedingProgram.Get(DefDatabase<ThingDef>.GetNamed("Muffalo"));
        Check(program.matingMode == MatingMode.BestMales && program.bestMalesCount == 2, "mating settings survive");
        Check(program.presetKey == BreedingPresets.Custom &&
              program.GetWeight(DefDatabase<StatDef>.GetNamed("MoveSpeed")) == 4f &&
              program.GetWeight(DefDatabase<StatDef>.GetNamed("CarryingCapacity")) == 3f, "weights survive");
        Check(Find.CurrentMap.autoSlaughterManager.AnimalsToSlaughter != null, "slaughter list after load");
        var donor = Find.CurrentMap.mapPawns.AllPawns.FirstOrDefault(p => p.thingIDNumber == ids[4]);
        var samples = Find.CurrentMap.listerThings.ThingsOfDef(AGB_DefOf.AGB_GermlineSample)
            .Select(t => t.TryGetComp<CompGermlineSample>()).ToList();
        var living = Find.CurrentMap.mapPawns.AllPawns.Select(p => p.AnimalGenetics()).Where(g => g != null).ToHashSet();
        Check(samples.Count > 0 && samples.All(c => c.genetics?.GeneRecords != null && c.species != null),
            $"every germline sample keeps its genetics and species after reload ({samples.Count} samples)");
        Check(samples.Any(c => living.Contains(c.genetics)), "samples still link to their living donors after reload");
        Check(samples.Any(c => c.Preserved), "cryobank still preserves after reload");
        Phase3Tests.KeptSample = Find.CurrentMap.listerThings.ThingsOfDef(AGB_DefOf.AGB_GermlineSample).FirstOrDefault();
        Phase3Tests.Donor = donor;
        Check(samples.Any(c => c.donorName != null && c.donorName.Contains("spliced")), "spliced sample survives reload");
        if (VgeBridge.Active)
        {
            var hybrid = Find.CurrentMap.mapPawns.AllPawns.FirstOrDefault(p => p.thingIDNumber == ids[5]);
            Check(hybrid?.AnimalGenetics()?.Mother != null && hybrid.AnimalGenetics().Father != null,
                "VGE: hybrid lineage survives reload");
        }
    }
}
