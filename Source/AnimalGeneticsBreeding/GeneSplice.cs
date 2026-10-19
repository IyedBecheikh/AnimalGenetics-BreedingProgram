using System.Collections.Generic;
using System.Linq;
using AnimalGenetics;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AnimalGeneticsBreeding;

public class CompProperties_GeneSplice : CompProperties
{
    public CompProperties_GeneSplice()
    {
        compClass = typeof(CompGeneSplice);
    }
}

/// <summary>
///     Gene splice on a cryobank: move one gene from a donor sample into a target sample, producing a new sample.
///     Transfers <see cref="TransferFraction" /> of the difference, so it speeds breeding up without skipping it;
///     the donor sample is used up.
/// </summary>
public class CompGeneSplice : ThingComp
{
    public const float TransferFraction = 0.75f;

    public Thing donor;
    public string gene;
    public Thing target;

    public bool Pending => target != null && donor != null && gene != null;

    public StatDef Gene => Constants.AffectedStats.FirstOrDefault(s => s.defName == gene);

    public List<Thing> StoredSamples => parent.Spawned
        ? parent.Position.GetThingList(parent.Map).Where(t => t.def == AGB_DefOf.AGB_GermlineSample).ToList()
        : [];

    public bool OrderStillValid()
    {
        var stored = StoredSamples;
        return Pending && stored.Contains(target) && stored.Contains(donor) && Gene != null;
    }

    public void Clear()
    {
        target = donor = null;
        gene = null;
    }

    public override void PostExposeData()
    {
        base.PostExposeData();
        Scribe_References.Look(ref target, "agbSpliceTarget");
        Scribe_References.Look(ref donor, "agbSpliceDonor");
        Scribe_Values.Look(ref gene, "agbSpliceGene");
    }

    public static float SplicedValue(float targetValue, float donorValue)
    {
        return targetValue + (TransferFraction * (donorValue - targetValue));
    }

    /// <summary>Creates the spliced sample (a clone of the target with one gene moved toward the donor).</summary>
    public static Thing MakeSpliced(CompGermlineSample targetComp, CompGermlineSample donorComp, StatDef stat)
    {
        var records = targetComp.genetics.GeneRecords.ToDictionary(kv => kv.Key,
            kv => new GeneRecord { Value = kv.Value.Value, Parent = kv.Value.Parent });
        if (records.TryGetValue(stat, out var record) && donorComp.genetics.GeneRecords.TryGetValue(stat, out var d))
        {
            record.Value = SplicedValue(record.Value, d.Value);
        }

        var gi = new GeneticInformation(records)
        {
            Mother = targetComp.genetics.Mother,
            Father = targetComp.genetics.Father
        };

        var thing = ThingMaker.MakeThing(AGB_DefOf.AGB_GermlineSample);
        var comp = thing.TryGetComp<CompGermlineSample>();
        comp.genetics = gi;
        comp.species = targetComp.species;
        comp.donorName = "AGB.SplicedName".Translate(targetComp.donorName, Constants.GetLabel(stat));
        return thing;
    }

    public void Complete()
    {
        var t = target.TryGetComp<CompGermlineSample>();
        var d = donor.TryGetComp<CompGermlineSample>();
        var stat = Gene;
        var spliced = MakeSpliced(t, d, stat);
        var pos = parent.Position;
        var map = parent.Map;
        target.Destroy();
        donor.Destroy();
        GenSpawn.Spawn(spliced, pos, map);
        Messages.Message("AGB.SpliceDone".Translate(spliced.LabelShort), spliced, MessageTypeDefOf.PositiveEvent);
        Clear();
    }

