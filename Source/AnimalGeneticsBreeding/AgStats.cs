using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RimWorld;
using Verse;

namespace AnimalGeneticsBreeding;

/// <summary>
/// The only place this mod touches AnimalGenetics.Constants. Mono runs a beforefieldinit type initializer as soon
/// as it JIT-compiles a method that references the type, so a method mentioning Constants directly would trigger
/// its initializer whenever another mod Harmony-patches that method. If that happens from a Mod constructor (before
/// DefOfs are bound), Constants fails permanently and takes Animal Genetics down with it. These methods are not
/// inlined and only compile when first called, which is always after defs load.
/// If Constants is already broken (by some other mod), they fall back to an equivalent list built here.
/// </summary>
public static class AgStats
{
    private static List<StatDef> fallbackAffected;
    private static List<StatDef> fallbackInverted;

    public static List<StatDef> Affected
    {
        get
        {
            try { return ConstantsAffected(); }
            catch (TypeInitializationException) { return fallbackAffected ??= BuildFallback(); }
        }
    }

    public static bool IsInverted(StatDef stat)
    {
        try { return ConstantsInverted().Contains(stat); }
        catch (TypeInitializationException) { return (fallbackInverted ??= new List<StatDef> { StatDefOf.Wildness }).Contains(stat); }
    }

    public static string Label(StatDef stat)
    {
        try { return ConstantsLabel(stat); }
        catch (TypeInitializationException) { return stat?.label ?? stat?.defName ?? "?"; }
    }

    /// <summary>Appends a gene of this mod to Animal Genetics' gene list (and the fallback list).</summary>
    public static void AddGene(StatDef stat)
    {
        var list = Affected;
        if (!list.Contains(stat))
        {
            list.Add(stat);
        }

        extraGenes.Add(stat);
    }

    private static readonly List<StatDef> extraGenes = [];

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<StatDef> ConstantsAffected() => global::AnimalGenetics.Constants.AffectedStats;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<StatDef> ConstantsInverted() => global::AnimalGenetics.Constants.InvertedStats;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string ConstantsLabel(StatDef stat) => global::AnimalGenetics.Constants.GetLabel(stat);

    private static List<StatDef> BuildFallback()
    {
        var list = BuildAgFallback();
        list.AddRange(extraGenes);
        return list;
    }

    private static List<StatDef> BuildAgFallback() => new()
    {
        global::AnimalGenetics.AnimalGenetics.Health,
        global::AnimalGenetics.AnimalGenetics.Damage,
        StatDefOf.MoveSpeed,
        StatDefOf.CarryingCapacity,
        StatDefOf.MeatAmount,
        StatDefOf.LeatherAmount,
        StatDefOf.Wildness,
        global::AnimalGenetics.AnimalGenetics.GatherYield,
    };
}
