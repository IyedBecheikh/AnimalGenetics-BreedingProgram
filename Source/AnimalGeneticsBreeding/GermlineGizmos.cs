using System.Collections.Generic;
using System.Linq;
using AnimalGenetics;
using RimWorld;
using UnityEngine;
using Verse;

namespace AnimalGeneticsBreeding;

[StaticConstructorOnStartup]
public static class GermlineGizmos
{
    // Loaded on first use, not in a static initializer: another mod Harmony-patching these methods early would
    // otherwise run the initializer before content is loaded and leave the textures null.
    private static Texture2D CollectIcon => collectIcon ??= ContentFinder<Texture2D>.Get("UI/AGB/CollectGermline");
    private static Texture2D collectIcon;
    private static Texture2D InseminateIcon => inseminateIcon ??= ContentFinder<Texture2D>.Get("UI/AGB/Inseminate");
    private static Texture2D inseminateIcon;

    public static IEnumerable<Gizmo> For(CompBreeding comp)
    {
        var pawn = comp.Pawn;
        if (pawn.gender == Gender.Male && pawn.ageTracker.CurLifeStage.reproductive && !pawn.Sterile())
        {
            var pending = comp.Order == GermlineOrder.Collect;
            var command = new Command_Toggle
            {
                defaultLabel = "AGB.CommandCollect".Translate(),
                defaultDesc = "AGB.CommandCollectDesc".Translate(),
                icon = CollectIcon,
                isActive = () => comp.Order == GermlineOrder.Collect,
                toggleAction = () => comp.SetOrder(comp.Order == GermlineOrder.Collect
                    ? GermlineOrder.None
                    : GermlineOrder.Collect)
            };
            if (!pending && !GermlineUtility.CanDonate(pawn))
            {
                command.Disable("AGB.RecentlySampled".Translate());
            }

            yield return command;
        }

        if (pawn.gender == Gender.Female && pawn.ageTracker.CurLifeStage.reproductive && !pawn.Sterile())
        {
            yield return new Command_Inseminate(comp);
            if (GermlineUtility.TuningUnlocked)
            {
                yield return new Command_ApplySerum(comp);
            }
        }
    }

    public static List<Thing> SamplesFor(Pawn female)
    {
        return female.Map?.listerThings.ThingsOfDef(AGB_DefOf.AGB_GermlineSample)
            .Where(s => GermlineUtility.Matches(s, female))
            .OrderByDescending(s => s.TryGetComp<CompGermlineSample>().Score)
            .ToList() ?? [];
    }
}

public class Command_Inseminate : Command
{
    private readonly CompBreeding comp;

    public Command_Inseminate(CompBreeding comp)
    {
        this.comp = comp;
        var pending = comp.Order == GermlineOrder.Inseminate;
        defaultLabel = pending ? "AGB.CommandInseminateCancel".Translate() : "AGB.CommandInseminate".Translate();
        defaultDesc = pending
            ? "AGB.CommandInseminatePending".Translate(comp.OrderSample?.LabelShort ?? "?")
            : "AGB.CommandInseminateDesc".Translate();
        icon = ContentFinder<Texture2D>.Get("UI/AGB/Inseminate");
        if (!pending && !GermlineUtility.CanReceive(comp.Pawn))
        {
            Disable("AGB.CannotReceive".Translate());
        }
    }

    public override bool InheritInteractionsFrom(Gizmo other)
    {
        return false;
    }

    public override void ProcessInput(Event ev)
    {
        base.ProcessInput(ev);
        if (comp.Order == GermlineOrder.Inseminate)
        {
            comp.ClearOrder();
            return;
        }

        var female = comp.Pawn;
        var samples = GermlineGizmos.SamplesFor(female);
        if (samples.Count == 0)
        {
            Messages.Message("AGB.NoSamples".Translate(female.def.label), female, MessageTypeDefOf.RejectInput, false);
            return;
        }

        var options = samples.Select(sample =>
        {
            var s = sample.TryGetComp<CompGermlineSample>();
            var expected = OffspringModel.ExpectedScore(female.def, s.genetics, female.AnimalGenetics());
            var kin = Kinship.Coancestry(s.genetics, female.AnimalGenetics());
            var label = "AGB.SampleOption".Translate(s.donorName, BreedingScore.Format(s.Score),
                BreedingScore.Format(expected)).Resolve();
            if (kin > 0.001f)
            {
                label += "  " + "AGB.SampleOptionKin".Translate((kin * 100f).ToString("0.#")).Resolve();
            }

            return new FloatMenuOption(label, () => comp.SetOrder(GermlineOrder.Inseminate, sample));
        }).ToList();
        Find.WindowStack.Add(new FloatMenu(options));
    }
}

public class Command_ApplySerum : Command
{
    private readonly CompBreeding comp;

    public Command_ApplySerum(CompBreeding comp)
    {
        this.comp = comp;
        var pending = comp.Order == GermlineOrder.ApplySerum;
        defaultLabel = pending ? "AGB.CommandSerumCancel".Translate() : "AGB.CommandSerum".Translate();
        defaultDesc = pending
            ? "AGB.CommandSerumPending".Translate(comp.OrderSample?.LabelNoCount ?? "?")
            : "AGB.CommandSerumDesc".Translate();
        icon = ContentFinder<Texture2D>.Get("UI/AGB/ApplySerum");
    }

    public override bool InheritInteractionsFrom(Gizmo other)
    {
        return false;
    }

    public override void ProcessInput(Event ev)
    {
        base.ProcessInput(ev);
        if (comp.Order == GermlineOrder.ApplySerum)
        {
            comp.ClearOrder();
            return;
        }

        var female = comp.Pawn;
        var serums = female.Map.listerThings.AllThings
            .Where(t => t.def.GetModExtension<SerumExtension>() != null && !t.IsForbidden(Faction.OfPlayer))
            .GroupBy(t => t.def).Select(g => g.First()).ToList();
        if (serums.Count == 0)
        {
            Messages.Message("AGB.NoSerums".Translate(), female, MessageTypeDefOf.RejectInput, false);
            return;
        }

        var options = new List<FloatMenuOption>();
        foreach (var serum in serums)
        {
            if (serum.def.GetModExtension<SerumExtension>().kind == SerumKind.Dominance)
            {
                foreach (var gene in BreedingUtility.GenesFor(female.def))
                {
                    options.Add(new FloatMenuOption(
                        "AGB.SerumOptionGene".Translate(serum.def.LabelCap, AgStats.Label(gene)),
                        () => comp.SetOrder(GermlineOrder.ApplySerum, serum, gene.defName)));
                }
            }
            else
            {
                options.Add(new FloatMenuOption(serum.def.LabelCap,
                    () => comp.SetOrder(GermlineOrder.ApplySerum, serum)));
            }
        }

        Find.WindowStack.Add(new FloatMenu(options));
    }
}
