using System.Collections.Generic;
using AnimalGenetics;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AnimalGeneticsBreeding;

/// <summary>
///     Wright's coefficients on Animal Genetics' parent graph (GeneticInformation.Mother/Father), which survives
///     after the animals themselves are gone.
/// </summary>
public static class Kinship
{
    private const int MaxDepth = 12;

    /// <summary>Coefficient of coancestry of a and b = inbreeding coefficient F of their offspring.</summary>
    public static float Coancestry(GeneticInformation a, GeneticInformation b)
    {
        var memo = new Dictionary<(GeneticInformation, GeneticInformation), float>();
        var depths = new Dictionary<GeneticInformation, int>();
        return Coancestry(a, b, memo, depths, 0);
    }

    /// <summary>Inbreeding coefficient of an individual: coancestry of its parents.</summary>
    public static float Inbreeding(GeneticInformation gi)
    {
        return gi == null ? 0f : Coancestry(gi.Mother, gi.Father);
    }

    private static float Coancestry(GeneticInformation a, GeneticInformation b,
        Dictionary<(GeneticInformation, GeneticInformation), float> memo, Dictionary<GeneticInformation, int> depths,
        int recursion)
    {
        if (a == null || b == null || recursion > MaxDepth)
        {
            return 0f;
        }

        if (memo.TryGetValue((a, b), out var cached) || memo.TryGetValue((b, a), out cached))
        {
            return cached;
        }

        float result;
        if (a == b)
        {
            result = 0.5f * (1f + Coancestry(a.Mother, a.Father, memo, depths, recursion + 1));
        }
        else
        {
            // Expand the younger individual (more generations above it), so we never recurse into a's
            // descendants: f(a,b) = (f(mother(a), b) + f(father(a), b)) / 2.
            if (Generation(b, depths) > Generation(a, depths))
            {
                (a, b) = (b, a);
            }

            result = 0.5f * (Coancestry(a.Mother, b, memo, depths, recursion + 1) +
                             Coancestry(a.Father, b, memo, depths, recursion + 1));
        }

        memo[(a, b)] = result;
        return result;
    }

    private static int Generation(GeneticInformation gi, Dictionary<GeneticInformation, int> depths, int guard = 0)
    {
        if (gi == null || guard > 64)
        {
            return 0;
        }

        if (depths.TryGetValue(gi, out var d))
        {
            return d;
        }

        d = 1 + System.Math.Max(Generation(gi.Mother, depths, guard + 1), Generation(gi.Father, depths, guard + 1));
        depths[gi] = d;
        return d;
    }

    /// <summary>Gene change per gene caused by inbreeding (negative = worse), using current settings.</summary>
    public static float PenaltyFor(float coefficient)
    {
        var settings = BreedingSettingsMod.Settings;
        return settings is { inbreedingEnabled: true } ? coefficient * settings.inbreedingPenalty : 0f;
    }

    public static string Describe(float coefficient)
    {
        return coefficient switch
        {
            <= 0.001f => "AGB.KinUnrelated".Translate(),
            >= 0.24f => "AGB.KinClose".Translate(),
            >= 0.12f => "AGB.KinModerate".Translate(),
            _ => "AGB.KinDistant".Translate()
        };
    }
}
