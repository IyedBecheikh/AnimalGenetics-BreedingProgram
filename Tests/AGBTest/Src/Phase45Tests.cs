using System;
using System.Collections.Generic;
using System.Linq;
using AnimalGenetics;
using AnimalGeneticsBreeding;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AGBTest;

// Serums, gene splice, trading, and (when VGE is loaded) the genome → hybrid bridge.
public static class Phase45Tests
{
    private const int StageTimeoutTicks = 8000;

    public static bool Active;
    public static Pawn Hybrid;

    private static Action<bool, string> check;
    private static Map map;
    private static List<Pawn> males, females;
    private static Pawn handler, doctor, mother;
    private static Thing serum, bank, spliceTarget, spliceDonor;
    private static Dictionary<StatDef, float> targetBefore;
    private static float donorSpeed;
    private static int stage, stageStart;

    public static void Start(Action<bool, string> checkFn, Map m, List<Pawn> ms, List<Pawn> fs)
    {
        check = checkFn;
        map = m;
        males = ms;
        females = fs;
        Active = true;
        stage = 0;
    }

    private static void Next()
    {
        stage++;
        stageStart = Find.TickManager.TicksGame;
    }

    private static bool TimedOut(string what)
    {
        if (Find.TickManager.TicksGame - stageStart < StageTimeoutTicks)
        {
            return false;
        }

        check(false, "timed out: " + what);
        Finish();
        return true;
    }

    private static void Finish()
    {
        Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
        Active = false;
    }

    public static void Tick()
    {
        try
        {
            switch (stage)
            {
                case 0:
                    Find.ResearchManager.FinishProject(AGB_DefOf.AGB_GermlineTuning);
                    check(GermlineUtility.TuningUnlocked, "germline tuning research can be completed");
                    ResearchPlacement();
                    SerumStatistics();
                    StartSerumJob();
                    Next();
                    break;
                case 1:
                    if (mother.health.hediffSet.HasHediff(DefDatabase<HediffDef>.GetNamed("AGB_SerumDominance")))
                    {
                        CheckSerumJob();
                        StartSplice();
                        Next();
                    }
                    else
                    {
                        Phase3Tests.KeepWorking(handler, AGB_DefOf.AGB_ApplySerum, () =>
                        {
                            var g = (WorkGiver_Germline)DefDatabase<WorkGiverDef>.GetNamed("AGB_Germline").Worker;
                            return g.HasJobOnThing(handler, mother, true) ? g.JobOnThing(handler, mother, true) : null;
                        }, Find.TickManager.TicksGame - stageStart);
                        TimedOut("apply serum job");
                    }

                    break;
                case 2:
                    if (spliceTarget.Destroyed && spliceDonor.Destroyed)
                    {
                        CheckSplice();
                        Trading();
                        if (VgeBridge.Active)
                        {
                            VgeChain();
                        }

                        Finish();
                    }
                    else
                    {
                        Phase3Tests.KeepWorking(doctor, AGB_DefOf.AGB_GeneSplice, () =>
                        {
                            var g = (WorkGiver_GeneSplice)DefDatabase<WorkGiverDef>.GetNamed("AGB_GeneSplice").Worker;
                            return g.HasJobOnThing(doctor, bank, true) ? g.JobOnThing(doctor, bank, true) : null;
                        }, Find.TickManager.TicksGame - stageStart);
                        TimedOut("gene splice job");
                    }

                    break;
            }
        }
        catch (Exception e)
        {
            check(false, "phase 4/5 exception: " + e);
            Finish();
        }
    }

    // ---------------------------------------------------------------- research placement

    private static void ResearchPlacement()
    {
        var banking = AGB_DefOf.AGB_GermlineBanking;
        if (VgeBridge.Active)
        {
            check(banking.tab.defName == "GR_GeneticReseach" && AGB_DefOf.AGB_GermlineTuning.tab == banking.tab,
                "VGE: research moved to VGE's genetics tab");
            check(banking.prerequisites.Any(p => p.defName == "GR_GeneticAlteration"),
                "VGE: germline banking requires Genetic Alteration");
            check(DefDatabase<ResearchTabDef>.GetNamedSilentFail("AGB_Breeding") == null, "VGE: own research tab removed");
        }
        else
        {
            check(banking.tab.defName == "AGB_Breeding", "no VGE: research on own Breeding tab");
        }
    }

