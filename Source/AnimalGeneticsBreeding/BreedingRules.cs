using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AnimalGeneticsBreeding;

/// <summary>Who may breed, and which animals auto-slaughter takes.</summary>
public static class BreedingRules
{
    private const int TopMalesCacheTicks = 600;

    private static readonly Dictionary<(Map, ThingDef), (int tick, HashSet<Pawn> males)> topMalesCache = new();

    public static void ClearCaches()
    {
        topMalesCache.Clear();
    }

    // ---------------------------------------------------------------- mating

    public static bool CanMate(Pawn pawn)
    {
        if (!BreedingUtility.IsManagedAnimal(pawn))
        {
            return true;
        }

        var role = pawn.HerdRole();
        if (role == HerdRole.Cull)
        {
            return false;
        }

        var program = BreedingProgram.Get(pawn.def);
        if (program.matingMode == MatingMode.Open)
        {
            return true;
        }

        if (pawn.gender != Gender.Male)
        {
            return !program.femalesNeedBreeder || role == HerdRole.Breeder;
        }

        if (role == HerdRole.Breeder)
        {
            return true;
        }

        return program.matingMode == MatingMode.BestMales && IsTopMale(pawn);
    }

    /// <summary>True if the species uses "best males" mating and this male is among the top N by score.</summary>
    public static bool IsTopMale(Pawn pawn)
    {
        if (pawn?.Map == null || pawn.gender != Gender.Male)
        {
            return false;
        }

        var program = BreedingProgram.Get(pawn.def);
        return program.matingMode == MatingMode.BestMales &&
               TopMales(pawn.Map, pawn.def, program.bestMalesCount).Contains(pawn);
    }

    private static HashSet<Pawn> TopMales(Map map, ThingDef def, int count)
    {
        var now = Find.TickManager.TicksGame;
        if (topMalesCache.TryGetValue((map, def), out var entry) && now - entry.tick < TopMalesCacheTicks)
        {
            return entry.males;
        }

        var males = map.mapPawns.SpawnedColonyAnimals
            .Where(p => p.def == def && p.gender == Gender.Male && p.ageTracker.CurLifeStage.reproductive &&
                        !p.Sterile() && p.HerdRole() != HerdRole.Cull)
            .OrderByDescending(p => p.BreedingScoreOf())
            .Take(count)
            .ToHashSet();
        topMalesCache[(map, def)] = (now, males);
        return males;
    }

    // --------------------------------------------------------- auto-slaughter

    private enum Bucket
    {
        MaleAdult,
        MaleYoung,
        FemaleAdult,
        FemaleYoung,
        Other
    }

    private static Bucket BucketOf(Pawn pawn)
    {
        var adult = pawn.ageTracker.CurLifeStage.reproductive;
        return pawn.gender switch
        {
            Gender.Male => adult ? Bucket.MaleAdult : Bucket.MaleYoung,
            Gender.Female => adult ? Bucket.FemaleAdult : Bucket.FemaleYoung,
            _ => Bucket.Other
        };
    }

    private static bool IsPregnant(Pawn pawn)
    {
        return pawn.health.hediffSet.HasHediff(HediffDefOf.Pregnant);
    }

    /// <summary>
    ///     Rewrites vanilla's freshly computed slaughter list. Vanilla decides how many animals of each
    ///     sex/age group go; we decide which ones (worst genes first, protected roles never), then add
    ///     the optional Cull / below-score extras.
    /// </summary>
    public static void Reselect(AutoSlaughterManager manager, List<Pawn> result)
    {
        var map = manager.map;
        if (map == null)
        {
            return;
        }

        foreach (var config in manager.configs)
        {
            var species = config.animal;
            var program = BreedingProgram.Get(species);

            var eligible = map.mapPawns.SpawnedColonyAnimals
                .Where(p => p.def == species && AutoSlaughterManager.CanAutoSlaughterNow(p) &&
                            (config.allowSlaughterBonded ||
                             p.relations.GetDirectRelationsCount(PawnRelationDefOf.Bond) <= 0) &&
                            (config.allowSlaughterPregnant || !IsPregnant(p)))
                .ToList();
            if (eligible.Count == 0)
            {
                continue;
            }

            var protectedSet = ProtectedAnimals(eligible, program);
            var vanillaChosen = result.Where(p => p.def == species).ToList();

            if (vanillaChosen.Count > 0)
            {
                result.RemoveAll(p => p.def == species);
                foreach (var group in vanillaChosen.GroupBy(BucketOf))
                {
                    var pool = eligible.Where(p => BucketOf(p) == group.Key && !protectedSet.Contains(p));
                    var ordered = program.geneAwareSlaughter
                        ? pool.OrderBy(p => p.HerdRole() == HerdRole.Cull ? 0 : 1)
                            .ThenBy(p => IsPregnant(p) ? 1 : 0)
                            .ThenBy(p => p.BreedingScoreOf())
                            .ThenByDescending(p => p.ageTracker.AgeBiologicalTicks)
                        : pool.OrderBy(p => IsPregnant(p) ? 1 : 0)
                            .ThenByDescending(p => p.ageTracker.AgeBiologicalTicks);
                    result.AddRange(ordered.Take(group.Count()));
                }
            }

            if (program.slaughterCulled)
            {
                result.AddRange(eligible.Where(p => p.HerdRole() == HerdRole.Cull && !result.Contains(p)));
            }

            if (program.slaughterBelowScore)
            {
                result.AddRange(eligible.Where(p =>
                    p.ageTracker.CurLifeStage.reproductive && !protectedSet.Contains(p) &&
                    p.BreedingScoreOf() < program.minScore && !result.Contains(p)));
            }
        }
    }

    private static HashSet<Pawn> ProtectedAnimals(List<Pawn> eligible, SpeciesProgram program)
    {
        var result = eligible
            .Where(p => p.HerdRole() is HerdRole.Breeder or HerdRole.Keep || IsTopMale(p))
            .ToHashSet();

        if (program.protectTopPerSex <= 0)
        {
            return result;
        }

        foreach (var sex in eligible.GroupBy(p => p.gender))
        {
            result.AddRange(sex.Where(p => p.HerdRole() != HerdRole.Cull)
                .OrderByDescending(p => p.BreedingScoreOf())
                .Take(program.protectTopPerSex));
        }

        return result;
    }
}
