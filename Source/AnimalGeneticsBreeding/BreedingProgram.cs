using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AnimalGeneticsBreeding;

public class BreedingPreset(string key, Dictionary<string, float> weights)
{
    public readonly string Key = key;
    public readonly Dictionary<string, float> Weights = weights;

    public string Label => ("AGB.Preset_" + Key).Translate();
}

public static class BreedingPresets
{
    public const string Custom = "Custom";

    public static readonly List<BreedingPreset> All =
    [
        new("Balanced", new Dictionary<string, float>
        {
            { "Health", 1 }, { "Damage", 1 }, { "MoveSpeed", 1 }, { "CarryingCapacity", 1 },
            { "MeatAmount", 1 }, { "LeatherAmount", 1 }, { "Wildness", 1 }, { "GatherYield", 1 },
            { GrowthGene.DefName, 1 }
        }),
        new("PackAnimal", new Dictionary<string, float>
            { { "CarryingCapacity", 3 }, { "MoveSpeed", 2 }, { "Health", 1 } }),
        new("Meat", new Dictionary<string, float>
            { { "MeatAmount", 3 }, { "LeatherAmount", 2 }, { "Health", 1 }, { GrowthGene.DefName, 1 } }),
        new("Produce", new Dictionary<string, float>
            { { "GatherYield", 3 }, { "MeatAmount", 1 }, { "Health", 1 } }),
        new("WarBeast", new Dictionary<string, float>
            { { "Damage", 3 }, { "Health", 2 }, { "MoveSpeed", 2 } }),
        new("Docile", new Dictionary<string, float>
            { { "Wildness", 3 }, { "Health", 1 }, { "GatherYield", 1 } }),
        new("FastGrowth", new Dictionary<string, float>
            { { GrowthGene.DefName, 3 }, { "Health", 1 }, { "MeatAmount", 1 } })
    ];

    public static BreedingPreset Default => All[0];

    public static string LabelFor(string key)
    {
        return ("AGB.Preset_" + key).Translate();
    }
}

/// <summary>Per-species breeding goal, mating rules and auto-slaughter rules.</summary>
public class SpeciesProgram : IExposable
{
    public bool slaughterBelowScore;
    public int bestMalesCount = 2;
    public ThingDef def;
    public bool femalesNeedBreeder;
    public bool geneAwareSlaughter = true;
    public MatingMode matingMode = MatingMode.Open;
    public float minScore = -0.05f;
    public string presetKey = BreedingPresets.Default.Key;
    public int protectTopPerSex;
    public bool slaughterCulled;
    public Dictionary<string, float> weights = new(BreedingPresets.Default.Weights);

    public void ExposeData()
    {
        Scribe_Defs.Look(ref def, "def");
        Scribe_Values.Look(ref presetKey, "presetKey", BreedingPresets.Default.Key);
        Scribe_Collections.Look(ref weights, "weights", LookMode.Value, LookMode.Value);
        Scribe_Values.Look(ref matingMode, "matingMode");
        Scribe_Values.Look(ref bestMalesCount, "bestMalesCount", 2);
        Scribe_Values.Look(ref femalesNeedBreeder, "femalesNeedBreeder");
        Scribe_Values.Look(ref geneAwareSlaughter, "geneAwareSlaughter", true);
        Scribe_Values.Look(ref protectTopPerSex, "protectTopPerSex");
        Scribe_Values.Look(ref slaughterCulled, "slaughterCulled");
        Scribe_Values.Look(ref slaughterBelowScore, "slaughterBelowScore");
        Scribe_Values.Look(ref minScore, "minScore", -0.05f);

        if (Scribe.mode == LoadSaveMode.PostLoadInit)
        {
            // Untouched presets follow the preset, so genes added in later versions get their weight.
            var preset = BreedingPresets.All.FirstOrDefault(p => p.Key == presetKey);
            weights = preset != null
                ? new Dictionary<string, float>(preset.Weights)
                : weights ?? new Dictionary<string, float>(BreedingPresets.Default.Weights);
        }
    }

    public float GetWeight(StatDef stat)
    {
        return weights.TryGetValue(stat.defName, out var w) ? w : 0f;
    }

    public void SetWeight(StatDef stat, float value)
    {
        if (GetWeight(stat) == value)
        {
            return;
        }

        weights[stat.defName] = value;
        presetKey = BreedingPresets.Custom;
        BreedingProgram.Notify_ProgramChanged();
    }

    public void ApplyPreset(BreedingPreset preset)
    {
        weights = new Dictionary<string, float>(preset.Weights);
        presetKey = preset.Key;
        BreedingProgram.Notify_ProgramChanged();
    }
}

/// <summary>Save-game storage for all species programs, plus change notification.</summary>
public class BreedingProgram : GameComponent
{
    /// <summary>Bumped on any change that affects scores or rules; caches compare against it.</summary>
    public static int Version;

    private static readonly SpeciesProgram fallback = new();

    private Dictionary<ThingDef, SpeciesProgram> lookup;
    private List<SpeciesProgram> programs = [];

    public BreedingProgram(Game game)
    {
        Version++;
        BreedingRules.ClearCaches();
    }

    public static BreedingProgram Instance => Current.Game?.GetComponent<BreedingProgram>();

    /// <summary>Read-only access: the species' program, or shared defaults if it was never edited.</summary>
    public static SpeciesProgram Get(ThingDef def)
    {
        var instance = Instance;
        if (instance == null || def == null)
        {
            return fallback;
        }

        instance.lookup ??= instance.programs.ToDictionary(p => p.def);
        return instance.lookup.TryGetValue(def, out var program) ? program : fallback;
    }

    /// <summary>Editable program for a species; created (and saved) on first edit.</summary>
    public static SpeciesProgram GetOrCreate(ThingDef def)
    {
        var instance = Instance;
        return instance == null || def == null ? fallback : instance.GetOrCreateInt(def);
    }

    private SpeciesProgram GetOrCreateInt(ThingDef def)
    {
        lookup ??= programs.ToDictionary(p => p.def);
        if (lookup.TryGetValue(def, out var program))
        {
            return program;
        }

        program = new SpeciesProgram { def = def };
        programs.Add(program);
        lookup[def] = program;
        return program;
    }

    public override void ExposeData()
    {
        Scribe_Collections.Look(ref programs, "programs", LookMode.Deep);
        if (Scribe.mode != LoadSaveMode.PostLoadInit)
        {
            return;
        }

        programs ??= [];
        programs.RemoveAll(p => p?.def == null);
        programs = programs.GroupBy(p => p.def).Select(g => g.First()).ToList();
        lookup = null;
        Version++;
        BreedingRules.ClearCaches();
    }

    public override void GameComponentTick()
    {
        // Animals age into new life stages and top-male rankings shift; vanilla's auto-slaughter cache
        // does not notice either, so refresh it now and then.
        if (Find.TickManager.TicksGame % 2500 == 0)
        {
            Notify_RolesChanged();
        }
    }

    public static void Notify_ProgramChanged()
    {
        Version++;
        Notify_RolesChanged();
    }

    public static void Notify_RolesChanged()
    {
        BreedingRules.ClearCaches();
        if (Find.Maps == null)
        {
            return;
        }

        foreach (var map in Find.Maps)
        {
            map.autoSlaughterManager?.Notify_ConfigChanged();
        }
    }
}
