using System;
using System.Collections.Generic;
using System.Linq;
using AnimalGenetics;
using AnimalGeneticsBreeding;
using RimWorld;
using UnityEngine;
using Verse;

namespace AGBTest;

public static class Phase2Tests
{
    public static Pawn Grandchild;

    private static GeneticInformation Founder(float value)
    {
        var records = Constants.AffectedStats.ToDictionary(s => s, _ => new GeneRecord { Value = value });
        return new GeneticInformation(records);
    }

    private static GeneticInformation Child(GeneticInformation mother, GeneticInformation father, float value)
    {
        var gi = Founder(value);
        gi.Mother = mother;
        gi.Father = father;
        return gi;
    }

    private static bool Near(float a, float b, float tol = 0.0005f)
    {
        return Mathf.Abs(a - b) <= tol;
    }

    public static void Run(Action<bool, string> check, List<Pawn> males, List<Pawn> females, Map map)
    {
        // ---- Kinship on a known family graph
        var a = Founder(1f);
        var b = Founder(1f);
        var c = Founder(1f);
        var s1 = Child(a, b, 1f);
        var s2 = Child(a, b, 1f);
        var half = Child(a, c, 1f);
        var inbred = Child(s1, s2, 1f);
        check(Near(Kinship.Coancestry(a, b), 0f), "kinship: unrelated founders = 0");
        check(Near(Kinship.Coancestry(s1, s2), 0.25f), $"kinship: full siblings = 0.25 (got {Kinship.Coancestry(s1, s2):F4})");
        check(Near(Kinship.Coancestry(a, s1), 0.25f), $"kinship: parent-child = 0.25 (got {Kinship.Coancestry(a, s1):F4})");
        check(Near(Kinship.Coancestry(s1, half), 0.125f), $"kinship: half siblings = 0.125 (got {Kinship.Coancestry(s1, half):F4})");
        check(Near(Kinship.Inbreeding(inbred), 0.25f), "kinship: child of siblings has F = 0.25");
        check(Near(Kinship.Coancestry(inbred, inbred), 0.625f), "kinship: self-coancestry = (1+F)/2");
        var cousinA = Child(s1, Founder(1f), 1f);
        var cousinB = Child(s2, Founder(1f), 1f);
        check(Near(Kinship.Coancestry(cousinA, cousinB), 0.0625f),
            $"kinship: first cousins = 0.0625 (got {Kinship.Coancestry(cousinA, cousinB):F4})");

        // ---- Inbreeding penalty applied through Animal Genetics' own generator
        var ag = CoreMod.Instance.Settings;
        var speed = StatDefOf.MoveSpeed;
        float MeanChild(GeneticInformation m, GeneticInformation f)
        {
            const int n = 3000;
            var sum = 0f;
            for (var i = 0; i < n; i++)
            {
                sum += GeneticCalculator.GenerateGenesRecord(m, f)[speed].Value;
            }

            return sum / n;
        }

        var settings = BreedingSettingsMod.Settings;
        settings.inbreedingEnabled = false;
        var baseline = MeanChild(a, b);
        check(Near(baseline, 1f + ag.mutationMean, 0.006f), $"penalty off: unrelated child speed ≈ {1f + ag.mutationMean:F3} (got {baseline:F3})");
        var siblingsOff = MeanChild(s1, s2);
        check(Near(siblingsOff, baseline, 0.006f), $"penalty off: sibling child speed unchanged (got {siblingsOff:F3})");
        settings.inbreedingEnabled = true;
        settings.inbreedingPenalty = 0.5f;
        var siblingsOn = MeanChild(s1, s2);
        check(Near(siblingsOn, baseline - 0.125f, 0.006f),
            $"penalty on: sibling child speed ≈ {baseline - 0.125f:F3} (got {siblingsOn:F3})");
        var unrelatedOn = MeanChild(a, b);
        check(Near(unrelatedOn, baseline, 0.006f), $"penalty on: unrelated child unchanged (got {unrelatedOn:F3})");

        // ---- Prediction model mirrors Animal Genetics' inheritance (real muffalo genes)
        var father = males[1].AnimalGenetics();
        var mother = females[2].AnimalGenetics();
        var species = males[1].def;
        var prediction = OffspringModel.Predict(species, father, mother, 0f);
        // Compare against Animal Genetics' generator with many samples; report every gene so a systematic
        // mismatch (not noise) is visible. Model uses 2000 samples, so allow ~4 standard errors.
        const int samples = 20000;
        var worst = 0f;
        var worstExcess = float.MinValue;
        foreach (var gene in prediction.Genes)
        {
            var sum = 0f;
            var sumSq = 0f;
            for (var i = 0; i < samples; i++)
            {
                var v = GeneticCalculator.GenerateGenesRecord(mother, father)[gene.Stat].Value;
                sum += v;
                sumSq += v * v;
            }

            var mean = sum / samples;
            var sd = Mathf.Sqrt(Mathf.Max((sumSq / samples) - (mean * mean), 0f));
            var tolerance = 4f * sd * Mathf.Sqrt((1f / samples) + (1f / 2000f));
            var diff = Mathf.Abs(mean - gene.Mean);
            worst = Mathf.Max(worst, diff);
            worstExcess = Mathf.Max(worstExcess, diff - tolerance);
            Log.Message($"[AGBTEST] gene {gene.Stat.defName,-18} AG {mean:F4}  model {gene.Mean:F4}  diff {diff:F4}  tol {tolerance:F4}");
        }

        check(worstExcess <= 0f, $"prediction gene means match Animal Genetics' generator (worst diff {worst:F4})");
        var expected = OffspringModel.ExpectedScore(species, father, mother);
        check(Near(expected, prediction.ScoreMean, 0.01f),
            $"closed-form expected score ≈ Monte Carlo ({expected:F3} vs {prediction.ScoreMean:F3})");
        check(prediction.ScoreLow <= prediction.ScoreMean && prediction.ScoreMean <= prediction.ScoreHigh,
            "prediction range brackets mean");
        settings.inbreedingEnabled = false;

        // ---- Real births: two siblings, then their child (F should be 0.25)
        var dam = females[2];
        var sire = males[1];
        Pawn sonKid = null, daughterKid = null;
        for (var attempt = 0; attempt < 20 && (sonKid == null || daughterKid == null); attempt++)
        {
            var before = map.mapPawns.AllPawnsSpawned.ToHashSet();
            Hediff_Pregnant.DoBirthSpawn(dam, sire);
            foreach (var kid in map.mapPawns.AllPawnsSpawned.Where(p => !before.Contains(p) && p.def == dam.def))
            {
                if (kid.gender == Gender.Male && sonKid == null)
                {
                    sonKid = kid;
                }
                else if (kid.gender == Gender.Female && daughterKid == null)
                {
                    daughterKid = kid;
                }
            }
        }

        check(sonKid != null && daughterKid != null, "births produced a son and a daughter");
        if (sonKid == null || daughterKid == null)
        {
            return;
        }

        check(sonKid.AnimalGenetics().Mother == dam.AnimalGenetics() &&
              sonKid.AnimalGenetics().Father == sire.AnimalGenetics(), "birth links child genetics to both parents");

        var before2 = map.mapPawns.AllPawnsSpawned.ToHashSet();
        Hediff_Pregnant.DoBirthSpawn(daughterKid, sonKid);
        Grandchild = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => !before2.Contains(p) && p.def == dam.def);
        check(Grandchild != null, "grandchild born from siblings");
        if (Grandchild != null)
        {
            var f = Kinship.Inbreeding(Grandchild.AnimalGenetics());
            check(Near(f, 0.25f), $"grandchild of full siblings has F = 0.25 (got {f:F4})");
            Grandchild.Name = new NameSingle("Inbred test calf");
        }
    }
}
