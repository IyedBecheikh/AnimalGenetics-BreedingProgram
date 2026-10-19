using System;
using System.Collections.Generic;
using System.Linq;
using AnimalGenetics;
using AnimalGeneticsBreeding;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace AGBTest;

// Tick-driven: real handler jobs need game time. Runs before the save/reload step.
public static class Phase3Tests
{
    private const int StageTimeoutTicks = 8000;

    public static bool Active;
    public static Pawn Donor;
    public static Thing KeptSample;

    private static Action<bool, string> check;
    private static Map map;
    private static List<Pawn> males, females;
    private static Pawn handler, recipient;
    private static int stage, stageStart, inseminateAttempts;
    private static Thing sample;

    private static readonly AccessTools.FieldRef<FatherGeneticInformation, GeneticInformation> fatherGi =
        AccessTools.FieldRefAccess<FatherGeneticInformation, GeneticInformation>("geneticInformation");

    public static void Start(Action<bool, string> checkFn, Map m, List<Pawn> ms, List<Pawn> fs)
    {
        check = checkFn;
        map = m;
        males = ms;
        females = fs;
        Active = true;
        stage = 0;
    }


    // Keep test workers available: full needs, no breaks, and re-issue the job if they wander off it.
    public static void KeepWorking(Pawn worker, JobDef expected, Func<Job> makeJob, int elapsed)
    {
        if (worker?.needs == null)
        {
            return;
        }

        foreach (var need in worker.needs.AllNeeds)
        {
            need.CurLevelPercentage = 1f;
        }

        if (worker.InMentalState)
        {
            worker.MentalState.RecoverFromState();
        }

        if (elapsed > 0 && elapsed % 500 == 0 && worker.CurJobDef != expected)
        {
            var job = makeJob();
            if (job != null)
            {
                worker.jobs.StartJob(job, JobCondition.InterruptForced);
            }
        }
    }

    private static void Next()
    {
        stage++;
        stageStart = Find.TickManager.TicksGame;
    }

    private static bool TimedOut(string what)
    {
        if (Find.TickManager.TicksGame - stageStart < StageTimeoutTicks)
        {
            return false;
        }

        check(false, "timed out: " + what);
        Active = false;
        return true;
    }

    private static Job JobFor(Pawn animal)
    {
        var giver = (WorkGiver_Germline)DefDatabase<WorkGiverDef>.GetNamed("AGB_Germline").Worker;
        return giver.HasJobOnThing(handler, animal, true) ? giver.JobOnThing(handler, animal, true) : null;
    }

    private static void StartJobFor(Pawn animal)
    {
        var giver = (WorkGiver_Germline)DefDatabase<WorkGiverDef>.GetNamed("AGB_Germline").Worker;
        var ok = giver.HasJobOnThing(handler, animal, true);
        check(ok, $"work giver offers a job for {animal.LabelShort} ({animal.Breeding().Order})");
        if (ok)
        {
            handler.jobs.StartJob(giver.JobOnThing(handler, animal, true), JobCondition.InterruptForced);
        }
    }

    public static void Tick()
    {
        switch (stage)
        {
            case 0:
                Setup();
                Next();
                break;
            case 1: // wait for the collected sample
                sample = map.listerThings.ThingsOfDef(AGB_DefOf.AGB_GermlineSample)
                    .FirstOrDefault(s => s.TryGetComp<CompGermlineSample>().genetics == Donor.AnimalGenetics());
                if (sample == null)
                {
                    KeepWorking(handler, AGB_DefOf.AGB_CollectGermline, () => JobFor(Donor),
                        Find.TickManager.TicksGame - stageStart);
                    TimedOut("sample collection job");
                    return;
                }

                CheckCollected();
                Next();
                break;
            case 2:
                StartInsemination();
                Next();
                break;
            case 3: // wait for insemination outcome
                if (recipient.health.hediffSet.HasHediff(HediffDefOf.Pregnant))
                {
                    CheckPregnancy();
                    Next();
                }
                else if (recipient.Breeding().Order == GermlineOrder.None && handler.CurJobDef != AGB_DefOf.AGB_Inseminate)
                {
                    // Failed roll (5% at skill 20): try again with a fresh sample.
                    if (++inseminateAttempts >= 4)
                    {
                        check(false, "insemination never succeeded");
                        Active = false;
                        return;
                    }

                    stage = 2;
                }
                else
                {
                    KeepWorking(handler, AGB_DefOf.AGB_Inseminate, () => JobFor(recipient),
                        Find.TickManager.TicksGame - stageStart);
                    TimedOut("insemination job");
                }

                break;
            case 4:
                EggLayerAndStorageTests();
                Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                Active = false;
                break;
        }
    }

