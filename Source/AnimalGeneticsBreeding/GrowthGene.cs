using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using AnimalGenetics;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AnimalGeneticsBreeding;

/// <summary>
///     The growth gene: a ninth Animal Genetics gene added by this mod. It speeds up (or slows down) how fast young
///     animals mature and how fast fertilized eggs hatch. It lives in Animal Genetics' own gene list, so inheritance,
///     the Genetics tab, scores, serums, samples and splicing all handle it like any other gene.
///     A fast-growth line shortens every generation, which compounds, so above 100% the effect has diminishing
///     returns (<see cref="Speed" />) and is capped at x3 until the "growth acceleration" research lifts the cap.
/// </summary>
public static class GrowthGene
{
    public const string DefName = "AGB_Growth";

    /// <summary>Speed = gene ^ exponent above 100%: 150% → x1.36, 200% → x1.68, 300% → x2.28.</summary>
    public const float Exponent = 0.75f;

    public const float MinSpeed = 0.25f;

    /// <summary>Speed cap until <see cref="AccelerationResearch" /> is finished.</summary>
    public const float MaxSpeed = 3f;

    private static StatDef stat;
    private static ResearchProjectDef research;

    public static StatDef Stat => stat ??= DefDatabase<StatDef>.GetNamedSilentFail(DefName);

    public static ResearchProjectDef AccelerationResearch =>
        research ??= DefDatabase<ResearchProjectDef>.GetNamedSilentFail("AGB_GrowthAcceleration");

    public static bool Uncapped => AccelerationResearch is { IsFinished: true };

    /// <summary>Maturation / hatching speed multiplier for a gene value.</summary>
    public static float Speed(float gene)
    {
        if (gene <= 1f)
        {
            return Mathf.Max(gene, MinSpeed);
        }

        var speed = Mathf.Pow(gene, Exponent);
        return Uncapped ? speed : Mathf.Min(speed, MaxSpeed);
    }

    public static float SpeedOf(GeneticInformation genetics)
    {
        var records = genetics?._geneRecords;
        return Stat != null && records != null && records.TryGetValue(Stat, out var r) ? Speed(r.Value) : 1f;
    }

    public static float SpeedOf(Pawn pawn)
    {
        return pawn != null && pawn.RaceProps.Animal ? SpeedOf(pawn.AnimalGenetics()) : 1f;
    }

    /// <summary>Speed of a fertilized egg's embryo, or 1 while its genes are not known yet.</summary>
    public static float SpeedOfEgg(ThingWithComps egg)
    {
        var gi = egg.TryGetComp<EggGeneticInformation>()?.GeneticInformation;
        if (gi == null)
        {
            return 1f;
        }

        // Never trigger gene generation before Animal Genetics has linked the mother (it does so on the egg's tick).
        if (gi._geneRecords == null && gi.Mother != null)
        {
            _ = gi.GeneRecords;
        }

        return SpeedOf(gi);
    }

    /// <summary>Adds the gene to Animal Genetics' gene list and its stats panel. Called once at startup.</summary>
    public static void Register()
    {
        if (Stat == null)
        {
            Log.Error("[AnimalGeneticsBreeding] StatDef " + DefName + " missing; growth gene disabled.");
            return;
        }

        AgStats.AddGene(Stat);

        // Animal Genetics shows each gene in a "Genetics" stats category through an internal wrapper StatDef.
        try
        {
            var wrapperType = AccessTools.TypeByName("AnimalGenetics.StatDefWrapper");
            var workerType = AccessTools.TypeByName("AnimalGenetics.StatWorker");
            var category = DefDatabase<StatCategoryDef>.GetNamedSilentFail("AnimalGenetics_Category");
            var defName = "AnimalGenetics_" + DefName;
            if (wrapperType == null || workerType == null || category == null ||
                DefDatabase<StatDef>.GetNamedSilentFail(defName) != null)
            {
                return;
            }

            var wrapper = (StatDef)Activator.CreateInstance(wrapperType);
            wrapper.defName = defName;
            wrapper.label = Stat.label;
            wrapper.description = Stat.description;
            wrapper.category = category;
            wrapper.workerClass = workerType;
            wrapper.toStringStyle = ToStringStyle.PercentZero;
            AccessTools.Field(wrapperType, "Underlying").SetValue(wrapper, Stat);
            DefDatabase<StatDef>.Add(wrapper);
        }
        catch (Exception e)
        {
            Log.Warning("[AnimalGeneticsBreeding] Could not add the growth gene to the stats panel: " + e.Message);
        }
    }

    /// <summary>Vanilla's maturation rate (hunger), times the growth gene for animals.</summary>
    public static float MaturationSpeed(Pawn pawn)
    {
        return PawnUtility.BodyResourceGrowthSpeed(pawn) * SpeedOf(pawn);
    }
}

