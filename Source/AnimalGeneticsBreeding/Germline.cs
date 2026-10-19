using System.Linq;
using System.Text;
using AnimalGenetics;
using RimWorld;
using UnityEngine;
using Verse;

namespace AnimalGeneticsBreeding;

[DefOf]
public static class AGB_DefOf
{
    public static ResearchProjectDef AGB_GermlineBanking;
    public static ThingDef AGB_GermlineSample;
    public static ThingDef AGB_Cryobank;
    public static HediffDef AGB_RecentlySampled;
    public static JobDef AGB_CollectGermline;
    public static JobDef AGB_Inseminate;
    public static JobDef AGB_ApplySerum;
    public static JobDef AGB_GeneSplice;
    public static ResearchProjectDef AGB_GermlineTuning;

    static AGB_DefOf()
    {
        DefOfHelper.EnsureInitializedInCtor(typeof(AGB_DefOf));
    }
}

public enum GermlineOrder
{
    None,
    Collect,
    Inseminate,
    ApplySerum
}

public class CompProperties_GermlineSample : CompProperties
{
    public int viableDays = 3;

    public CompProperties_GermlineSample()
    {
        compClass = typeof(CompGermlineSample);
    }
}

/// <summary>
///     One animal's germline. Holds a reference to the donor's own GeneticInformation (not a copy), so children
///     made from it link to the donor in Animal Genetics' parent graph and in the pedigree.
/// </summary>
public class CompGermlineSample : ThingComp
{
    public string donorName;
    public GeneticInformation genetics;
    public ThingDef species;
    public int ticksViable = -1;

    private CompProperties_GermlineSample Props => (CompProperties_GermlineSample)props;

    public float Score => genetics?._geneRecords == null
        ? 0f
        : BreedingScore.FromValues(species, s => genetics._geneRecords.TryGetValue(s, out var r) ? r.Value : 1f);

    private Building Cryobank => parent.Spawned && parent.Map.edificeGrid[parent.Position] is Building b &&
                                 b.def == AGB_DefOf.AGB_Cryobank
        ? b
        : null;

    /// <summary>Samples inside a cryobank are stored out of sight (the cabinet art already shows racks of vials).</summary>
    public override bool DontDrawParent()
    {
        return Cryobank != null;
    }

    public bool Preserved
    {
        get
        {
            if (Cryobank is { } building)
            {
                var power = building.GetComp<CompPowerTrader>();
                if (power == null || power.PowerOn)
                {
                    return true;
                }
            }

            return parent.AmbientTemperature < 0f;
        }
    }

    public static Thing MakeFrom(Pawn donor)
    {
        var thing = ThingMaker.MakeThing(AGB_DefOf.AGB_GermlineSample);
        var comp = thing.TryGetComp<CompGermlineSample>();
        comp.genetics = donor.AnimalGenetics();
        comp.species = donor.def;
        comp.donorName = donor.LabelShortCap;
        return thing;
    }

    public override void PostPostMake()
    {
        base.PostPostMake();
        ticksViable = Props.viableDays * GenDate.TicksPerDay;
    }

    public override void PostExposeData()
    {
        base.PostExposeData();
        Scribe_References.Look(ref genetics, "genetics");
        Scribe_Defs.Look(ref species, "species");
        Scribe_Values.Look(ref donorName, "donorName");
        Scribe_Values.Look(ref ticksViable, "ticksViable", -1);
    }

    public override void CompTickRare()
    {
        base.CompTickRare();
        if (ticksViable < 0 || Preserved)
        {
            return;
        }

        ticksViable -= GenTicks.TickRareInterval;
        if (ticksViable > 0)
        {
            return;
        }

        if (parent.Spawned)
        {
            Messages.Message("AGB.SampleExpired".Translate(parent.LabelShort), new TargetInfo(parent.Position, parent.Map),
                MessageTypeDefOf.NegativeEvent);
        }

        parent.Destroy();
    }

    public override string TransformLabel(string label)
    {
        return species == null ? label : "AGB.SampleLabel".Translate(label, species.label, donorName ?? "?");
    }

    public override bool AllowStackWith(Thing other)
    {
        return false;
    }

    public override string CompInspectStringExtra()
    {
        if (species == null || genetics == null)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.Append("AGB.SampleScore".Translate(BreedingScore.Format(Score)));
        sb.AppendLine();
        sb.Append(Preserved
            ? "AGB.SamplePreserved".Translate()
            : "AGB.SampleViable".Translate((ticksViable / (float)GenDate.TicksPerDay).ToString("0.0")));
        var records = genetics._geneRecords;
        if (records != null)
        {
            sb.AppendLine();
            sb.Append(string.Join(", ", BreedingUtility.GenesFor(species)
                .Where(records.ContainsKey)
                .Select(s => $"{Constants.GetLabel(s)} {records[s].Value * 100f:F0}%")));
        }

        return sb.ToString();
    }
}

public static class GermlineUtility
{
    private static readonly AccessTools_FatherField fatherField = new();

    public static bool Unlocked => AGB_DefOf.AGB_GermlineBanking.IsFinished;

    public static bool TuningUnlocked => AGB_DefOf.AGB_GermlineTuning.IsFinished;

    public static bool CanDonate(Pawn pawn)
    {
        return pawn.gender == Gender.Male && pawn.ageTracker.CurLifeStage.reproductive && !pawn.Sterile() &&
               !pawn.health.hediffSet.HasHediff(AGB_DefOf.AGB_RecentlySampled);
    }

    public static bool CanReceive(Pawn pawn)
    {
        if (pawn.gender != Gender.Female || !pawn.ageTracker.CurLifeStage.reproductive || pawn.Sterile() ||
            pawn.health.hediffSet.HasHediff(HediffDefOf.Pregnant))
        {
            return false;
        }

        var eggs = pawn.TryGetComp<CompEggLayer>();
        return eggs == null || !eggs.FullyFertilized;
    }