    private static void Setup()
    {
        Find.ResearchManager.FinishProject(AGB_DefOf.AGB_GermlineBanking);
        check(GermlineUtility.Unlocked, "germline research can be completed");

        Donor = males[2];
        recipient = females[3];
        foreach (var p in new[] { Donor, recipient })
        {
            p.Breeding().SetRole(HerdRole.None);
            p.Breeding().ClearOrder();
        }

        var maleGizmos = Donor.Breeding().CompGetGizmosExtra().ToList();
        var femaleGizmos = recipient.Breeding().CompGetGizmosExtra().ToList();
        check(maleGizmos.Any(g => g is Command_Toggle), "male shows Collect sample gizmo");
        check(femaleGizmos.Any(g => g is Command_Inseminate), "female shows Inseminate gizmo");
        check(!femaleGizmos.Any(g => g is Command_Toggle), "female has no Collect gizmo");

        handler = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(c => !c.WorkTypeIsDisabled(WorkTypeDefOf.Handling));
        check(handler != null, "a colonist can do Handling");
        if (handler == null)
        {
            Active = false;
            return;
        }

        handler.skills.GetSkill(SkillDefOf.Animals).Level = 20;
        handler.Position = Donor.Position;
        handler.Notify_Teleported();
        Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;

        Donor.Breeding().SetOrder(GermlineOrder.Collect);
        StartJobFor(Donor);
    }

    private static void CheckCollected()
    {
        var comp = sample.TryGetComp<CompGermlineSample>();
        check(comp.species == Donor.def, "sample records donor species");
        check(Donor.health.hediffSet.HasHediff(AGB_DefOf.AGB_RecentlySampled), "donor gets the recently-sampled cooldown");
        check(Donor.Breeding().Order == GermlineOrder.None, "collect order cleared after the job");
        check(!GermlineUtility.CanDonate(Donor), "donor can't be sampled again during cooldown");
        check(comp.ticksViable > 0 && comp.ticksViable <= 3 * GenDate.TicksPerDay, $"sample starts viable ({comp.ticksViable} ticks)");
        check(sample.LabelShort.Contains(Donor.LabelShortCap), $"sample label names the donor: {sample.LabelShort}");
        Log.Message("[AGBTEST] sample inspect:\n" + comp.CompInspectStringExtra());
    }

    private static void StartInsemination()
    {
        var useSample = inseminateAttempts == 0 ? sample : GenSpawn.Spawn(CompGermlineSample.MakeFrom(Donor), Donor.Position, map);
        check(GermlineUtility.Matches(useSample, recipient), "sample matches the female's species");
        check(GermlineGizmos.SamplesFor(recipient).Contains(useSample), "sample is offered in the Inseminate menu");
        recipient.Breeding().SetOrder(GermlineOrder.Inseminate, useSample);
        handler.Position = useSample.Position;
        handler.Notify_Teleported();
        StartJobFor(recipient);
    }

    private static void CheckPregnancy()
    {
        var pregnancy = (Hediff_Pregnant)recipient.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.Pregnant);
        var fatherComp = pregnancy.TryGetComp<FatherGeneticInformation>();
        check(fatherComp != null && fatherGi(fatherComp) == Donor.AnimalGenetics(),
            "pregnancy carries the donor's genetics as father");
        check(map.listerThings.ThingsOfDef(AGB_DefOf.AGB_GermlineSample).All(s =>
                  s.TryGetComp<CompGermlineSample>().genetics != Donor.AnimalGenetics() || s != recipient.Breeding().OrderSample),
            "used sample consumed");

