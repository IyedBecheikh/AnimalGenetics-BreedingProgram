using System.Collections.Generic;
using System.Linq;
using AnimalGenetics;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AnimalGeneticsBreeding;

public enum SerumKind
{
    Dominance,
    Stabilizing,
    Mutagenic
}

/// <summary>On serum items: which hediff they give and whether the player picks a gene.</summary>
public class SerumExtension : DefModExtension
{
    public HediffDef hediff;
    public SerumKind kind;
}

public class HediffCompProperties_Serum : HediffCompProperties
{
    public SerumKind kind;

    public HediffCompProperties_Serum()
    {
        compClass = typeof(HediffComp_Serum);
    }
}

/// <summary>Active serum on a mother. Changes how her next offspring inherit genes.</summary>
public class HediffComp_Serum : HediffComp
{
    // Mother genetics → serums active on her. Rebuilt from hediffs, never saved separately.
    private static readonly Dictionary<GeneticInformation, List<HediffComp_Serum>> active = new();

    public string geneDefName;

    public SerumKind Kind => ((HediffCompProperties_Serum)props).kind;

    public StatDef Gene => geneDefName == null ? null : Constants.AffectedStats.FirstOrDefault(s => s.defName == geneDefName);

    public override string CompLabelInBracketsExtra =>
        Kind == SerumKind.Dominance && Gene != null ? Constants.GetLabel(Gene) : null;

    public static List<HediffComp_Serum> For(GeneticInformation mother)
    {
        if (mother == null || !active.TryGetValue(mother, out var list))
        {
            return null;
        }

        list.RemoveAll(c => c.parent?.pawn == null || c.parent.pawn.Dead || !c.parent.pawn.health.hediffSet.hediffs.Contains(c.parent));
        return list.Count > 0 ? list : null;
    }

    private void Register()
    {
        var gi = parent.pawn?.AnimalGenetics();
        if (gi == null)
        {
            return;
        }

        if (!active.TryGetValue(gi, out var list))
        {
            active[gi] = list = [];
        }

        if (!list.Contains(this))
        {
            list.Add(this);
        }
    }

    public override void CompPostMake()
    {
        base.CompPostMake();
        Register();
    }

    public override void CompExposeData()
    {
        base.CompExposeData();
        Scribe_Values.Look(ref geneDefName, "agbGene");
        if (Scribe.mode == LoadSaveMode.PostLoadInit)
        {
            Register();
        }
    }

    public override void CompPostPostRemoved()
    {
        base.CompPostPostRemoved();
        var gi = parent.pawn?.AnimalGenetics();
        if (gi != null && active.TryGetValue(gi, out var list))
        {
            list.Remove(this);
        }
    }

    public static void Apply(Pawn mother, ThingDef serumDef, StatDef gene)
    {
        var ext = serumDef.GetModExtension<SerumExtension>();
        // Same serum type replaces the previous dose.
        foreach (var old in mother.health.hediffSet.hediffs.Where(h => h.def == ext.hediff).ToList())
        {
            mother.health.RemoveHediff(old);
        }

        var hediff = HediffMaker.MakeHediff(ext.hediff, mother);
        var comp = hediff.TryGetComp<HediffComp_Serum>();
        comp.geneDefName = gene?.defName;
        mother.health.AddHediff(hediff);
        comp.Register();
    }
}

/// <summary>
///     The one hook into Animal Genetics' inheritance: serums (dominance / mutation spread) and the optional
///     inbreeding penalty, applied in that order to genes generated for a new child.
/// </summary>
[HarmonyPatch(typeof(GeneticCalculator), nameof(GeneticCalculator.EnsureAllGenesExist))]
public static class GeneticCalculator_EnsureAllGenesExist
{
    public class State
    {
        public HashSet<StatDef> Existing;
        public float Penalty;
        public HashSet<string> Dominant;
        public float OriginalStdDev = -1f;
    }