    // ---------------------------------------------------------------- serum effects (statistical)

    private static GeneticInformation CloneOf(GeneticInformation gi, StatDef shifted = null, float shift = 0f)
    {
        var records = gi.GeneRecords.ToDictionary(kv => kv.Key, kv => new GeneRecord { Value = kv.Value.Value });
        if (shifted != null)
        {
            records[shifted].Value += shift;
        }

        return new GeneticInformation(records);
    }

    private static (float mean, float sd) Children(GeneticInformation m, GeneticInformation f, StatDef stat, int n = 4000)
    {
        float sum = 0f, sumSq = 0f;
        for (var i = 0; i < n; i++)
        {
            var v = GeneticCalculator.GenerateGenesRecord(m, f)[stat].Value;
            sum += v;
            sumSq += v * v;
        }

        var mean = sum / n;
        return (mean, Mathf.Sqrt(Mathf.Max((sumSq / n) - (mean * mean), 0f)));
    }

    private static void ClearSerums(Pawn p)
    {
        foreach (var h in p.health.hediffSet.hediffs.Where(h => h.TryGetComp<HediffComp_Serum>() != null).ToList())
        {
            p.health.RemoveHediff(h);
        }
    }

    private static void SerumStatistics()
    {
        mother = females.First(f => f.HerdRole() != HerdRole.Cull && !f.Dead && f.Spawned);
        ClearSerums(mother);
        var mgi = mother.AnimalGenetics();
        var health = AnimalGenetics.AnimalGenetics.Health;
        var speed = StatDefOf.MoveSpeed;
        var settings = CoreMod.Instance.Settings;
        var sameFather = CloneOf(mgi);
        var originalSpread = settings.mutationStdDev;

        var baseline = Children(mgi, sameFather, health);
        check(Mathf.Abs(baseline.sd - settings.mutationStdDev) < 0.004f, $"baseline mutation spread ≈ {settings.mutationStdDev:F3} (got {baseline.sd:F4})");

        HediffComp_Serum.Apply(mother, DefDatabase<ThingDef>.GetNamed("AGB_SerumStabilizing"), null);
        var stable = Children(mgi, sameFather, health);
        check(Mathf.Abs(stable.sd - (settings.mutationStdDev * 0.25f)) < 0.002f, $"stabilizing serum: spread x0.25 (got {stable.sd:F4})");
        check(settings.mutationStdDev == originalSpread, "Animal Genetics mutation setting restored after each roll");
        ClearSerums(mother);

        HediffComp_Serum.Apply(mother, DefDatabase<ThingDef>.GetNamed("AGB_SerumMutagenic"), null);
        var wild = Children(mgi, sameFather, health);
        check(Mathf.Abs(wild.sd - (settings.mutationStdDev * 3f)) < 0.01f, $"mutagenic serum: spread x3 (got {wild.sd:F4})");
        ClearSerums(mother);
        check(Mathf.Abs(Children(mgi, sameFather, health).sd - settings.mutationStdDev) < 0.004f, "spread back to normal once serum removed");

        // Dominance: father is 30 points faster; without serum the child averages between, with it always the better.
        var fastFather = CloneOf(mgi, speed, 0.3f);
        var m = mgi.GeneRecords[speed].Value;
        var plain = Children(mgi, fastFather, speed).mean;
        var expectedPlain = (settings.bestGeneChance * (m + 0.3f)) + ((1f - settings.bestGeneChance) * m) + settings.mutationMean;
        check(Mathf.Abs(plain - expectedPlain) < 0.01f, $"no serum: speed child mean ≈ {expectedPlain:F3} (got {plain:F3})");
        HediffComp_Serum.Apply(mother, DefDatabase<ThingDef>.GetNamed("AGB_SerumDominance"), speed);
        var dominant = Children(mgi, fastFather, speed).mean;
        check(Mathf.Abs(dominant - (m + 0.3f + settings.mutationMean)) < 0.006f,
            $"dominance serum (speed): child mean ≈ better parent {m + 0.3f + settings.mutationMean:F3} (got {dominant:F3})");
        var otherGene = Children(mgi, sameFather, health);
        check(Mathf.Abs(otherGene.sd - settings.mutationStdDev) < 0.004f, "dominance serum leaves other genes alone");

        // Mammal births use the serum up.
        Hediff_Pregnant.DoBirthSpawn(mother, null);
        check(!mother.health.hediffSet.hediffs.Any(h => h.TryGetComp<HediffComp_Serum>() != null), "birth uses up the mother's serum");
    }