    public override IEnumerable<Gizmo> CompGetGizmosExtra()
    {
        if (parent.Faction != Faction.OfPlayer || !GermlineUtility.TuningUnlocked)
        {
            yield break;
        }

        var command = new Command_Action
        {
            defaultLabel = Pending ? "AGB.CommandSpliceCancel".Translate() : "AGB.CommandSplice".Translate(),
            defaultDesc = Pending
                ? "AGB.CommandSplicePending".Translate(target.LabelShort, donor.LabelShort, Constants.GetLabel(Gene))
                : "AGB.CommandSpliceDesc".Translate((TransferFraction * 100f).ToString("F0")),
            icon = ContentFinder<Texture2D>.Get("UI/AGB/GeneSplice"),
            action = () =>
            {
                if (Pending)
                {
                    Clear();
                }
                else
                {
                    Find.WindowStack.Add(new Dialog_GeneSplice(this));
                }
            }
        };
        if (!Pending && !StoredSamples.GroupBy(s => s.TryGetComp<CompGermlineSample>().species).Any(g => g.Count() >= 2))
        {
            command.Disable("AGB.SpliceNeedsTwo".Translate());
        }

        yield return command;
    }

    public override string CompInspectStringExtra()
    {
        var count = StoredSamples.Count;
        var text = "AGB.CryobankCount".Translate(count).Resolve();
        if (Pending)
        {
            text += "\n" + "AGB.SplicePendingShort".Translate(Constants.GetLabel(Gene)).Resolve();
        }

        return text;
    }
}

public class Dialog_GeneSplice : Window
{
    private readonly CompGeneSplice comp;
    private Thing donor;
    private StatDef gene;
    private Vector2 scrollA, scrollB;
    private Thing target;

    public Dialog_GeneSplice(CompGeneSplice comp)
    {
        this.comp = comp;
        doCloseX = true;
        forcePause = true;
        absorbInputAroundWindow = true;
    }

    public override Vector2 InitialSize => new(860f, 560f);

    private static CompGermlineSample C(Thing t)
    {
        return t?.TryGetComp<CompGermlineSample>();
    }

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 32f), "AGB.SpliceTitle".Translate());
        Text.Font = GameFont.Small;
        Widgets.Label(new Rect(inRect.x, inRect.y + 34f, inRect.width, 22f),
            "AGB.SpliceExplain".Translate((CompGeneSplice.TransferFraction * 100f).ToString("F0")));

        var samples = comp.StoredSamples;
        var top = inRect.y + 64f;
        var colW = (inRect.width - 24f) / 3f;
        var listH = inRect.height - top - 50f;

        DrawList(new Rect(inRect.x, top, colW, listH), "AGB.SpliceTarget".Translate(), samples, ref target, ref scrollA);
        DrawList(new Rect(inRect.x + colW + 12f, top, colW, listH), "AGB.SpliceDonor".Translate(),
            samples.Where(s => s != target && target != null && C(s).species == C(target).species).ToList(), ref donor,
            ref scrollB);
        if (donor != null && (donor == target || target == null || C(donor).species != C(target).species))
        {
            donor = null;
        }

        // Gene picker with preview
        var geneRect = new Rect(inRect.x + (2 * (colW + 12f)), top, colW, listH);
        Widgets.DrawMenuSection(geneRect);
        var inner = geneRect.ContractedBy(6f);
        Widgets.Label(new Rect(inner.x, inner.y, inner.width, 24f), "AGB.SpliceGene".Translate());
        var y = inner.y + 26f;
        if (target != null && donor != null)
        {
            var t = C(target).genetics.GeneRecords;
            var d = C(donor).genetics.GeneRecords;
            foreach (var stat in BreedingUtility.GenesFor(C(target).species))
            {
                if (!t.TryGetValue(stat, out var tv) || !d.TryGetValue(stat, out var dv))
                {
                    continue;
                }

                var row = new Rect(inner.x, y, inner.width, 26f);
                if (stat == gene)
                {
                    Widgets.DrawHighlightSelected(row);
                }
                else if (Mouse.IsOver(row))
                {
                    Widgets.DrawHighlight(row);
                }

                var after = CompGeneSplice.SplicedValue(tv.Value, dv.Value);
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(row, $"{Constants.GetLabel(stat).CapitalizeFirst()}: {tv.Value * 100f:F0}% → {after * 100f:F0}%");
                Text.Anchor = TextAnchor.UpperLeft;
                if (Widgets.ButtonInvisible(row))
                {
                    gene = stat;
                }

                y += 26f;
            }
        }
        else
        {
            GUI.color = Color.gray;
            Widgets.Label(new Rect(inner.x, y, inner.width, 60f), "AGB.SplicePickFirst".Translate());
            GUI.color = Color.white;
        }

        var ok = target != null && donor != null && gene != null;
        if (Widgets.ButtonText(new Rect(inRect.xMax - 200f, inRect.yMax - 36f, 200f, 34f), "AGB.SpliceConfirm".Translate(),
                active: ok) && ok)
        {
            comp.target = target;
            comp.donor = donor;
            comp.gene = gene.defName;
            Close();
        }
    }

    private static void DrawList(Rect rect, string title, List<Thing> things, ref Thing selected, ref Vector2 scroll)
    {
        Widgets.DrawMenuSection(rect);
        var inner = rect.ContractedBy(6f);
        Widgets.Label(new Rect(inner.x, inner.y, inner.width, 24f), title);
        var listRect = new Rect(inner.x, inner.y + 26f, inner.width, inner.height - 26f);
        var view = new Rect(0f, 0f, listRect.width - 16f, things.Count * 26f);
        Widgets.BeginScrollView(listRect, ref scroll, view);
        var y = 0f;
        foreach (var thing in things)
        {
            var c = C(thing);
            var row = new Rect(0f, y, view.width, 26f);
            if (thing == selected)
            {
                Widgets.DrawHighlightSelected(row);
            }
            else if (Mouse.IsOver(row))
            {
                Widgets.DrawHighlight(row);
            }

            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(row, $"{c.donorName} ({c.species.label}) {BreedingScore.Format(c.Score)}".Truncate(row.width));
            Text.Anchor = TextAnchor.UpperLeft;
            if (Widgets.ButtonInvisible(row))
            {
                selected = thing;
            }

            y += 26f;
        }

        Widgets.EndScrollView();
    }
}

