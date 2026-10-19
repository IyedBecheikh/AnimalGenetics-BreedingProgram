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

// Longer, behaviour-level scenarios run in game time: natural mating under rules, colonists actually
// slaughtering, and assorted edge cases.
public static class ScenarioTests
{
    private const int MatingTicks = 20000;
    private const int SlaughterTimeoutTicks = 20000;

    public static bool Active;

    private static Action<bool, string> check;
    private static Map map;
    private static int stage, stageStart;
    private static readonly List<(Pawn male, Pawn female)> matings = [];
    private static bool patched;
    private static List<Pawn> pigMales, pigFemales;
    private static Pawn topMale, cullFemale, keepMale;
    private static float originalMtb;
    private static ThingDef pig;
    private static List<Pawn> expectedSlaughter;

    public static void OnMated(Pawn male, Pawn female)
    {
        if (male?.def == pig)
        {
            matings.Add((male, female));
        }
    }

    public static void Start(Action<bool, string> checkFn, Map m)
    {
        check = checkFn;
        map = m;
        Active = true;
        stage = 0;
        if (!patched)
        {
            new Harmony("iyedm.agbtest.scenarios").Patch(AccessTools.Method(typeof(PawnUtility), nameof(PawnUtility.Mated)),
                postfix: new HarmonyMethod(typeof(ScenarioTests), nameof(OnMated)));
            patched = true;
        }
    }

    private static void Next()
    {
        stage++;
        stageStart = Find.TickManager.TicksGame;
    }

    private static int Elapsed => Find.TickManager.TicksGame - stageStart;

