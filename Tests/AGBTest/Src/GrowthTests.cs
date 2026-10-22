using System;
using System.Collections.Generic;
using System.Linq;
using AnimalGenetics;
using AnimalGeneticsBreeding;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AGBTest;

/// <summary>Growth gene: registration, effect curve, inheritance drift, maturation and hatching speed.</summary>
public static class GrowthTests
{
    private static Action<bool, string> check;

    public static void Run(Action<bool, string> checkFn, Map map)
    {
        check = checkFn;
        var growth = GrowthGene.Stat;
        check(growth != null && Constants.AffectedStats.Contains(growth),
            $"growth gene registered with Animal Genetics ({Constants.AffectedStats.Count} genes)");
        check(DefDatabase<PawnTableDef>.GetNamed("Genetics").columns.Any(c => c.defName == "AGB_GrowthGene"),
            "Genetics tab has growth column");
        check(DefDatabase<StatDef>.GetNamedSilentFail("AnimalGenetics_" + GrowthGene.DefName) != null,
            "growth gene listed in the Genetics stats category");

        Curve();
        var kind = DefDatabase<PawnKindDef>.GetNamed("Muffalo");
        var adult = Spawn(map, kind, 5f);
        check(adult.AnimalGenetics().GeneRecords.ContainsKey(growth), "new animals get a growth gene");
        Inheritance(adult, kind.race);
        OldParents(adult);
        Maturation(map, kind);
        Hatching(map);
        adult.Destroy();
    }

    private static void Curve()
    {
        check(Mathf.Approximately(GrowthGene.Speed(1f), 1f), "growth 100% = normal speed");
        check(Mathf.Abs(GrowthGene.Speed(2f) - 1.682f) < 0.01f, $"growth 200% = x1.68 (got {GrowthGene.Speed(2f):F3})");
        check(Mathf.Approximately(GrowthGene.Speed(0.6f), 0.6f), "growth below 100% slows one for one");
        check(Mathf.Approximately(GrowthGene.Speed(0f), GrowthGene.MinSpeed), "growth speed floor x0.25");
        check(Mathf.Abs(BreedingScore.Deviation(GrowthGene.Stat, 2f) - 0.682f) < 0.01f,
            "score counts the growth gene's real speed-up, not its raw value");

        // x3 cap until growth acceleration is researched.
        var research = GrowthGene.AccelerationResearch;
        var manager = Find.ResearchManager;
        var wasDone = research.IsFinished;
        check(!wasDone && Mathf.Approximately(GrowthGene.Speed(10f), GrowthGene.MaxSpeed),
            $"growth speed capped at x3 before research (got x{GrowthGene.Speed(10f):F2})");
        manager.FinishProject(research, false, null, false);
        var uncapped = GrowthGene.Speed(10f);
        check(Mathf.Abs(uncapped - Mathf.Pow(10f, GrowthGene.Exponent)) < 0.01f,
            $"growth acceleration research lifts the cap (1000% → x{uncapped:F2})");
        Traverse.Create(manager).Field("progress").GetValue<Dictionary<ResearchProjectDef, float>>()[research] = 0f;
        check(!research.IsFinished, "research reset for the remaining tests");
    }

    private static GeneticInformation With(GeneticInformation gi, float growth, float speed)
    {
        var records = gi.GeneRecords.ToDictionary(kv => kv.Key, kv => new GeneRecord { Value = kv.Value.Value });
        records[GrowthGene.Stat].Value = growth;
        records[StatDefOf.MoveSpeed].Value = speed;
        return new GeneticInformation(records);
    }

    private static float Mean(GeneticInformation m, GeneticInformation f, StatDef stat, int n = 4000)
    {
        var sum = 0f;
        for (var i = 0; i < n; i++)
        {
            sum += GeneticCalculator.GenerateGenesRecord(m, f)[stat].Value;
        }

        return sum / n;
    }

    private static void Inheritance(Pawn adult, ThingDef species)
    {
        var settings = CoreMod.Instance.Settings;
        var gi = adult.AnimalGenetics();
        // Growth inherits exactly like Animal Genetics' own genes.
        var mother = With(gi, 1.8f, 1.8f);
        var father = With(gi, 1.2f, 1.2f);
        var speedMean = Mean(mother, father, StatDefOf.MoveSpeed);
        var growthMean = Mean(mother, father, GrowthGene.Stat);
        var expected = (settings.bestGeneChance * 1.8f) + ((1f - settings.bestGeneChance) * 1.2f) + settings.mutationMean;
        check(Mathf.Abs(speedMean - expected) < 0.02f && Mathf.Abs(growthMean - expected) < 0.02f,
            $"growth inherits like other genes: ≈ {expected:F3} (growth {growthMean:F3}, speed {speedMean:F3})");

        // The pairings prediction matches (both sides are sampled, so allow ~4 standard errors).
        var predicted = OffspringModel.Predict(species, father, mother, 0f).Genes
            .First(g => g.Stat == GrowthGene.Stat).Mean;
        check(Mathf.Abs(growthMean - predicted) < 0.03f, $"pairings predict growth children ({predicted:F3} vs real {growthMean:F3})");

        // Dominance serum works on growth.
        HediffComp_Serum.Apply(adult, DefDatabase<ThingDef>.GetNamed("AGB_SerumDominance"), GrowthGene.Stat);
        var dominant = Mean(adult.AnimalGenetics(), With(gi, 1.8f, 1f), GrowthGene.Stat);
        var expectedDominant = Mathf.Max(1.8f, gi.GeneRecords[GrowthGene.Stat].Value) + settings.mutationMean;
        check(Mathf.Abs(dominant - expectedDominant) < 0.006f,
            $"dominance serum (growth): ≈ {expectedDominant:F3} (got {dominant:F3})");
        foreach (var h in adult.health.hediffSet.hediffs.Where(h => h.TryGetComp<HediffComp_Serum>() != null).ToList())
        {
            adult.health.RemoveHediff(h);
        }
    }

