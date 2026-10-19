using System;
using System.Collections.Generic;
using System.Linq;
using AnimalGenetics;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace AnimalGeneticsBreeding;

// ------------------------------------------------------------------ comps (added to VGE defs by XML patch)

/// <summary>On VGE genome items: which animal this genome was taken from (its Animal Genetics record).</summary>
public class CompProperties_GenomeProfile : CompProperties
{
    public CompProperties_GenomeProfile()
    {
        compClass = typeof(CompGenomeProfile);
    }
}

public class CompGenomeProfile : ThingComp
{
    public int createdTick = -1;
    public GeneticInformation genetics;
    public string sourceName;
    public ThingDef sourceSpecies;

    public void SetFrom(Pawn source)
    {
        var gi = source?.AnimalGenetics();
        if (gi == null)
        {
            return;
        }

        genetics = gi;
        sourceName = source.LabelShortCap;
        sourceSpecies = source.def;
    }

    public override void PostPostMake()
    {
        base.PostPostMake();
        createdTick = Find.TickManager?.TicksGame ?? -1;
    }

    public override bool AllowStackWith(Thing other)
    {
        var o = other.TryGetComp<CompGenomeProfile>();
        return genetics == null && o?.genetics == null;
    }

    public override void PostSplitOff(Thing piece)
    {
        base.PostSplitOff(piece);
        var o = piece.TryGetComp<CompGenomeProfile>();
        if (o != null)
        {
            o.genetics = genetics;
            o.sourceName = sourceName;
            o.sourceSpecies = sourceSpecies;
        }
    }

    public override string TransformLabel(string label)
    {
        return sourceName == null ? label : "AGB.GenomeFrom".Translate(label, sourceName);
    }

    public override string CompInspectStringExtra()
    {
        var records = genetics?._geneRecords;
        if (records == null || sourceSpecies == null)
        {
            return null;
        }

        var score = BreedingScore.FromValues(sourceSpecies, s => records.TryGetValue(s, out var r) ? r.Value : 1f);
        return "AGB.GenomeProfile".Translate(sourceName, BreedingScore.Format(score)) + "\n" +
               string.Join(", ", BreedingUtility.GenesFor(sourceSpecies).Where(records.ContainsKey)
                   .Select(s => $"{AgStats.Label(s)} {records[s].Value * 100f:F0}%"));
    }

    public override void PostExposeData()
    {
        base.PostExposeData();
        Scribe_References.Look(ref genetics, "agbGenetics");
        Scribe_Values.Look(ref sourceName, "agbSourceName");
        Scribe_Defs.Look(ref sourceSpecies, "agbSourceSpecies");
    }
}

/// <summary>On VGE growth cells and electrowombs: the two genome profiles the hybrid will inherit from.</summary>
public class CompProperties_HybridLineage : CompProperties
{
    public CompProperties_HybridLineage()
    {
        compClass = typeof(CompHybridLineage);
    }
}

public class CompHybridLineage : ThingComp
{
    public GeneticInformation dominant;
    public GeneticInformation secondary;

    public bool Any => dominant != null || secondary != null;

    public void CopyFrom(CompHybridLineage other)
    {
        dominant = other?.dominant;
        secondary = other?.secondary;
    }

    public void Clear()
    {
        dominant = secondary = null;
    }

    public override string CompInspectStringExtra()
    {
        return Any ? "AGB.LineageInspect".Translate() : null;
    }

    public override void PostExposeData()
    {
        base.PostExposeData();
        Scribe_References.Look(ref dominant, "agbDominant");
        Scribe_References.Look(ref secondary, "agbSecondary");
    }
}

// ------------------------------------------------------------------ genome sources

/// <summary>Genome extractor bills: the genome remembers the corpse it came from.</summary>
[HarmonyPatch(typeof(GenRecipe), nameof(GenRecipe.MakeRecipeProducts))]
public static class GenRecipe_MakeRecipeProducts
{
    public static IEnumerable<Thing> Postfix(IEnumerable<Thing> __result, List<Thing> ingredients)
    {
        var source = ingredients?.Select(i => i is Corpse c ? c.InnerPawn : i as Pawn)
            .FirstOrDefault(p => p?.AnimalGenetics() != null);
        foreach (var product in __result)
        {
            if (source != null)
            {
                product.TryGetComp<CompGenomeProfile>()?.SetFrom(source);
            }

            yield return product;
        }
    }
}