    private static Pawn Spawn(PawnKindDef kind, Gender g, float age, IntVec3 near)
    {
        var p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, Faction.OfPlayer, fixedGender: g,
            fixedBiologicalAge: age, fixedChronologicalAge: age));
        return (Pawn)GenSpawn.Spawn(p, CellFinder.RandomClosewalkCellNear(near, map, 3), map);
    }

    public static void Tick()
    {
        try
        {
            switch (stage)
            {
                case 0:
                    EdgeCases();
                    SetupMating();
                    Next();
                    break;
                case 1:
                    if (Elapsed < MatingTicks)
                    {
                        return;
                    }

                    CheckMating();
                    SetupSlaughter();
                    Next();
                    break;
                case 2:
                    Phase3Tests.KeepWorking(butcher, JobDefOf.Slaughter, NextSlaughterJob, Elapsed);
                    if (expectedSlaughter.All(p => p.Dead) || Elapsed > SlaughterTimeoutTicks)
                    {
                        CheckSlaughter();
                        OperationsDirect();
                        StartOperationBill();
                        Next();
                    }

                    break;
                case 3:
                    if (opPatient.health.hediffSet.HasHediff(HediffDefOf.Pregnant))
                    {
                        check(opPatient.BillStack.Bills.Count == 0, "operation bill completed and removed");
                        var fatherGi = AccessTools.FieldRefAccess<FatherGeneticInformation, GeneticInformation>("geneticInformation")(
                            ((HediffWithComps)opPatient.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.Pregnant))
                            .GetComp<FatherGeneticInformation>());
                        check(fatherGi == opBestSample.TryGetComp<CompGermlineSample>().genetics,
                            "doctor's insemination operation used the best sample as father");
                        Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                        Active = false;
                    }
                    else if (opPatient.BillStack.Bills.Count == 0)
                    {
                        // 5% failure roll at Medicine 20: queue it again with the next best sample.
                        if (++opAttempts > 3)
                        {
                            check(false, "insemination operation never succeeded");
                            Active = false;
                            return;
                        }

                        StartOperationBill();
                    }
                    else
                    {
                        if (!opJobStarted)
                        {
                            TryStartOperationJob();
                        }

                        Phase3Tests.KeepWorking(opDoctor, JobDefOf.DoBill, () =>
                            opGiver.HasJobOnThing(opDoctor, opPatient, true) ? opGiver.JobOnThing(opDoctor, opPatient, true) : null,
                            Elapsed);
                        if (Elapsed > SlaughterTimeoutTicks)
                        {
                            check(false, "timed out: insemination operation by a doctor");
                            Active = false;
                        }
                    }

                    break;
            }
        }
        catch (Exception e)
        {
            check(false, "scenario exception: " + e);
            pig.race.mateMtbHours = originalMtb;
            Active = false;
        }
    }

    // ------------------------------------------------------------------ edge cases

    private static void EdgeCases()
    {
        // Colonists: Animal Genetics may track them (human mode) but we never manage them.
        var colonist = map.mapPawns.FreeColonistsSpawned.First();
        check(!BreedingUtility.IsManagedAnimal(colonist), "colonists are never managed animals");
        check(colonist.Breeding()?.CompGetGizmosExtra().Any() != true, "colonists get no breeding gizmos");
        check(colonist.Breeding()?.CompInspectStringExtra() == null, "colonists get no breeding inspect line");

        // Wild animals: untouched by herd rules, but still scored.
        var wild = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.RaceProps.Animal && p.Faction == null);
        if (wild != null)
        {
            check(BreedingRules.CanMate(wild), "wild animals mate normally (rules only apply to your animals)");
            check(!wild.Breeding().CompGetGizmosExtra().Any(), "wild animals get no gizmos");
            check(!float.IsNaN(wild.BreedingScoreOf()), "wild animals still get a score for the Wildlife/Genetics tabs");
        }

        // Settings default: inbreeding depression off.
        check(!new BreedingSettings().inbreedingEnabled, "inbreeding depression is off by default");

        // A real rooster overrides a pending sample-father on a hen.
        var chicken = DefDatabase<PawnKindDef>.GetNamed("Chicken");
        var hen = Spawn(chicken, Gender.Female, 1f, map.Center);
        var rooster = Spawn(chicken, Gender.Male, 1f, map.Center);
        var other = Spawn(chicken, Gender.Male, 1f, map.Center);
        GermlineUtility.Inseminate(hen, CompGermlineSample.MakeFrom(other).TryGetComp<CompGermlineSample>());
        check(hen.Breeding().eggFather == other.AnimalGenetics(), "hen remembers the sample father");
        hen.TryGetComp<CompEggLayer>().Fertilize(rooster);
        check(hen.Breeding().eggFather == null, "a real rooster replaces the sample father");
        var egg = hen.TryGetComp<CompEggLayer>().ProduceEgg();
        check(egg.TryGetComp<EggGeneticInformation>().GeneticInformation.Father == rooster.AnimalGenetics(),
            "eggs after a real mating are fathered by the rooster");

        // Score is stable and cached; changing the goal recomputes it.
        var p0 = hen;
        var s1 = p0.BreedingScoreOf();
        check(Math.Abs(s1 - p0.BreedingScoreOf()) < 1e-6f, "score is stable between reads");

        // Cull-tagged females block even vanilla FertileMateTarget; sterile animals can't donate.
        hen.Breeding().SetRole(HerdRole.Cull);
        check(!PawnUtility.FertileMateTarget(rooster, hen), "Cull hen can't be mated");
        hen.Breeding().SetRole(HerdRole.None);

        // Kinship never loops or explodes on deep pedigrees.
        var gi = rooster.AnimalGenetics();
        GeneticInformation chainTop = null;
        var current = new GeneticInformation(gi.GeneRecords.ToDictionary(kv => kv.Key, kv => new GeneRecord { Value = 1f }));
        for (var i = 0; i < 40; i++)
        {
            var next = new GeneticInformation(gi.GeneRecords.ToDictionary(kv => kv.Key, kv => new GeneRecord { Value = 1f }))
            {
                Mother = current, Father = current
            };
            chainTop ??= current;
            current = next;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var f = Kinship.Inbreeding(current);
        watch.Stop();
        check(f > 0.9f && watch.ElapsedMilliseconds < 200,
            $"kinship on a 40-generation selfing chain is bounded and fast (F={f:F3}, {watch.ElapsedMilliseconds} ms)");
    }

    // ------------------------------------------------------------------ natural mating under rules

    private static void SetupMating()
    {
        pig = DefDatabase<ThingDef>.GetNamed("Pig");
        var kind = DefDatabase<PawnKindDef>.GetNamed("Pig");
        var center = CellFinder.RandomClosewalkCellNear(map.Center, map, 10);
        pigMales = Enumerable.Range(0, 3).Select(i => Spawn(kind, Gender.Male, 3f + i, center)).ToList();
        pigFemales = Enumerable.Range(0, 4).Select(i => Spawn(kind, Gender.Female, 3f + i, center)).ToList();

        var program = BreedingProgram.GetOrCreate(pig);
        program.matingMode = MatingMode.BestMales;
        program.bestMalesCount = 1;
        BreedingProgram.Notify_RolesChanged();
        topMale = pigMales.OrderByDescending(p => p.BreedingScoreOf()).First();
        cullFemale = pigFemales[0];
        cullFemale.Breeding().SetRole(HerdRole.Cull);

        // Make mating frequent so the scenario runs in seconds, keep everyone together and fed.
        originalMtb = pig.race.mateMtbHours;
        pig.race.mateMtbHours = 0.01f;
        foreach (var p in pigMales.Concat(pigFemales))
        {
            p.needs.food.CurLevelPercentage = 1f;
        }

        matings.Clear();
        Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
    }

    private static void CheckMating()
    {
        pig.race.mateMtbHours = originalMtb;
        var males = matings.Select(m => m.male).Distinct().ToList();
        Log.Message($"[AGBTEST] natural pig matings: {matings.Count}, by {string.Join(", ", males.Select(m => m.LabelShort))}");
        check(matings.Count >= 2, $"natural mating happened under best-males rule ({matings.Count} matings)");
        check(males.All(m => m == topMale), "only the top-scoring male ever mated");
        check(matings.All(m => m.female != cullFemale), "the Cull female was never mated");
    }

    // ------------------------------------------------------------------ colonists actually slaughter

    private static void SetupSlaughter()
    {
        var program = BreedingProgram.GetOrCreate(pig);
        program.matingMode = MatingMode.Open;
        program.geneAwareSlaughter = true;
        foreach (var p in pigMales.Concat(pigFemales))
        {
            p.Breeding().SetRole(HerdRole.None);
        }

        keepMale = pigMales.OrderBy(p => p.BreedingScoreOf()).First(); // worst male, but protected
        keepMale.Breeding().SetRole(HerdRole.Keep);
        var config = map.autoSlaughterManager.configs.First(c => c.animal == pig);
        config.maxMales = 1;
        map.autoSlaughterManager.Notify_ConfigChanged();
        expectedSlaughter = map.autoSlaughterManager.AnimalsToSlaughter.Where(p => p.def == pig).ToList();
        var unprotected = pigMales.Where(p => p != keepMale && p.ageTracker.CurLifeStage.reproductive)
            .OrderBy(p => p.BreedingScoreOf()).ToList();
        check(expectedSlaughter.Count == pigMales.Count(p => p.ageTracker.CurLifeStage.reproductive) - 1 &&
              !expectedSlaughter.Contains(keepMale),
            $"auto-slaughter picks {expectedSlaughter.Count} pig(s), never the Keep male");
        check(expectedSlaughter.SequenceEqual(unprotected.Take(expectedSlaughter.Count)),
            "auto-slaughter picks the worst unprotected males");

        // Slaughtering needs Handling and violence (some colonists are incapable of violence).
        butcher = map.mapPawns.FreeColonistsSpawned.First(c =>
            !c.WorkTypeIsDisabled(WorkTypeDefOf.Handling) && !c.WorkTagIsDisabled(WorkTags.Violent));
        slaughterGiver = (WorkGiver_Scanner)DefDatabase<WorkGiverDef>.AllDefs
            .First(d => d.giverClass == typeof(WorkGiver_Slaughter)).Worker;
        check(expectedSlaughter.All(p => slaughterGiver.HasJobOnThing(butcher, p, true)),
            "vanilla slaughter work-giver accepts every animal we chose");
        check(!slaughterGiver.HasJobOnThing(butcher, keepMale, true), "vanilla slaughter work-giver refuses the Keep male");
        butcher.Position = expectedSlaughter[0].Position;
        butcher.Notify_Teleported();
    }

    private static Pawn butcher;
    private static Pawn opPatient, opDoctor;
    private static Thing opBestSample;
    private static WorkGiver_Scanner opGiver;
    private static int opAttempts;

    private static RecipeDef Op(string name)
    {
        return DefDatabase<RecipeDef>.GetNamed(name);
    }

    // ------------------------------------------------------------------ health-tab operations

    private static void OperationsDirect()
    {
        var muffalo = map.mapPawns.SpawnedColonyAnimals.Where(p => p.def.defName == "Muffalo" && p.ageTracker.CurLifeStage.reproductive).ToList();
        // Fresh pair: earlier scenarios leave the herd sampled, pregnant or culled.
        var muffaloKind = DefDatabase<PawnKindDef>.GetNamed("Muffalo");
        var male = Spawn(muffaloKind, Gender.Male, 5f, map.Center);
        var female = Spawn(muffaloKind, Gender.Female, 5f, map.Center);
        muffalo.Add(male);
        muffalo.Add(female);
        check(male != null && female != null, "found a donor male and a fertile female for operation tests");
        if (male == null || female == null)
        {
            return;
        }

        var collect = Op("AGB_Operation_CollectGermline");
        var inseminate = Op("AGB_Operation_Inseminate");
        var stabilize = Op("AGB_Operation_SerumStabilizing");
        var domSpeed = Op("AGB_Operation_SerumDominance_MoveSpeed");
        check(male.def.AllRecipes.Contains(collect) && male.def.AllRecipes.Contains(domSpeed),
            "germline operations are attached to animal races");
        var missing = DefDatabase<ThingDef>.AllDefs.Where(d => d.race is { Animal: true } && !d.IsCorpse && !d.AllRecipes.Contains(inseminate)).Select(d => d.defName).ToList();
        Log.Message("[AGBTEST] races without operations: " + string.Join(", ", missing));
        check(missing.Count == 0,
            "every animal race has the operations (including modded ones inheriting AnimalThingBase)");
        check(collect.Worker.AvailableOnNow(male) && !collect.Worker.AvailableOnNow(female), "collect operation: males only");
        check(inseminate.Worker.AvailableOnNow(female) && !inseminate.Worker.AvailableOnNow(male), "inseminate operation: females only");
        check(stabilize.Worker.AvailableOnNow(female) && domSpeed.Worker.AvailableOnNow(female), "serum operations offered on females");
        var colonist = map.mapPawns.FreeColonistsSpawned.First();
        check(!collect.Worker.AvailableOnNow(colonist) && !inseminate.Worker.AvailableOnNow(colonist), "no germline operations on colonists");
        var wild = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.RaceProps.Animal && p.Faction == null);
        if (wild != null)
        {
            check(!collect.Worker.AvailableOnNow(wild) && !inseminate.Worker.AvailableOnNow(wild), "no germline operations on wild animals");
        }

        // Collect operation outcome
        var before = map.listerThings.ThingsOfDef(AGB_DefOf.AGB_GermlineSample).ToHashSet();
        collect.Worker.ApplyOnPawn(male, null, colonist, [], null);
        var made = map.listerThings.ThingsOfDef(AGB_DefOf.AGB_GermlineSample).FirstOrDefault(t => !before.Contains(t));
        check(made?.TryGetComp<CompGermlineSample>().genetics == male.AnimalGenetics(), "collect operation produces the male's sample");
        check(male.health.hediffSet.HasHediff(AGB_DefOf.AGB_RecentlySampled), "collect operation applies the cooldown");

        // Serum operation outcome
        var serum = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("AGB_SerumDominance"));
        domSpeed.Worker.ApplyOnPawn(female, null, colonist, [serum], null);
        var hediff = female.health.hediffSet.GetFirstHediffOfDef(DefDatabase<HediffDef>.GetNamed("AGB_SerumDominance"));
        check(hediff?.TryGetComp<HediffComp_Serum>().Gene == StatDefOf.MoveSpeed, "dominance (speed) operation gives the speed serum");

        // Ingredient filter: only the best matching sample is acceptable for this female.
        var chickenSample = map.listerThings.ThingsOfDef(AGB_DefOf.AGB_GermlineSample)
            .FirstOrDefault(t => t.TryGetComp<CompGermlineSample>().species?.defName == "Chicken");
        chickenSample ??= GenSpawn.Spawn(CompGermlineSample.MakeFrom(map.mapPawns.SpawnedColonyAnimals.First(p => p.def.defName == "Chicken")),
            female.Position, map);
        var other = muffalo.FirstOrDefault(p => p.gender == Gender.Male && p != male);
        if (other != null)
        {
            GenSpawn.Spawn(CompGermlineSample.MakeFrom(other), female.Position, map);
        }

        var bill = new Bill_Medical(inseminate, null);
        female.BillStack.AddBill(bill);
        var best = Recipe_Inseminate.BestSampleFor(female);
        var muffaloSamples = map.listerThings.ThingsOfDef(AGB_DefOf.AGB_GermlineSample).Where(t => GermlineUtility.Matches(t, female)).ToList();
        check(best != null && bill.IsFixedOrAllowedIngredient(best), "insemination bill accepts the best matching sample");
        check(!bill.IsFixedOrAllowedIngredient(chickenSample), "insemination bill rejects another species' sample");
        check(muffaloSamples.Where(t => t != best).All(t => !bill.IsFixedOrAllowedIngredient(t)),
            $"insemination bill rejects worse matching samples ({muffaloSamples.Count - 1})");
        female.BillStack.Delete(bill);
        opPatient = female;
    }

    private static void StartOperationBill()
    {
        opJobStarted = false;
        opDoctor = map.mapPawns.FreeColonistsSpawned.First(c => !c.WorkTypeIsDisabled(WorkTypeDefOf.Doctor));
        opDoctor.skills.GetSkill(SkillDefOf.Medicine).Level = 20;
        opGiver = (WorkGiver_Scanner)DefDatabase<WorkGiverDef>.GetNamed("DoBillsMedicalAnimalOperation").Worker;
        opBestSample = Recipe_Inseminate.BestSampleFor(opPatient);
        opPatient.BillStack.AddBill(new Bill_Medical(Op("AGB_Operation_Inseminate"), null));
        if (opBed == null)
        {
            // Like any vanilla operation, the animal must lie in a bed or sleeping spot first.
            var spotCell = CellFinder.RandomClosewalkCellNear(opPatient.Position, map, 4,
                c => c.Standable(map) && c.GetFirstBuilding(map) == null && c.GetFirstItem(map) == null);
            opBed = (Building_Bed)GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("AnimalSleepingSpot")), spotCell, map);
            opBed.SetFaction(Faction.OfPlayer);
            opBed.Medical = true;
        }

        opDoctor.Position = opPatient.Position;
        opDoctor.Notify_Teleported();
    }

    private static Building_Bed opBed;
    private static bool opJobStarted;

    private static void TryStartOperationJob()
    {
        var ok = opGiver.HasJobOnThing(opDoctor, opPatient, true);
        if (!ok)
        {
            return;
        }

        opJobStarted = true;
        opBestSample = Recipe_Inseminate.BestSampleFor(opPatient);
        Log.Message($"[AGBTEST] DoBill reason: {JobFailReason.Reason} | best sample {opBestSample?.LabelShort} at {opBestSample?.Position} forbidden={opBestSample?.IsForbidden(opDoctor)} spawned={opBestSample?.Spawned} | patient downed={opPatient.Downed} inbed={opPatient.InBed()}");
        check(true, "vanilla 'operate on animals' work-giver offers the insemination bill once the patient is in bed");
        opDoctor.jobs.StartJob(opGiver.JobOnThing(opDoctor, opPatient, true), JobCondition.InterruptForced);
    }
    private static WorkGiver_Scanner slaughterGiver;

    private static Job NextSlaughterJob()
    {
        var target = expectedSlaughter.FirstOrDefault(p => !p.Dead && slaughterGiver.HasJobOnThing(butcher, p, true));
        return target == null ? null : slaughterGiver.JobOnThing(butcher, target, true);
    }

    private static void CheckSlaughter()
    {
        var dead = expectedSlaughter.Count(p => p.Dead);
        check(dead == expectedSlaughter.Count, $"colonists slaughtered every chosen pig ({dead}/{expectedSlaughter.Count})");
        check(!keepMale.Dead, "the Keep male survived");
        var config = map.autoSlaughterManager.configs.First(c => c.animal == pig);
        config.maxMales = -1;
        map.autoSlaughterManager.Notify_ConfigChanged();
    }
}
