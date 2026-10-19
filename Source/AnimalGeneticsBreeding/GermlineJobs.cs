using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace AnimalGeneticsBreeding;

/// <summary>Handlers act on animals that have a pending collect / inseminate order.</summary>
public class WorkGiver_Germline : WorkGiver_Scanner
{
    public override PathEndMode PathEndMode => PathEndMode.Touch;

    private static IEnumerable<Pawn> Ordered(Map map)
    {
        return map.mapPawns.SpawnedColonyAnimals.Where(a => a.Breeding() is { Order: not GermlineOrder.None });
    }

    public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
    {
        return Ordered(pawn.Map);
    }

    public override bool ShouldSkip(Pawn pawn, bool forced = false)
    {
        return !Ordered(pawn.Map).Any();
    }

    public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
    {
        if (t is not Pawn animal || animal == pawn || animal.Breeding() is not { } comp ||
            comp.Order == GermlineOrder.None)
        {
            return false;
        }

        if (!comp.OrderStillValid())
        {
            comp.ClearOrder();
            return false;
        }

        if (animal.Downed || animal.InMentalState || !animal.CanCasuallyInteractNow() ||
            !pawn.CanReserve(animal, 1, -1, null, forced))
        {
            return false;
        }

        if (comp.Order is GermlineOrder.Inseminate or GermlineOrder.ApplySerum)
        {
            var sample = comp.OrderSample;
            if (sample.IsForbidden(pawn) ||
                !pawn.CanReserveAndReach(sample, PathEndMode.ClosestTouch, Danger.Some, 1, -1, null, forced))
            {
                return false;
            }
        }

        return true;
    }

    public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
    {
        var comp = ((Pawn)t).Breeding();
        if (comp.Order == GermlineOrder.Collect)
        {
            return JobMaker.MakeJob(AGB_DefOf.AGB_CollectGermline, t);
        }

        var job = JobMaker.MakeJob(comp.Order == GermlineOrder.ApplySerum ? AGB_DefOf.AGB_ApplySerum : AGB_DefOf.AGB_Inseminate,
            t, comp.OrderSample);
        job.count = 1;
        return job;
    }
}

public abstract class JobDriver_GermlineBase : JobDriver
{
    protected const TargetIndex AnimalInd = TargetIndex.A;
    private const int WorkTicks = 400;
    private float progress;

    protected Pawn Animal => (Pawn)job.GetTarget(AnimalInd).Thing;

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref progress, "agbProgress");
    }

    /// <summary>Hold the animal still and work on it (same pattern as vanilla shearing).</summary>
    protected Toil WorkOnAnimal(System.Action onDone)
    {
        var toil = ToilMaker.MakeToil("AGB_WorkOnAnimal");
        toil.initAction = () =>
        {
            toil.actor.pather.StopDead();
            PawnUtility.ForceWait(Animal, 15000, null, maintainPosture: true);
        };
        toil.tickIntervalAction = delta =>
        {
            toil.actor.skills?.Learn(SkillDefOf.Animals, 0.1f * delta);
            progress += toil.actor.GetStatValue(StatDefOf.AnimalGatherSpeed) * delta;
            if (progress >= WorkTicks)
            {
                onDone();
                toil.actor.jobs.EndCurrentJob(JobCondition.Succeeded);
            }
        };
        toil.AddFinishAction(() =>
        {
            if (Animal != null && Animal.CurJobDef == JobDefOf.Wait_MaintainPosture)
            {
                Animal.jobs.EndCurrentJob(JobCondition.InterruptForced);
            }
        });
        toil.FailOnDespawnedOrNull(AnimalInd);
        toil.FailOnCannotTouch(AnimalInd, PathEndMode.Touch);
        toil.defaultCompleteMode = ToilCompleteMode.Never;
        toil.WithProgressBar(AnimalInd, () => progress / WorkTicks);
        toil.activeSkill = () => SkillDefOf.Animals;
        return toil;
    }
}

public class JobDriver_CollectGermline : JobDriver_GermlineBase
{
    public override bool TryMakePreToilReservations(bool errorOnFailed)
    {
        return pawn.Reserve(job.GetTarget(AnimalInd), job, 1, -1, null, errorOnFailed);
    }