/// <summary>Patches that only exist when Vanilla Genetics Expanded is loaded (its types are looked up by name).</summary>
[StaticConstructorOnStartup]
public static class VgeBridge
{
    public const string PackageId = "VanillaExpanded.VGeneticsE";

    public static readonly bool Active;

    private static readonly AccessTools.FieldRef<object, Thing> genomorpherDominant;
    private static readonly AccessTools.FieldRef<object, Thing> genomorpherSecondary;
    private static readonly AccessTools.FieldRef<object, Thing> genomorpherCell;
    private static readonly AccessTools.FieldRef<object, PawnKindDef> wombGrowing;

    static VgeBridge()
    {
        Active = ModsConfig.IsActive(PackageId);
        if (!Active)
        {
            return;
        }

        try
        {
            var genomorpher = AccessTools.TypeByName("GeneticRim.CompGenomorpher");
            var womb = AccessTools.TypeByName("GeneticRim.CompElectroWomb");
            var extract = AccessTools.TypeByName("GeneticRim.CompTargetEffect_Extract");
            genomorpherDominant = AccessTools.FieldRefAccess<Thing>(genomorpher, "genomeDominant");
            genomorpherSecondary = AccessTools.FieldRefAccess<Thing>(genomorpher, "genomeSecondary");
            genomorpherCell = AccessTools.FieldRefAccess<Thing>(genomorpher, "growthCell");
            wombGrowing = AccessTools.FieldRefAccess<PawnKindDef>(womb, "growingResult");

            var h = new Harmony(BreedingMod.HarmonyId + ".vge");
            h.Patch(AccessTools.Method(genomorpher, "StartGrowthProcess"),
                new HarmonyMethod(typeof(VgeBridge), nameof(StartGrowth_Prefix)),
                new HarmonyMethod(typeof(VgeBridge), nameof(StartGrowth_Postfix)));
            h.Patch(AccessTools.Method(womb, "InitProcess"),
                new HarmonyMethod(typeof(VgeBridge), nameof(WombInit_Prefix)));
            h.Patch(AccessTools.Method(womb, "CompTick"),
                new HarmonyMethod(typeof(VgeBridge), nameof(WombTick_Prefix)),
                new HarmonyMethod(typeof(VgeBridge), nameof(WombTick_Postfix)),
                finalizer: new HarmonyMethod(typeof(VgeBridge), nameof(WombTick_Finalizer)));
            h.Patch(AccessTools.Method(extract, "DoEffectOn"),
                new HarmonyMethod(typeof(VgeBridge), nameof(Extract_Prefix)),
                new HarmonyMethod(typeof(VgeBridge), nameof(Extract_Postfix)));
            Log.Message("[AnimalGeneticsBreeding] Vanilla Genetics Expanded detected: hybrids inherit genome genetics.");
        }
        catch (Exception e)
        {
            Active = false;
            Log.Error($"[AnimalGeneticsBreeding] Could not hook into Vanilla Genetics Expanded: {e}");
        }
    }

    public static GeneticInformation ProfileOf(Thing genome)
    {
        return genome?.TryGetComp<CompGenomeProfile>()?.genetics;
    }

    // Genomorpher: capture the two genome profiles before VGE nulls them, put them on the growth cell.
    public static void StartGrowth_Prefix(object __instance, out (GeneticInformation, GeneticInformation) __state)
    {
        __state = (ProfileOf(genomorpherDominant(__instance)), ProfileOf(genomorpherSecondary(__instance)));
    }

    public static void StartGrowth_Postfix(object __instance, (GeneticInformation, GeneticInformation) __state)
    {
        var lineage = genomorpherCell(__instance)?.TryGetComp<CompHybridLineage>();
        if (lineage != null)
        {
            (lineage.dominant, lineage.secondary) = __state;
        }
    }

    // Electrowomb: the growth cell is destroyed on insertion, so copy its lineage first.
    public static void WombInit_Prefix(ThingComp __instance, Thing growthCell)
    {
        __instance.parent.TryGetComp<CompHybridLineage>()?.CopyFrom(growthCell?.TryGetComp<CompHybridLineage>());
    }

    // While the womb ticks (and possibly spawns the hybrid), make the lineage the parents Animal Genetics sees.
    public static void WombTick_Prefix(ThingComp __instance, out bool __state)
    {
        __state = false;
        var lineage = __instance.parent.TryGetComp<CompHybridLineage>();
        if (lineage is not { Any: true } || wombGrowing(__instance) == null)
        {
            return;
        }

        ParentReferences.Push(new ParentReferences.Record { Mother = lineage.dominant, Father = lineage.secondary });
        __state = true;
    }

