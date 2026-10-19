using System.Collections.Generic;
using System.Linq;
using AnimalGenetics;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AnimalGeneticsBreeding;

/// <summary>On serum operations: which gene a dominance serum targets.</summary>
public class SerumGeneExtension : DefModExtension
{
    public string gene;
}

/// <summary>Health-tab operations share the germline research gates and only show on your animals.</summary>
public abstract class Recipe_GermlineBase : Recipe_Surgery
{
    protected virtual bool NeedsTuning => false;

    public override bool AvailableOnNow(Thing thing, BodyPartRecord part = null)
    {
        return thing is Pawn pawn && BreedingUtility.IsManagedAnimal(pawn) && GermlineUtility.Unlocked &&
               (!NeedsTuning || GermlineUtility.TuningUnlocked) && Available(pawn) && base.AvailableOnNow(thing, part);
    }

    protected abstract bool Available(Pawn pawn);

    public override bool IsViolationOnPawn(Pawn pawn, BodyPartRecord part, Faction billDoerFaction)
    {
        return false;
    }
}

public class Recipe_CollectGermline : Recipe_GermlineBase
{
    protected override bool Available(Pawn pawn)
    {
        return pawn.gender == Gender.Male && pawn.ageTracker.CurLifeStage.reproductive && !pawn.Sterile();
    }

    public override void ApplyOnPawn(Pawn pawn, BodyPartRecord part, Pawn billDoer, List<Thing> ingredients, Bill bill)
    {
        if (!GermlineUtility.CanDonate(pawn))
        {
            Messages.Message("AGB.RecentlySampled".Translate(), pawn, MessageTypeDefOf.RejectInput);
            return;
        }

        var sample = CompGermlineSample.MakeFrom(pawn);
        GenPlace.TryPlaceThing(sample, pawn.PositionHeld, pawn.MapHeld, ThingPlaceMode.Near);
        pawn.health.AddHediff(AGB_DefOf.AGB_RecentlySampled);
        Messages.Message("AGB.SampleCollected".Translate(pawn.LabelShort), sample, MessageTypeDefOf.PositiveEvent);
    }
}

/// <summary>Uses the best matching sample on the map (highest expected child score).</summary>
public class Recipe_Inseminate : Recipe_GermlineBase
{
    protected override bool Available(Pawn pawn)
    {
        return pawn.gender == Gender.Female && pawn.ageTracker.CurLifeStage.reproductive && !pawn.Sterile();
    }

    public override void ApplyOnPawn(Pawn pawn, BodyPartRecord part, Pawn billDoer, List<Thing> ingredients, Bill bill)
    {
        var sample = ingredients?.Select(i => i.TryGetComp<CompGermlineSample>()).FirstOrDefault(c => c != null);
        if (sample == null || !GermlineUtility.CanReceive(pawn) || !GermlineUtility.Matches(sample.parent, pawn))
        {
            Messages.Message("AGB.CannotReceive".Translate(), pawn, MessageTypeDefOf.RejectInput);
            return;
        }

        if (Rand.Chance(GermlineUtility.SuccessChance(billDoer, SkillDefOf.Medicine)))
        {
            GermlineUtility.Inseminate(pawn, sample);
            Messages.Message("AGB.InseminationSucceeded".Translate(pawn.LabelShort, sample.donorName), pawn,
                MessageTypeDefOf.PositiveEvent);
        }
        else
        {
            Messages.Message("AGB.InseminationFailed".Translate(pawn.LabelShort), pawn, MessageTypeDefOf.NegativeEvent);
        }
    }

    /// <summary>Best usable (not forbidden) sample for this female right now, or null.</summary>
    public static Thing BestSampleFor(Pawn female)
    {
        var gi = female.AnimalGenetics();
        return female.MapHeld?.listerThings.ThingsOfDef(AGB_DefOf.AGB_GermlineSample)
            .Where(s => GermlineUtility.Matches(s, female) && !s.IsForbidden(Faction.OfPlayer))
            .OrderByDescending(s => OffspringModel.ExpectedScore(female.def, s.TryGetComp<CompGermlineSample>().genetics, gi))
            .FirstOrDefault();
    }
}

public class Recipe_ApplySerum : Recipe_GermlineBase
{
    protected override bool NeedsTuning => true;

    protected override bool Available(Pawn pawn)
    {
        return pawn.gender == Gender.Female && pawn.ageTracker.CurLifeStage.reproductive;
    }

    public override void ApplyOnPawn(Pawn pawn, BodyPartRecord part, Pawn billDoer, List<Thing> ingredients, Bill bill)
    {
        var serum = ingredients?.FirstOrDefault(i => i.def.GetModExtension<SerumExtension>() != null);
        if (serum == null)
        {
            return;
        }

        var geneName = recipe.GetModExtension<SerumGeneExtension>()?.gene;
        var gene = geneName == null ? null : AgStats.Affected.FirstOrDefault(s => s.defName == geneName);
        HediffComp_Serum.Apply(pawn, serum.def, gene);
        Messages.Message("AGB.SerumApplied".Translate(pawn.LabelShort, serum.def.label), pawn, MessageTypeDefOf.PositiveEvent);
    }
}

/// <summary>Insemination bills only take a sample that matches the patient: the best one for her.</summary>
[HarmonyPatch(typeof(Bill), nameof(Bill.IsFixedOrAllowedIngredient), typeof(Thing))]
public static class Bill_IsFixedOrAllowedIngredient
{
    public static void Postfix(Bill __instance, Thing thing, ref bool __result)
    {
        if (!__result || __instance.recipe.Worker is not Recipe_Inseminate || __instance is not Bill_Medical medical ||
            thing.def != AGB_DefOf.AGB_GermlineSample)
        {
            return;
        }

        var patient = medical.GiverPawn;
        __result = patient != null && thing == Recipe_Inseminate.BestSampleFor(patient);
    }
}
