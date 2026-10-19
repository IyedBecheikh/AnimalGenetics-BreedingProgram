using System.Collections.Generic;
using System.Linq;
using AnimalGenetics;
using RimWorld;
using UnityEngine;
using Verse;

namespace AnimalGeneticsBreeding;

public class CompProperties_Breeding : CompProperties
{
    public CompProperties_Breeding()
    {
        compClass = typeof(CompBreeding);
    }
}

/// <summary>Holds the herd role of one animal and caches its breeding score.</summary>
public class CompBreeding : ThingComp
{
    /// <summary>Sample genetics fathering this hen's next fertilized eggs (egg-layers only).</summary>
    public GeneticInformation eggFather;

    private GermlineOrder order;
    private string orderGene;
    private Thing orderSample;
    private HerdRole role;
    private float score;
    private int scoreVersion = -1;

    public GermlineOrder Order => order;

    public Thing OrderSample => orderSample;

    public string OrderGene => orderGene;

    public void SetOrder(GermlineOrder newOrder, Thing thing = null, string gene = null)
    {
        order = newOrder;
        orderSample = newOrder is GermlineOrder.Inseminate or GermlineOrder.ApplySerum ? thing : null;
        orderGene = newOrder == GermlineOrder.ApplySerum ? gene : null;
    }

    public void ClearOrder()
    {
        SetOrder(GermlineOrder.None);
    }

    public bool OrderStillValid()
    {
        return order switch
        {
            GermlineOrder.Collect => GermlineUtility.CanDonate(Pawn),
            GermlineOrder.Inseminate => orderSample is { Destroyed: false } && GermlineUtility.CanReceive(Pawn) &&
                                        GermlineUtility.Matches(orderSample, Pawn),
            GermlineOrder.ApplySerum => orderSample is { Destroyed: false } && Pawn.gender == Gender.Female &&
                                        Pawn.ageTracker.CurLifeStage.reproductive,
            _ => true
        };
    }

    public Pawn Pawn => parent as Pawn;

    public HerdRole Role => role;

    public float Score
    {
        get
        {
            if (scoreVersion == BreedingProgram.Version)
            {
                return score;
            }

            score = BreedingScore.Compute(Pawn);
            scoreVersion = BreedingProgram.Version;
            return score;
        }
    }

    public void SetRole(HerdRole newRole)
    {
        if (role == newRole)
        {
            return;
        }

        role = newRole;
        BreedingProgram.Notify_RolesChanged();
    }

    public override void PostExposeData()
    {
        base.PostExposeData();
        Scribe_Values.Look(ref role, "agbHerdRole");
        Scribe_Values.Look(ref order, "agbGermlineOrder");
        Scribe_References.Look(ref orderSample, "agbOrderSample");
        Scribe_Values.Look(ref orderGene, "agbOrderGene");
        Scribe_References.Look(ref eggFather, "agbEggFather");
    }

    public override IEnumerable<Gizmo> CompGetGizmosExtra()
    {
        if (!BreedingUtility.IsManagedAnimal(Pawn))
        {
            yield break;
        }

        yield return new Command_HerdRole(this);
        if (!GermlineUtility.Unlocked)
        {
            yield break;
        }

        foreach (var gizmo in GermlineGizmos.For(this))
        {
            yield return gizmo;
        }
    }

    public override string CompInspectStringExtra()
    {
        var pawn = Pawn;
        if (!BreedingUtility.IsManagedAnimal(pawn))
        {
            return null;
        }

        var text = "AGB.InspectScore".Translate(BreedingScore.Format(Score),
            BreedingPresets.LabelFor(BreedingProgram.Get(pawn.def).presetKey)).Resolve();
        if (role != HerdRole.None)
        {
            text += ", " + role.Label();
        }

        if (BreedingRules.IsTopMale(pawn))
        {
            text += ", " + "AGB.TopMale".Translate();
        }

        return text;
    }
}

public static class BreedingUtility
{
    /// <summary>A player-owned animal that Animal Genetics tracks.</summary>
    public static bool IsManagedAnimal(Pawn pawn)
    {
        return pawn != null && pawn.RaceProps.Animal && pawn.Faction == Faction.OfPlayer &&
               pawn.AnimalGenetics() != null;
    }

    public static CompBreeding Breeding(this Pawn pawn)
    {
        return pawn?.TryGetComp<CompBreeding>();
    }

    public static HerdRole HerdRole(this Pawn pawn)
    {
        return pawn.Breeding()?.Role ?? AnimalGeneticsBreeding.HerdRole.None;
    }

    public static float BreedingScoreOf(this Pawn pawn)
    {
        return pawn.Breeding()?.Score ?? BreedingScore.Compute(pawn);
    }

    /// <summary>Stats Animal Genetics applies to animals of this species, in display order.</summary>
    public static IEnumerable<StatDef> GenesFor(ThingDef def)
    {
        var gatherable = global::AnimalGenetics.HarmonyPatches.AnimalGeneticsAssemblyLoader.GatherableTypes
            .Any(t => t != null && def.HasComp(t));
        return AgStats.Affected.Where(s => s != global::AnimalGenetics.AnimalGenetics.GatherYield || gatherable);
    }
}

public static class BreedingScore
{
    /// <summary>
    ///     Weighted average gene deviation from 100%, using the species' breeding goal.
    ///     0 = average animal, +0.2 = genes 20% better than baseline on what you care about.
    /// </summary>
    public static float Compute(Pawn pawn)
    {
        if (pawn?.AnimalGenetics() == null)
        {
            return 0f;
        }

        var program = BreedingProgram.Get(pawn.def);
        float sum = 0f, weightSum = 0f;
        foreach (var stat in pawn.GetGenes())
        {
            var weight = program.GetWeight(stat);
            if (weight <= 0f)
            {
                continue;
            }

            var value = Genes.GetGene(pawn, stat);
            var deviation = AgStats.IsInverted(stat) ? 1f - value : value - 1f;
            sum += weight * deviation;
            weightSum += weight;
        }

        return weightSum > 0f ? sum / weightSum : 0f;
    }

    /// <summary>Score for arbitrary gene values (ancestors, predicted children) using the species' goal.</summary>
    public static float FromValues(ThingDef species, System.Func<StatDef, float> valueOf)
    {
        var program = BreedingProgram.Get(species);
        float sum = 0f, weightSum = 0f;
        foreach (var stat in BreedingUtility.GenesFor(species))
        {
            var weight = program.GetWeight(stat);
            if (weight <= 0f)
            {
                continue;
            }

            var value = valueOf(stat);
            sum += weight * (AgStats.IsInverted(stat) ? 1f - value : value - 1f);
            weightSum += weight;
        }

        return weightSum > 0f ? sum / weightSum : 0f;
    }

    public static string Format(float score)
    {
        return (score >= 0f ? "+" : "") + (score * 100f).ToString("F0") + "%";
    }

    public static Color ColorFor(float score)
    {
        return Utilities.TextColor(1f + score);
    }
}