    public static void WombTick_Postfix(ThingComp __instance, bool __state)
    {
        if (__state && wombGrowing(__instance) == null)
        {
            // Hybrid was born this tick.
            __instance.parent.TryGetComp<CompHybridLineage>()?.Clear();
        }
    }

    public static Exception WombTick_Finalizer(Exception __exception, bool __state)
    {
        if (__state)
        {
            ParentReferences.Pop();
        }

        return __exception;
    }

    // Genome excavator: VGE spawns the genome near the target; tag genomes made during this call.
    public static void Extract_Prefix(Thing target, out (Pawn, Map, IntVec3) __state)
    {
        var pawn = target is Corpse c ? c.InnerPawn : target as Pawn;
        __state = (pawn, target?.MapHeld, target?.PositionHeld ?? IntVec3.Invalid);
    }

    public static void Extract_Postfix((Pawn, Map, IntVec3) __state)
    {
        var (pawn, map, pos) = __state;
        if (pawn?.AnimalGenetics() == null || map == null || !pos.IsValid)
        {
            return;
        }

        var now = Find.TickManager.TicksGame;
        foreach (var cell in GenRadial.RadialCellsAround(pos, 4f, true).Where(c => c.InBounds(map)))
        {
            foreach (var thing in cell.GetThingList(map))
            {
                var profile = thing.TryGetComp<CompGenomeProfile>();
                if (profile is { genetics: null } && profile.createdTick == now)
                {
                    profile.SetFrom(pawn);
                }
            }
        }
    }
}

// ------------------------------------------------------------------ trading

/// <summary>Pedigree samples for sale: random species, genes picked as the best of a few random draws.</summary>
public class StockGenerator_GermlineSamples : StockGenerator
{
    public int qualityDraws = 3;

    public override IEnumerable<Thing> GenerateThings(PlanetTile forTile, Faction faction = null)
    {
        var species = DefDatabase<ThingDef>.AllDefs.Where(d =>
            d.race is { Animal: true, Dryad: false } && d.HasComp(typeof(CompBreeding)) &&
            d.GetStatValueAbstract(StatDefOf.Wildness) < 0.8f && d.tradeTags != null).ToList();
        if (species.Count == 0)
        {
            yield break;
        }

        var count = countRange.RandomInRange;
        for (var i = 0; i < count; i++)
        {
            var def = species.RandomElement();
            Dictionary<StatDef, GeneRecord> best = null;
            var bestScore = float.MinValue;
            for (var d = 0; d < Mathf.Max(1, qualityDraws); d++)
            {
                var records = GeneticCalculator.GenerateGenesRecord(null, null);
                var score = BreedingScore.FromValues(def, s => records.TryGetValue(s, out var r) ? r.Value : 1f);
                if (score > bestScore)
                {
                    best = records;
                    bestScore = score;
                }
            }

            var thing = ThingMaker.MakeThing(AGB_DefOf.AGB_GermlineSample);
            var comp = thing.TryGetComp<CompGermlineSample>();
            comp.genetics = new GeneticInformation(best);
            comp.species = def;
            comp.donorName = "AGB.PedigreeStock".Translate();
            yield return thing;
        }
    }

    public override bool HandlesThingDef(ThingDef thingDef)
    {
        return thingDef == AGB_DefOf.AGB_GermlineSample;
    }
}

/// <summary>Germline samples are worth more the better the donor's genes (for its species' breeding goal).</summary>
public class StatPart_SampleGenes : RimWorld.StatPart
{
    public static float Factor(Thing thing)
    {
        var comp = thing?.TryGetComp<CompGermlineSample>();
        return comp?.genetics == null ? 1f : Mathf.Max(0.25f, 1f + (4f * comp.Score));
    }

    public override void TransformValue(StatRequest req, ref float val)
    {
        if (req.HasThing && req.Thing.def == AGB_DefOf.AGB_GermlineSample)
        {
            val *= Factor(req.Thing);
        }
    }

    public override string ExplanationPart(StatRequest req)
    {
        return req.HasThing && req.Thing.def == AGB_DefOf.AGB_GermlineSample
            ? "AGB.SampleValueFactor".Translate(Factor(req.Thing).ToStringPercent())
            : null;
    }
}

[StaticConstructorOnStartup]
public static class SampleValueSetup
{
    static SampleValueSetup()
    {
        StatDefOf.MarketValue.parts ??= [];
        StatDefOf.MarketValue.parts.Add(new StatPart_SampleGenes { parentStat = StatDefOf.MarketValue });
    }
}