    private static void OldParents(Pawn adult)
    {
        // Genetics saved before the gene existed (dead ancestors, old samples) must still breed.
        var records = adult.AnimalGenetics().GeneRecords.ToDictionary(kv => kv.Key, kv => new GeneRecord { Value = kv.Value.Value });
        records.Remove(GrowthGene.Stat);
        var old = new GeneticInformation(records);
        Dictionary<StatDef, GeneRecord> child = null;
        try
        {
            child = GeneticCalculator.GenerateGenesRecord(old, adult.AnimalGenetics());
        }
        catch (Exception e)
        {
            check(false, "breeding with pre-growth genetics threw " + e.GetType().Name);
        }

        check(child != null && child.ContainsKey(GrowthGene.Stat) && old._geneRecords.ContainsKey(GrowthGene.Stat),
            "parent from before the growth gene gets one filled in");
    }

    private static Pawn Spawn(Map map, PawnKindDef kind, float age)
    {
        var p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, Faction.OfPlayer, fixedGender: Gender.Female,
            fixedBiologicalAge: age, fixedChronologicalAge: age));
        GenSpawn.Spawn(p, CellFinder.RandomClosewalkCellNear(map.Center, map, 10), map);
        return p;
    }

    private static void SetGrowth(GeneticInformation gi, float value)
    {
        gi.GeneRecords[GrowthGene.Stat].Value = value;
    }

    private static void Maturation(Map map, PawnKindDef kind)
    {
        var calculate = AccessTools.Method(typeof(Pawn_AgeTracker), "CalculateGrowth");
        var growthField = AccessTools.Field(typeof(Pawn_AgeTracker), "growth");
        var normal = Spawn(map, kind, 0.05f);
        var fast = Spawn(map, kind, 0.05f);
        SetGrowth(normal.AnimalGenetics(), 1f);
        SetGrowth(fast.AnimalGenetics(), 2f);
        var gains = new List<float>();
        foreach (var p in new[] { normal, fast })
        {
            p.needs.food.CurLevelPercentage = 1f;
            var before = (float)growthField.GetValue(p.ageTracker);
            for (var i = 0; i < 100; i++)
            {
                calculate.Invoke(p.ageTracker, [240]);
            }

            gains.Add((float)growthField.GetValue(p.ageTracker) - before);
        }

        var ratio = gains[1] / Mathf.Max(gains[0], 1e-9f);
        check(gains[0] > 0f && Mathf.Abs(ratio - GrowthGene.Speed(2f)) < 0.02f,
            $"200% growth calf matures x{GrowthGene.Speed(2f):F2} as fast (got x{ratio:F3})");
        var inspect = fast.Breeding()?.CompInspectStringExtra() ?? "";
        check(inspect.Contains("AGB.GrowthSpeed".Translate(GrowthGene.Speed(2f).ToStringPercent()).Resolve()),
            "juvenile's inspect line shows its growth speed");
        var label = "Stat_Animal_GrowthTime".Translate().CapitalizeFirst().ToString();
        string GrowthTime(Pawn p) => p.def.race.SpecialDisplayStats(p.def, StatRequest.For(p))
            .FirstOrDefault(e => e.LabelCap == label)?.ValueString;
        var expectedDays = "PeriodDays".Translate((AnimalProductionUtility.DaysToAdulthood(kind.race) / GrowthGene.Speed(2f))
            .ToString("0.#")).ToString();
        check(GrowthTime(fast) == expectedDays && GrowthTime(normal) != expectedDays,
            $"info card growth time follows the gene (fast {GrowthTime(fast)}, normal {GrowthTime(normal)})");
        normal.Destroy();
        fast.Destroy();
    }

    private static void Hatching(Map map)
    {
        var eggDef = DefDatabase<ThingDef>.GetNamedSilentFail("EggChickenFertilized");
        if (eggDef == null)
        {
            check(false, "EggChickenFertilized missing");
            return;
        }

        var progress = AccessTools.Field(typeof(CompHatcher), "gestateProgress");
        var gains = new List<float>();
        var eggs = new List<Thing>();
        foreach (var value in new[] { 1f, 2f })
        {
            var egg = (ThingWithComps)ThingMaker.MakeThing(eggDef);
            var gi = egg.TryGetComp<EggGeneticInformation>().GeneticInformation;
            gi._geneRecords = GeneticCalculator.GenerateGenesRecord(null, null);
            gi._geneRecords[GrowthGene.Stat].Value = value;
            GenSpawn.Spawn(egg, CellFinder.RandomClosewalkCellNear(map.Center, map, 10), map);
            var hatcher = egg.TryGetComp<CompHatcher>();
            for (var i = 0; i < 1000; i++)
            {
                hatcher.CompTick();
            }

            gains.Add((float)progress.GetValue(hatcher));
            eggs.Add(egg);
        }

        var ratio = gains[1] / Mathf.Max(gains[0], 1e-9f);
        check(gains[0] > 0f && Mathf.Abs(ratio - GrowthGene.Speed(2f)) < 0.01f,
            $"200% growth egg hatches x{GrowthGene.Speed(2f):F2} as fast (got x{ratio:F3})");
        var inspect = eggs[1].TryGetComp<CompHatcher>().CompInspectStringExtra() ?? "";
        check(inspect.Contains("AGB.GrowthSpeed".Translate(GrowthGene.Speed(2f).ToStringPercent()).Resolve()),
            "egg inspect shows growth speed and adjusted hatch time");
        foreach (var egg in eggs)
        {
            egg.Destroy();
        }
    }
}