/// <summary>Animals mature by a "growth" fraction that vanilla advances every 240 ticks; scale that step.</summary>
[HarmonyPatch(typeof(Pawn_AgeTracker), "CalculateGrowth")]
public static class Pawn_AgeTracker_CalculateGrowth
{
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var target = AccessTools.Method(typeof(PawnUtility), nameof(PawnUtility.BodyResourceGrowthSpeed));
        var replacement = AccessTools.Method(typeof(GrowthGene), nameof(GrowthGene.MaturationSpeed));
        var replaced = false;
        foreach (var code in instructions)
        {
            if (!replaced && code.Calls(target))
            {
                replaced = true;
                yield return new CodeInstruction(OpCodes.Call, replacement).MoveLabelsFrom(code);
                continue;
            }

            yield return code;
        }

        if (!replaced)
        {
            Log.Warning("[AnimalGeneticsBreeding] Pawn_AgeTracker.CalculateGrowth changed; growth gene does not affect maturation.");
        }
    }
}

/// <summary>Fertilized eggs hatch faster or slower with the embryo's growth gene.</summary>
[HarmonyPatch(typeof(CompHatcher), nameof(CompHatcher.CompTick))]
public static class CompHatcher_CompTick
{
    public static void Prefix(CompHatcher __instance, ref float ___gestateProgress)
    {
        if (__instance.TemperatureDamaged)
        {
            return;
        }

        var speed = GrowthGene.SpeedOfEgg(__instance.parent);
        if (speed != 1f)
        {
            ___gestateProgress += (speed - 1f) / (__instance.Props.hatcherDaystoHatch * 60000f);
        }
    }
}

[HarmonyPatch(typeof(CompHatcher), nameof(CompHatcher.CompInspectStringExtra))]
public static class CompHatcher_CompInspectStringExtra
{
    public static void Postfix(CompHatcher __instance, float ___gestateProgress, ref string __result)
    {
        if (__result == null)
        {
            return;
        }

        var speed = GrowthGene.SpeedOfEgg(__instance.parent);
        if (speed == 1f)
        {
            return;
        }

        var days = __instance.Props.hatcherDaystoHatch * (1f - ___gestateProgress) / speed;
        __result = "EggProgress".Translate() + ": " + ___gestateProgress.ToStringPercent() + "\n" +
                   "HatchesIn".Translate() + ": " + "PeriodDays".Translate(days.ToString("F1")) + "\n" +
                   "AGB.GrowthSpeed".Translate(speed.ToStringPercent());
    }
}

/// <summary>Animal Genetics' gene tab has a fixed height for its own eight genes; make room for ours.</summary>
[HarmonyPatch]
public static class ITab_Pawn_Genetics_UpdateSize
{
    private const int AgGeneCount = 8;
    private const float RowHeight = 20f;

    private static readonly AccessTools.FieldRef<InspectTabBase, Vector2> size =
        AccessTools.FieldRefAccess<InspectTabBase, Vector2>("size");

    public static bool Prepare()
    {
        return AccessTools.TypeByName("AnimalGenetics.ITab_Pawn_Genetics") != null;
    }

    public static System.Reflection.MethodBase TargetMethod()
    {
        return AccessTools.Method(AccessTools.TypeByName("AnimalGenetics.ITab_Pawn_Genetics"), "UpdateSize");
    }

    public static void Postfix(InspectTabBase __instance)
    {
        var extra = AgStats.Affected.Count - AgGeneCount;
        if (extra > 0)
        {
            size(__instance).y += extra * RowHeight;
        }
    }
}

/// <summary>The info card's "Growth time" is per species; show this animal's own time with its growth gene.</summary>
[HarmonyPatch(typeof(RaceProperties), nameof(RaceProperties.SpecialDisplayStats))]
public static class RaceProperties_SpecialDisplayStats
{
    public static IEnumerable<StatDrawEntry> Postfix(IEnumerable<StatDrawEntry> entries, ThingDef parentDef,
        StatRequest req)
    {
        var speed = req.Thing is Pawn pawn ? GrowthGene.SpeedOf(pawn) : 1f;
        var label = "Stat_Animal_GrowthTime".Translate().CapitalizeFirst().ToString();
        foreach (var entry in entries)
        {
            if (speed != 1f && entry.LabelCap == label)
            {
                var days = AnimalProductionUtility.DaysToAdulthood(parentDef) / speed;
                yield return new StatDrawEntry(StatCategoryDefOf.AnimalProductivity, label,
                    "PeriodDays".Translate(days.ToString("0.#")),
                    "Stat_Animal_GrowthTimeDesc".Translate() + "\n\n" +
                    "AGB.GrowthTimeExplain".Translate(AnimalProductionUtility.DaysToAdulthood(parentDef).ToString("0.#"),
                        speed.ToStringPercent()), 9980);
                continue;
            }

            yield return entry;
        }
    }
}