    public const float StabilizingSpread = 0.25f;
    public const float MutagenicSpread = 3f;

    public static void Prefix(Dictionary<StatDef, GeneRecord> records, GeneticInformation mother,
        GeneticInformation father, out State __state)
    {
        __state = null;
        if (records == null || (mother == null && father == null))
        {
            return;
        }

        var state = new State();
        if (mother != null && father != null)
        {
            state.Penalty = Kinship.PenaltyFor(Kinship.Coancestry(mother, father));
        }

        var serums = HediffComp_Serum.For(mother);
        if (serums != null)
        {
            var spread = 1f;
            foreach (var serum in serums)
            {
                switch (serum.Kind)
                {
                    case SerumKind.Stabilizing:
                        spread *= StabilizingSpread;
                        break;
                    case SerumKind.Mutagenic:
                        spread *= MutagenicSpread;
                        break;
                    case SerumKind.Dominance when serum.geneDefName != null:
                        (state.Dominant ??= []).Add(serum.geneDefName);
                        break;
                }
            }

            if (!Mathf.Approximately(spread, 1f))
            {
                var settings = CoreMod.Instance.Settings;
                state.OriginalStdDev = settings.mutationStdDev;
                settings.mutationStdDev *= spread;
            }
        }

        if (state.Penalty <= 0f && state.Dominant == null && state.OriginalStdDev < 0f)
        {
            return;
        }

        state.Existing = [..records.Keys];
        __state = state;
    }

    public static void Postfix(Dictionary<StatDef, GeneRecord> records, GeneticInformation mother,
        GeneticInformation father, State __state)
    {
        if (__state?.Existing == null)
        {
            return;
        }

        var settings = CoreMod.Instance.Settings;
        foreach (var (stat, record) in records)
        {
            if (__state.Existing.Contains(stat))
            {
                continue;
            }

            var inverted = Constants.InvertedStats.Contains(stat);
            if (__state.Dominant?.Contains(stat.defName) == true && mother?._geneRecords != null &&
                father?._geneRecords != null && mother._geneRecords.TryGetValue(stat, out var m) &&
                father._geneRecords.TryGetValue(stat, out var f))
            {
                // Dominance: always inherit the better parent's value, then mutate as usual.
                var better = inverted ? Mathf.Min(m.Value, f.Value) : Mathf.Max(m.Value, f.Value);
                var delta = Rand.Gaussian(settings.mutationMean, settings.mutationStdDev);
                record.Value = Mathf.Max(better + (inverted ? -delta : delta), 0f);
                record.Parent = better == m.Value ? GeneRecord.Source.Mother : GeneRecord.Source.Father;
                if (stat == StatDefOf.Wildness)
                {
                    record.Value = Mathf.Min(record.Value, 1f);
                }
            }

            if (__state.Penalty > 0f)
            {
                record.Value = inverted
                    ? Mathf.Min(record.Value + __state.Penalty, 1f)
                    : Mathf.Max(record.Value - __state.Penalty, 0f);
            }
        }
    }

    public static void Finalizer(State __state)
    {
        if (__state is { OriginalStdDev: >= 0f })
        {
            CoreMod.Instance.Settings.mutationStdDev = __state.OriginalStdDev;
        }
    }
}

/// <summary>Mammal serums are used up by the birth they affected.</summary>
[HarmonyPatch(typeof(Hediff_Pregnant), nameof(Hediff_Pregnant.DoBirthSpawn))]
public static class Hediff_Pregnant_DoBirthSpawn_Serums
{
    public static void Postfix(Pawn mother)
    {
        if (mother?.health == null || mother.TryGetComp<CompEggLayer>() != null)
        {
            return;
        }

        foreach (var hediff in mother.health.hediffSet.hediffs.Where(h => h.TryGetComp<HediffComp_Serum>() != null)
                     .ToList())
        {
            mother.health.RemoveHediff(hediff);
        }
    }
}