public class WorkGiver_GeneSplice : WorkGiver_Scanner
{
    public override PathEndMode PathEndMode => PathEndMode.Touch;

    public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
    {
        return pawn.Map.listerThings.ThingsOfDef(AGB_DefOf.AGB_Cryobank)
            .Where(b => b.TryGetComp<CompGeneSplice>()?.Pending == true);
    }

    public override bool ShouldSkip(Pawn pawn, bool forced = false)
    {
        return !PotentialWorkThingsGlobal(pawn).Any();
    }

    public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
    {
        var comp = t.TryGetComp<CompGeneSplice>();
        if (comp is not { Pending: true })
        {
            return false;
        }

        if (!comp.OrderStillValid())
        {
            comp.Clear();
            return false;
        }

        var power = t.TryGetComp<CompPowerTrader>();
        return (power == null || power.PowerOn) && !t.IsForbidden(pawn) && pawn.CanReserve(t, 1, -1, null, forced);
    }

    public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
    {
        return JobMaker.MakeJob(AGB_DefOf.AGB_GeneSplice, t);
    }
}

public class JobDriver_GeneSplice : JobDriver
{
    private const float WorkTotal = 2500f;
    private float progress;

    public override bool TryMakePreToilReservations(bool errorOnFailed)
    {
        return pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref progress, "agbProgress");
    }

    protected override IEnumerable<Toil> MakeNewToils()
    {
        this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
        this.FailOn(() => job.targetA.Thing.TryGetComp<CompGeneSplice>()?.OrderStillValid() != true);
        yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
        var work = ToilMaker.MakeToil("AGB_GeneSplice");
        work.tickIntervalAction = delta =>
        {
            var actor = work.actor;
            actor.skills?.Learn(SkillDefOf.Medicine, 0.08f * delta);
            progress += actor.GetStatValue(StatDefOf.MedicalTendSpeed) * delta;
            if (progress >= WorkTotal)
            {
                job.targetA.Thing.TryGetComp<CompGeneSplice>().Complete();
                actor.jobs.EndCurrentJob(JobCondition.Succeeded);
            }
        };
        work.defaultCompleteMode = ToilCompleteMode.Never;
        work.WithProgressBar(TargetIndex.A, () => progress / WorkTotal);
        work.WithEffect(EffecterDefOf.Research, TargetIndex.A);
        work.FailOnCannotTouch(TargetIndex.A, PathEndMode.Touch);
        work.activeSkill = () => SkillDefOf.Medicine;
        yield return work;
    }
}