    // ---------------------------------------------------------------- serum job

    private static void StartSerumJob()
    {
        handler = map.mapPawns.FreeColonistsSpawned.First(c => !c.WorkTypeIsDisabled(WorkTypeDefOf.Handling));
        handler.skills.GetSkill(SkillDefOf.Animals).Level = 20;
        Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;

        var gizmos = mother.Breeding().CompGetGizmosExtra().ToList();
        check(gizmos.Any(g => g is Command_ApplySerum), "female shows Apply serum gizmo once tuning is researched");

        serum = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("AGB_SerumDominance"));
        serum.stackCount = 2;
        GenSpawn.Spawn(serum, mother.Position, map);
        mother.Breeding().SetOrder(GermlineOrder.ApplySerum, serum, StatDefOf.MoveSpeed.defName);
        handler.Position = mother.Position;
        handler.Notify_Teleported();
        var giver = (WorkGiver_Germline)DefDatabase<WorkGiverDef>.GetNamed("AGB_Germline").Worker;
        var ok = giver.HasJobOnThing(handler, mother, true);
        check(ok, "work giver offers the apply-serum job");
        if (ok)
        {
            handler.jobs.StartJob(giver.JobOnThing(handler, mother, true), JobCondition.InterruptForced);
        }
    }

    private static void CheckSerumJob()
    {
        var hediff = mother.health.hediffSet.GetFirstHediffOfDef(DefDatabase<HediffDef>.GetNamed("AGB_SerumDominance"));
        check(hediff.TryGetComp<HediffComp_Serum>().Gene == StatDefOf.MoveSpeed, "applied dominance serum targets the chosen gene (speed)");
        check(!serum.Destroyed && serum.stackCount == 1 || map.listerThings.ThingsOfDef(serum.def).Sum(t => t.stackCount) == 1,
            "exactly one serum dose used");
        check(mother.Breeding().Order == GermlineOrder.None, "serum order cleared");
        Log.Message("[AGBTEST] serum hediff label: " + hediff.LabelCap);
    }

    // ---------------------------------------------------------------- gene splice

    private static void StartSplice()
    {
        doctor = map.mapPawns.FreeColonistsSpawned.First(c => !c.WorkTypeIsDisabled(WorkTypeDefOf.Doctor));
        var cell = CellFinder.RandomClosewalkCellNear(map.Center, map, 10,
            c => c.Standable(map) && c.GetFirstBuilding(map) == null && c.GetFirstItem(map) == null && c.GetFirstPawn(map) == null);
        bank = GenSpawn.Spawn(ThingMaker.MakeThing(AGB_DefOf.AGB_Cryobank), cell, map, WipeMode.Vanish);
        bank.SetFaction(Faction.OfPlayer);
        bank.TryGetComp<CompPowerTrader>().PowerOn = true;

        var a = males.First(p => !p.Dead);
        var b = males.Last(p => !p.Dead && p != a);
        spliceTarget = GenSpawn.Spawn(CompGermlineSample.MakeFrom(a), cell, map);
        spliceDonor = GenSpawn.Spawn(CompGermlineSample.MakeFrom(b), cell, map);
        var comp = bank.TryGetComp<CompGeneSplice>();
        check(comp.StoredSamples.Count == 2, "both samples stored in the cryobank");
        check(comp.CompGetGizmosExtra().OfType<Command_Action>().Any(g => !g.Disabled), "cryobank shows an enabled Gene splice gizmo");
        targetBefore = a.AnimalGenetics().GeneRecords.ToDictionary(kv => kv.Key, kv => kv.Value.Value);
        donorSpeed = b.AnimalGenetics().GeneRecords[StatDefOf.MoveSpeed].Value;
        comp.target = spliceTarget;
        comp.donor = spliceDonor;
        comp.gene = StatDefOf.MoveSpeed.defName;

        doctor.Position = cell;
        doctor.Notify_Teleported();
        var giver = (WorkGiver_GeneSplice)DefDatabase<WorkGiverDef>.GetNamed("AGB_GeneSplice").Worker;
        var ok = giver.HasJobOnThing(doctor, bank, true);
        check(ok, "work giver offers the gene splice job");
        if (ok)
        {
            doctor.jobs.StartJob(giver.JobOnThing(doctor, bank, true), JobCondition.InterruptForced);
        }
    }

    private static void GraphicsChecks()
    {
        var inBank = bank.TryGetComp<CompGeneSplice>().StoredSamples.FirstOrDefault();
        check(inBank?.TryGetComp<CompGermlineSample>().DontDrawParent() == true, "samples inside a cryobank are not drawn");
        var loose = GenSpawn.Spawn(CompGermlineSample.MakeFrom(males.First(p => !p.Dead)), map.Center, map);
        check(!loose.TryGetComp<CompGermlineSample>().DontDrawParent(), "samples on the floor are drawn");
        foreach (var name in new[] { "AGB_SerumDominance", "AGB_SerumStabilizing", "AGB_SerumMutagenic" })
        {
            var def = DefDatabase<ThingDef>.GetNamed(name);
            var g = def.graphic as Graphic_StackCount;
            check(g != null && g.SubGraphicForStackCount(1, def) != g.SubGraphicForStackCount(5, def) &&
                  g.SubGraphicForStackCount(5, def) != g.SubGraphicForStackCount(def.stackLimit, def),
                $"{def.label}: separate art for 1 / few / full stack");
        }

        var cryo = AGB_DefOf.AGB_Cryobank;
        check(cryo.uiIcon != null && cryo.uiIcon.name.Contains("MenuIcon"), "cryobank uses its padded build-menu icon");
    }

    private static void CheckSplice()
    {
        var result = bank.TryGetComp<CompGeneSplice>().StoredSamples.SingleOrDefault();
        check(result != null, "splice leaves exactly one new sample in the cryobank");
        if (result == null)
        {
            return;
        }

        var gi = result.TryGetComp<CompGermlineSample>().genetics;
        var expectedSpeed = targetBefore[StatDefOf.MoveSpeed] + (0.75f * (donorSpeed - targetBefore[StatDefOf.MoveSpeed]));
        check(Mathf.Abs(gi.GeneRecords[StatDefOf.MoveSpeed].Value - expectedSpeed) < 1e-4f,
            $"spliced speed moved 75% toward donor ({targetBefore[StatDefOf.MoveSpeed]:F3} → {gi.GeneRecords[StatDefOf.MoveSpeed].Value:F3}, donor {donorSpeed:F3})");
        check(targetBefore.Where(kv => kv.Key != StatDefOf.MoveSpeed).All(kv => Mathf.Abs(gi.GeneRecords[kv.Key].Value - kv.Value) < 1e-5f),
            "all other genes equal the target's");
        check(bank.TryGetComp<CompGeneSplice>().Pending == false, "splice order cleared");
        GraphicsChecks();
        Log.Message("[AGBTEST] spliced sample: " + result.LabelShort);
        result.SetForbidden(true); // keep it for the save/reload check
        check(Recipe_Inseminate.BestSampleFor(mother) != result, "forbidden samples are never picked for insemination bills");
    }

    // ---------------------------------------------------------------- trading & value

    private static void Trading()
    {
        var gen = new StockGenerator_GermlineSamples { countRange = new IntRange(5, 5) };
        var stock = gen.GenerateThings(PlanetTile.Invalid).ToList();
        check(stock.Count == 5 && stock.All(t => t.TryGetComp<CompGermlineSample>() is { genetics: not null, species: not null }),
            "trader stock: 5 samples with genetics and species");
        check(gen.HandlesThingDef(AGB_DefOf.AGB_GermlineSample), "trader deals in germline samples (buys and sells)");
        var best = stock.OrderByDescending(t => t.TryGetComp<CompGermlineSample>().Score).First();
        var worst = stock.OrderBy(t => t.TryGetComp<CompGermlineSample>().Score).First();
        check(best.MarketValue >= worst.MarketValue, $"better genes sell for more ({worst.MarketValue:F0} → {best.MarketValue:F0} silver)");
        var expected = AGB_DefOf.AGB_GermlineSample.BaseMarketValue * StatPart_SampleGenes.Factor(best);
        check(Mathf.Abs(best.MarketValue - expected) < 0.5f, $"sample value = base x gene factor ({best.MarketValue:F1} vs {expected:F1})");

        if (VgeBridge.Active)
        {
            var trader = DefDatabase<TraderKindDef>.GetNamed("GR_Outlander_CaravanTrader");
            check(trader.stockGenerators.Any(g => g is StockGenerator_GermlineSamples), "VGE: genetics trader stocks germline samples");
            check(trader.WillTrade(AGB_DefOf.AGB_GermlineSample), "VGE: genetics trader will trade samples");
        }
    }

    // ---------------------------------------------------------------- VGE: genome → growth cell → womb → hybrid

    private static void VgeChain()
    {
        // 1. Genome from an extractor bill remembers the corpse.
        var muffaloDef = DefDatabase<ThingDef>.GetNamed("Muffalo");
        var victim = PawnGenerator.GeneratePawn(new PawnGenerationRequest(DefDatabase<PawnKindDef>.GetNamed("Muffalo"), Faction.OfPlayer));
        GenSpawn.Spawn(victim, CellFinder.RandomClosewalkCellNear(map.Center, map, 8), map);
        var victimGi = victim.AnimalGenetics();
        victim.Kill(null);
        var corpse = victim.Corpse;
        var recipe = DefDatabase<RecipeDef>.AllDefs.FirstOrDefault(r =>
            r.products.Any(p => p.thingDef.HasComp(typeof(CompGenomeProfile))) &&
            r.ingredients.Any(i => i.filter.Allows(muffaloDef.race.corpseDef)));
        check(recipe != null, $"VGE: found a genome extraction recipe for muffalo corpses ({recipe?.defName})");
        Thing genomeA = null;
        if (recipe != null && corpse != null)
        {
            var products = GenRecipe.MakeRecipeProducts(recipe, handler, [corpse], corpse, null).ToList();
            genomeA = products.FirstOrDefault(p => p.TryGetComp<CompGenomeProfile>() != null);
            check(genomeA?.TryGetComp<CompGenomeProfile>().genetics == victimGi,
                $"VGE: extracted genome carries the corpse's genetics ({genomeA?.LabelShort})");
        }

        // Second genome: tag one directly from a living animal.
        var otherGenomeDef = DefDatabase<ThingDef>.AllDefs.First(d => d.HasComp(typeof(CompGenomeProfile)) && d != genomeA?.def);
        var genomeB = ThingMaker.MakeThing(otherGenomeDef);
        var sourceB = females.First(f => !f.Dead);
        genomeB.TryGetComp<CompGenomeProfile>().SetFrom(sourceB);
        genomeA ??= ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("GR_MuffaloGenetic"));
        check(!genomeA.CanStackWith(genomeB), "VGE: genomes from different animals don't stack");

        // 2. Genomorpher: growth cell gets the lineage.
        var genomorpherType = AccessTools.TypeByName("GeneticRim.CompGenomorpher");
        var podCell = CellFinder.RandomClosewalkCellNear(map.Center, map, 20, c =>
            GenAdj.OccupiedRect(c, Rot4.North, new IntVec2(5, 5)).All(x => x.InBounds(map) && x.Standable(map) && x.GetFirstPawn(map) == null));
        var pod = GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("GR_GenePod")), podCell, map, WipeMode.Vanish);
        pod.SetFaction(Faction.OfPlayer);
        var morph = ((ThingWithComps)pod).AllComps.First(c => genomorpherType.IsInstanceOfType(c));
        var frame = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("GR_GenoframeNormal"));
        AccessTools.Method(genomorpherType, "Initialize",
            [typeof(Thing), typeof(Thing), typeof(Thing), typeof(Thing), typeof(int), typeof(PawnKindDef)]).Invoke(morph,
            [genomeA, genomeB, frame, null, 100, DefDatabase<PawnKindDef>.GetNamed("Muffalo")]);
        AccessTools.Method(genomorpherType, "StartGrowthProcess").Invoke(morph, null);
        var cell = (Thing)AccessTools.Field(genomorpherType, "growthCell").GetValue(morph);
        var cellLineage = cell?.TryGetComp<CompHybridLineage>();
        check(cellLineage?.dominant == genomeA.TryGetComp<CompGenomeProfile>().genetics &&
              cellLineage?.secondary == sourceB.AnimalGenetics(), "VGE: growth cell carries both genomes' genetics");

        // 3. Electrowomb: lineage copied on insertion, hybrid inherits it at birth.
        var wombType = AccessTools.TypeByName("GeneticRim.CompElectroWomb");
        var wombCell = CellFinder.RandomClosewalkCellNear(map.Center, map, 12, c =>
            c.Standable(map) && c.GetFirstBuilding(map) == null && c.GetFirstPawn(map) == null);
        var wombThing = GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("GR_ElectroWomb")), wombCell, map, WipeMode.Vanish);
        wombThing.SetFaction(Faction.OfPlayer);
        wombThing.TryGetComp<CompPowerTrader>().PowerOn = true;
        var womb = ((ThingWithComps)wombThing).AllComps.First(c => wombType.IsInstanceOfType(c));
        if (cell != null)
        {
            AccessTools.Method(wombType, "InitProcess").Invoke(womb, [cell]);
        }

        var wombLineage = wombThing.TryGetComp<CompHybridLineage>();
        check(wombLineage is { Any: true }, "VGE: electrowomb took the growth cell's lineage");
        AccessTools.Field(wombType, "progress").SetValue(womb, 2f);
        var before = map.mapPawns.AllPawnsSpawned.ToHashSet();
        womb.CompTick();
        Hybrid = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => !before.Contains(p));
        check(Hybrid != null, $"VGE: electrowomb produced {Hybrid?.KindLabel ?? "nothing"}");
        if (Hybrid != null)
        {
            var hg = Hybrid.AnimalGenetics();
            check(hg?.Mother == genomeA.TryGetComp<CompGenomeProfile>().genetics && hg?.Father == sourceB.AnimalGenetics(),
                "VGE: hybrid's genetics descend from the two genome sources");
            var dom = hg.Mother.GeneRecords;
            var sec = hg.Father.GeneRecords;
            var within = hg.GeneRecords.Where(kv => dom.ContainsKey(kv.Key) && sec.ContainsKey(kv.Key))
                .All(kv => kv.Value.Value >= Mathf.Min(dom[kv.Key].Value, sec[kv.Key].Value) - 0.3f &&
                           kv.Value.Value <= Mathf.Max(dom[kv.Key].Value, sec[kv.Key].Value) + 0.3f);
            check(within, "VGE: hybrid genes are inherited (near its genome sources), not random");
            Hybrid.Name = new NameSingle("Lineage hybrid");
        }

        check(!wombLineage.Any, "VGE: womb lineage cleared after the birth");
    }
}
