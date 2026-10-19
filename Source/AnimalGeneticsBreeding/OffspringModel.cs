using System;
using System.Collections.Generic;
using System.Linq;
using AnimalGenetics;
using RimWorld;
using UnityEngine;
using Verse;

namespace AnimalGeneticsBreeding;

/// <summary>
///     Predicts children of a pair by mirroring Animal Genetics' inheritance (GeneticCalculator):
///     per gene, take the higher parent value with chance bestGeneChance (else the lower), add
///     Gaussian(mutationMean, mutationStdDev) (sign flipped for inverted stats), clamp at 0 (wildness also at 1),
///     then apply this mod's optional inbreeding penalty.
/// </summary>
public class OffspringPrediction
{
    public float Coancestry;
    public List<GenePrediction> Genes = [];
    public float PenaltyPerGene;
    public float ScoreMean, ScoreLow, ScoreHigh;
    public float ChanceBeatsParents;
    public float ChanceBeatsHerdBest;

    public class GenePrediction
    {
        public StatDef Stat;
        public float Father, Mother;
        public float Mean, Low, High;
    }
}

public static class OffspringModel
{
    private const int Samples = 2000;

    private static readonly Dictionary<(GeneticInformation, GeneticInformation, int, int), OffspringPrediction>
        cache = new();

    private static float Value(GeneticInformation gi, StatDef stat)
    {
        var records = gi?._geneRecords;
        return records != null && records.TryGetValue(stat, out var r) ? r.Value : 1f;
    }

    /// <summary>Expected child score from the closed-form mean of each gene (cheap; used to rank many pairs).</summary>
    public static float ExpectedScore(ThingDef species, GeneticInformation father, GeneticInformation mother)
    {
        var settings = CoreMod.Instance.Settings;
        var penalty = Kinship.PenaltyFor(Kinship.Coancestry(father, mother));
        return BreedingScore.FromValues(species, stat =>
        {
            var a = Value(father, stat);
            var b = Value(mother, stat);
            var inverted = Constants.InvertedStats.Contains(stat);
            var mean = (settings.bestGeneChance * Mathf.Max(a, b)) + ((1f - settings.bestGeneChance) * Mathf.Min(a, b)) +
                       (inverted ? -settings.mutationMean : settings.mutationMean);
            return inverted ? Mathf.Min(mean + penalty, 1f) : Mathf.Max(mean - penalty, 0f);
        });
    }

    /// <summary>Full Monte Carlo prediction (cached per pair and settings version).</summary>
    public static OffspringPrediction Predict(ThingDef species, GeneticInformation father, GeneticInformation mother,
        float herdBest)
    {
        var settings = CoreMod.Instance.Settings;
        var key = (father, mother, BreedingProgram.Version,
            HashCode(settings.bestGeneChance, settings.mutationMean, settings.mutationStdDev,
                herdBest + (1000f * Kinship.PenaltyFor(1f))));
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (cache.Count > 64)
        {
            cache.Clear();
        }

        var coancestry = Kinship.Coancestry(father, mother);
        var penalty = Kinship.PenaltyFor(coancestry);
        var stats = BreedingUtility.GenesFor(species).ToList();
        var program = BreedingProgram.Get(species);

        // Deterministic per pair so the numbers don't flicker between frames.
        var rng = new System.Random(RuntimeHelpersHash(father) * 31 + RuntimeHelpersHash(mother));
        var values = new float[stats.Count, Samples];
        var scores = new float[Samples];
        var parentBest = Mathf.Max(
            BreedingScore.FromValues(species, s => Value(father, s)),
            BreedingScore.FromValues(species, s => Value(mother, s)));

        for (var n = 0; n < Samples; n++)
        {
            float sum = 0f, weightSum = 0f;
            for (var i = 0; i < stats.Count; i++)
            {
                var stat = stats[i];
                var a = Value(father, stat);
                var b = Value(mother, stat);
                var inverted = Constants.InvertedStats.Contains(stat);
                var parent = rng.NextDouble() < settings.bestGeneChance ? Mathf.Max(a, b) : Mathf.Min(a, b);
                var delta = settings.mutationMean + (Gaussian(rng) * settings.mutationStdDev);
                if (inverted)
                {
                    delta = -delta;
                }

                var v = Mathf.Max(parent + delta, 0f);
                if (stat == RimWorld.StatDefOf.Wildness)
                {
                    v = Mathf.Min(v, 1f);
                }

                v = inverted ? Mathf.Min(v + penalty, 1f) : Mathf.Max(v - penalty, 0f);
                values[i, n] = v;

                var w = program.GetWeight(stat);
                if (w > 0f)
                {
                    sum += w * (inverted ? 1f - v : v - 1f);
                    weightSum += w;
                }
            }

            scores[n] = weightSum > 0f ? sum / weightSum : 0f;
        }

        var result = new OffspringPrediction { Coancestry = coancestry, PenaltyPerGene = penalty };
        for (var i = 0; i < stats.Count; i++)
        {
            var column = new float[Samples];
            for (var n = 0; n < Samples; n++)
            {
                column[n] = values[i, n];
            }

            Array.Sort(column);
            result.Genes.Add(new OffspringPrediction.GenePrediction
            {
                Stat = stats[i],
                Father = Value(father, stats[i]),
                Mother = Value(mother, stats[i]),
                Mean = column.Average(),
                Low = column[(int)(Samples * 0.05f)],
                High = column[(int)(Samples * 0.95f)]
            });
        }

        var sorted = (float[])scores.Clone();
        Array.Sort(sorted);
        result.ScoreMean = scores.Average();
        result.ScoreLow = sorted[(int)(Samples * 0.05f)];
        result.ScoreHigh = sorted[(int)(Samples * 0.95f)];
        result.ChanceBeatsParents = scores.Count(s => s > parentBest) / (float)Samples;
        result.ChanceBeatsHerdBest = scores.Count(s => s > herdBest) / (float)Samples;
        cache[key] = result;
        return result;
    }

    private static int RuntimeHelpersHash(object o)
    {
        return o == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
    }

    private static int HashCode(float a, float b, float c, float d)
    {
        unchecked
        {
            return (((a.GetHashCode() * 397) ^ b.GetHashCode()) * 397 ^ c.GetHashCode()) * 397 ^ d.GetHashCode();
        }
    }

    private static float Gaussian(System.Random rng)
    {
        // Box-Muller, same as Verse.Rand.Gaussian.
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2));
    }
}