    protected override IEnumerable<Toil> MakeNewToils()
    {
        this.FailOnDespawnedNullOrForbidden(AnimalInd);
        this.FailOnDowned(AnimalInd);
        this.FailOnNotCasualInterruptible(AnimalInd);
        this.FailOn(() => Animal.Breeding()?.Order != GermlineOrder.Collect || !GermlineUtility.CanDonate(Animal));
        yield return Toils_Goto.GotoThing(AnimalInd, PathEndMode.Touch);
        yield return WorkOnAnimal(() =>
        {
            var sample = CompGermlineSample.MakeFrom(Animal);
            GenPlace.TryPlaceThing(sample, Animal.Position, Animal.Map, ThingPlaceMode.Near);
            Animal.health.AddHediff(AGB_DefOf.AGB_RecentlySampled);
            Animal.Breeding().ClearOrder();
            Messages.Message("AGB.SampleCollected".Translate(Animal.LabelShort), sample, MessageTypeDefOf.PositiveEvent);
        });
    }
}

public class JobDriver_Inseminate : JobDriver_GermlineBase
{
    private const TargetIndex SampleInd = TargetIndex.B;

    public override bool TryMakePreToilReservations(bool errorOnFailed)
    {
        return pawn.Reserve(job.GetTarget(AnimalInd), job, 1, -1, null, errorOnFailed) &&
               pawn.Reserve(job.GetTarget(SampleInd), job, 1, -1, null, errorOnFailed);
    }

    protected override IEnumerable<Toil> MakeNewToils()
    {
        this.FailOnDespawnedNullOrForbidden(AnimalInd);
        this.FailOnDowned(AnimalInd);
        this.FailOnNotCasualInterruptible(AnimalInd);
        this.FailOn(() => Animal.Breeding()?.Order != GermlineOrder.Inseminate || !GermlineUtility.CanReceive(Animal));
        yield return Toils_Goto.GotoThing(SampleInd, PathEndMode.ClosestTouch)
            .FailOnDespawnedNullOrForbidden(SampleInd).FailOnSomeonePhysicallyInteracting(SampleInd);
        yield return Toils_Haul.StartCarryThing(SampleInd);
        yield return Toils_Goto.GotoThing(AnimalInd, PathEndMode.Touch);
        yield return WorkOnAnimal(() =>
        {
            var sample = job.GetTarget(SampleInd).Thing;
            var comp = sample?.TryGetComp<CompGermlineSample>();
            if (comp == null)
            {
                return;
            }

            var success = Rand.Chance(GermlineUtility.SuccessChance(pawn));
            if (success)
            {
                GermlineUtility.Inseminate(Animal, comp);
                Messages.Message("AGB.InseminationSucceeded".Translate(Animal.LabelShort, comp.donorName), Animal,
                    MessageTypeDefOf.PositiveEvent);
            }
            else
            {
                Messages.Message("AGB.InseminationFailed".Translate(Animal.LabelShort), Animal,
                    MessageTypeDefOf.NegativeEvent);
            }

            Animal.Breeding().ClearOrder();
            sample.Destroy();
        });
    }
}

public class JobDriver_ApplySerum : JobDriver_GermlineBase
{
    private const TargetIndex SerumInd = TargetIndex.B;

    public override bool TryMakePreToilReservations(bool errorOnFailed)
    {
        return pawn.Reserve(job.GetTarget(AnimalInd), job, 1, -1, null, errorOnFailed) &&
               pawn.Reserve(job.GetTarget(SerumInd), job, 1, 1, null, errorOnFailed);
    }

    protected override IEnumerable<Toil> MakeNewToils()
    {
        this.FailOnDespawnedNullOrForbidden(AnimalInd);
        this.FailOnDowned(AnimalInd);
        this.FailOnNotCasualInterruptible(AnimalInd);
        this.FailOn(() => Animal.Breeding()?.Order != GermlineOrder.ApplySerum);
        yield return Toils_Goto.GotoThing(SerumInd, PathEndMode.ClosestTouch)
            .FailOnDespawnedNullOrForbidden(SerumInd).FailOnSomeonePhysicallyInteracting(SerumInd);
        yield return Toils_Haul.StartCarryThing(SerumInd, false, true);
        yield return Toils_Goto.GotoThing(AnimalInd, PathEndMode.Touch);
        yield return WorkOnAnimal(() =>
        {
            var serum = pawn.carryTracker.CarriedThing;
            if (serum?.def.GetModExtension<SerumExtension>() == null)
            {
                return;
            }

            var comp = Animal.Breeding();
            var gene = comp.OrderGene == null
                ? null
                : AnimalGenetics.Constants.AffectedStats.FirstOrDefault(s => s.defName == comp.OrderGene);
            HediffComp_Serum.Apply(Animal, serum.def, gene);
            Messages.Message("AGB.SerumApplied".Translate(Animal.LabelShort, serum.def.label), Animal,
                MessageTypeDefOf.PositiveEvent);
            comp.ClearOrder();
            serum.SplitOff(1).Destroy();
        });
    }
}