    public static bool Matches(Thing sample, Pawn female)
    {
        var comp = sample?.TryGetComp<CompGermlineSample>();
        return comp?.genetics != null && comp.species != null &&
               (comp.species == female.def || female.RaceProps.canCrossBreedWith?.Contains(comp.species) == true);
    }

    public static float SuccessChance(Pawn worker, SkillDef skillDef = null)
    {
        var skill = worker?.skills?.GetSkill(skillDef ?? SkillDefOf.Animals)?.Level ?? 0;
        return Mathf.Clamp(0.6f + (0.03f * skill), 0.6f, 0.95f);
    }

    /// <summary>Makes the female carry offspring fathered by the sample's donor genetics.</summary>
    public static void Inseminate(Pawn female, CompGermlineSample sample)
    {
        var eggs = female.TryGetComp<CompEggLayer>();
        if (eggs != null)
        {
            // Vanilla fertilization with no living male; each egg it produces gets the sample's genetics as
            // father (see CompEggLayer_ProduceEgg).
            eggs.Fertilize(null);
            var comp = female.Breeding();
            if (comp != null)
            {
                comp.eggFather = sample.genetics;
            }

            return;
        }

        var pregnancy = (Hediff_Pregnant)HediffMaker.MakeHediff(HediffDefOf.Pregnant, female);
        pregnancy.SetParents(null, null, null);
        female.health.AddHediff(pregnancy);
        // Animal Genetics reads the father's genetics from this hediff comp at birth when there is no father pawn.
        var fatherComp = pregnancy.TryGetComp<FatherGeneticInformation>();
        if (fatherComp != null)
        {
            fatherField.Set(fatherComp, sample.genetics);
        }
    }

    private class AccessTools_FatherField
    {
        private readonly HarmonyLib.AccessTools.FieldRef<FatherGeneticInformation, GeneticInformation> field =
            HarmonyLib.AccessTools.FieldRefAccess<FatherGeneticInformation, GeneticInformation>("geneticInformation");

        public void Set(FatherGeneticInformation comp, GeneticInformation gi)
        {
            field(comp) = gi;
        }
    }
}

/// <summary>Stamps the sample's genetics as father on eggs from a sample-fertilized hen.</summary>
[HarmonyLib.HarmonyPatch(typeof(CompEggLayer), nameof(CompEggLayer.ProduceEgg))]
public static class CompEggLayer_ProduceEgg
{
    private static readonly HarmonyLib.AccessTools.FieldRef<CompEggLayer, Pawn> fertilizedBy =
        HarmonyLib.AccessTools.FieldRefAccess<CompEggLayer, Pawn>("fertilizedBy");

    public static void Postfix(CompEggLayer __instance, Thing __result)
    {
        if (__result == null || __instance.parent is not Pawn hen || __result.TryGetComp<CompHatcher>() == null)
        {
            return;
        }

        var eggGenetics = __result.TryGetComp<EggGeneticInformation>()?.GeneticInformation;
        if (eggGenetics == null)
        {
            return;
        }

        var male = fertilizedBy(__instance);
        var father = male != null ? male.AnimalGenetics() : hen.Breeding()?.eggFather;
        if (father == null)
        {
            return;
        }

        // Same links Animal Genetics makes on the egg's first tick, made now so they can't be missed.
        eggGenetics.Father ??= father;
        eggGenetics.Mother ??= hen.AnimalGenetics();

        // A serum only affects eggs whose genes are rolled while it is active: roll them at laying.
        if (eggGenetics._geneRecords == null && HediffComp_Serum.For(hen.AnimalGenetics()) != null)
        {
            _ = eggGenetics.GeneRecords;
        }
    }
}

/// <summary>A real male fertilizing the hen replaces any pending sample father.</summary>
[HarmonyLib.HarmonyPatch(typeof(CompEggLayer), nameof(CompEggLayer.Fertilize))]
public static class CompEggLayer_Fertilize
{
    public static void Postfix(CompEggLayer __instance, Pawn male)
    {
        if (male != null && __instance.parent is Pawn hen && hen.Breeding() is { } comp)
        {
            comp.eggFather = null;
        }
    }
}

// ---------------------------------------------------------------- egg sorting filters

public abstract class SpecialThingFilterWorker_EggGenes : SpecialThingFilterWorker
{
    /// <summary>Predicted chick score, or null if the egg's genes can't be known yet.</summary>
    protected static float? ChickScore(Thing egg)
    {
        var gi = egg.TryGetComp<EggGeneticInformation>()?.GeneticInformation;
        var species = egg.TryGetComp<CompHatcher>()?.Props.hatcherPawn?.race;
        if (gi == null || species == null)
        {
            return null;
        }

        // Never trigger gene generation before Animal Genetics has linked the parents.
        var records = gi._geneRecords ?? (gi.Mother != null || gi.Father != null ? gi.GeneRecords : null);
        if (records == null)
        {
            return null;
        }

        return BreedingScore.FromValues(species, s => records.TryGetValue(s, out var r) ? r.Value : 1f);
    }

    public override bool CanEverMatch(ThingDef def)
    {
        return def.HasComp(typeof(CompHatcher));
    }
}

public class SpecialThingFilterWorker_EggGenes_Good : SpecialThingFilterWorker_EggGenes
{
    public override bool Matches(Thing t)
    {
        var score = ChickScore(t);
        return score == null || score >= 0f;
    }
}

public class SpecialThingFilterWorker_EggGenes_Poor : SpecialThingFilterWorker_EggGenes
{
    public override bool Matches(Thing t)
    {
        return ChickScore(t) is < 0f;
    }
}
