using UnityEngine;
using Verse;

namespace AnimalGeneticsBreeding;

public class BreedingSettings : ModSettings
{
    public const float DefaultPenalty = 0.5f;

    /// <summary>Off by default: no hidden change to how Animal Genetics breeds unless the player opts in.</summary>
    public bool inbreedingEnabled;

    /// <summary>Gene loss per 1.0 of inbreeding coefficient. Full siblings (F = 25%) at 0.5 lose 12.5% per gene.</summary>
    public float inbreedingPenalty = DefaultPenalty;

    public override void ExposeData()
    {
        Scribe_Values.Look(ref inbreedingEnabled, "inbreedingEnabled");
        Scribe_Values.Look(ref inbreedingPenalty, "inbreedingPenalty", DefaultPenalty);
    }
}

public class BreedingSettingsMod : Mod
{
    public static BreedingSettings Settings;

    public BreedingSettingsMod(ModContentPack content) : base(content)
    {
        Settings = GetSettings<BreedingSettings>();
    }

    public override string SettingsCategory()
    {
        return "AGB.SettingsCategory".Translate();
    }

    public override void DoSettingsWindowContents(Rect inRect)
    {
        var listing = new Listing_Standard();
        listing.Begin(inRect.LeftPart(0.6f));
        listing.CheckboxLabeled("AGB.SettingInbreeding".Translate(), ref Settings.inbreedingEnabled,
            "AGB.SettingInbreedingTip".Translate());
        if (Settings.inbreedingEnabled)
        {
            Settings.inbreedingPenalty = Mathf.Round(listing.SliderLabeled(
                "AGB.SettingPenalty".Translate((Settings.inbreedingPenalty * 25f).ToString("0.#")),
                Settings.inbreedingPenalty, 0f, 2f, 0.6f, "AGB.SettingPenaltyTip".Translate()) * 20f) / 20f;
        }

        listing.End();
    }
}