        var before = map.mapPawns.AllPawnsSpawned.ToHashSet();
        Hediff_Pregnant.DoBirthSpawn(recipient, null);
        var calf = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => !before.Contains(p) && p.def == recipient.def);
        check(calf != null, "birth from insemination produces a calf");
        if (calf != null)
        {
            check(calf.AnimalGenetics().Father == Donor.AnimalGenetics() &&
                  calf.AnimalGenetics().Mother == recipient.AnimalGenetics(),
                "calf's genetics link to the sample donor and the mother");
            calf.Name = new NameSingle("AI calf");
        }

        recipient.health.RemoveHediff(pregnancy);
    }

    private static void EggLayerAndStorageTests()
    {
        // ---- Egg-layer: a rooster sample fertilizes a hen with no rooster present
        var chicken = DefDatabase<PawnKindDef>.GetNamed("Chicken");
        Pawn MakeChicken(Gender g)
        {
            var p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(chicken, Faction.OfPlayer,
                fixedGender: g, fixedBiologicalAge: 1f, fixedChronologicalAge: 1f));
            return (Pawn)GenSpawn.Spawn(p, CellFinder.RandomClosewalkCellNear(map.Center, map, 6), map);
        }

        var hen = MakeChicken(Gender.Female);
        var rooster = MakeChicken(Gender.Male);
        var roosterSample = CompGermlineSample.MakeFrom(rooster);
        rooster.Destroy(); // the donor doesn't need to exist any more
        var eggs = hen.TryGetComp<CompEggLayer>();
        check(GermlineUtility.CanReceive(hen), "hen can receive a sample");
        GermlineUtility.Inseminate(hen, roosterSample.TryGetComp<CompGermlineSample>());
        check(eggs.FullyFertilized, "sample fertilizes the hen");
        var egg = eggs.ProduceEgg();
        var eggGi = egg?.TryGetComp<EggGeneticInformation>()?.GeneticInformation;
        check(egg?.TryGetComp<CompHatcher>() != null, "hen lays a fertilized egg");
        check(eggGi?.Father == roosterSample.TryGetComp<CompGermlineSample>().genetics &&
              eggGi?.Mother == hen.AnimalGenetics(), "egg's genetics: father = sample, mother = hen");

        if (egg != null)
        {
            var good = new SpecialThingFilterWorker_EggGenes_Good();
            var poor = new SpecialThingFilterWorker_EggGenes_Poor();
            check(good.CanEverMatch(egg.def) && !good.CanEverMatch(ThingDefOf.Steel), "egg filter applies only to hatchable eggs");
            check(good.Matches(egg) != poor.Matches(egg), "egg is exactly one of good / poor genes");
            GenSpawn.Spawn(egg, hen.Position, map);
        }

        // ---- Viability: decays on the floor, paused in a powered cryobank, expires at zero
        var warm = map.mapTemperature.OutdoorTemp >= 0f;
        var floorSample = (Thing)GenSpawn.Spawn(CompGermlineSample.MakeFrom(Donor), map.Center, map);
        var fc = floorSample.TryGetComp<CompGermlineSample>();
        if (warm)
        {
            var t0 = fc.ticksViable;
            fc.CompTickRare();
            check(fc.ticksViable < t0, "sample loses viability at room temperature");
        }

        var bankCell = CellFinder.RandomClosewalkCellNear(map.Center, map, 4,
            c => c.Standable(map) && c.GetFirstBuilding(map) == null && c.GetFirstItem(map) == null);
        var bank = GenSpawn.Spawn(ThingMaker.MakeThing(AGB_DefOf.AGB_Cryobank), bankCell, map, WipeMode.Vanish);
        bank.SetFaction(Faction.OfPlayer);
        var power = bank.TryGetComp<CompPowerTrader>();
        floorSample.DeSpawn();
        GenSpawn.Spawn(floorSample, bankCell, map);
        power.PowerOn = true;
        var t1 = fc.ticksViable;
        fc.CompTickRare();
        check(fc.Preserved && fc.ticksViable == t1, "powered cryobank preserves the sample");
        if (warm)
        {
            power.PowerOn = false;
            check(!fc.Preserved, "unpowered cryobank does not preserve");
            power.PowerOn = true;
        }

        var doomed = (Thing)GenSpawn.Spawn(CompGermlineSample.MakeFrom(Donor), map.Center, map);
        if (warm)
        {
            doomed.TryGetComp<CompGermlineSample>().ticksViable = 10;
            doomed.TryGetComp<CompGermlineSample>().CompTickRare();
            check(doomed.Destroyed, "expired sample is discarded");
        }

        check(bank.def.building.fixedStorageSettings.filter.Allows(floorSample), "cryobank accepts samples");
        check(!bank.def.building.fixedStorageSettings.filter.Allows(ThingDefOf.Steel), "cryobank rejects other items");
        KeptSample = floorSample;
    }
}
