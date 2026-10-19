using System.Collections.Generic;
using System.Linq;
using AnimalGenetics;
using RimWorld;
using UnityEngine;
using Verse;

namespace AnimalGeneticsBreeding;

/// <summary>Per-species breeding goal, mating and auto-slaughter rules, plus the herd ranked by score.</summary>
public partial class Dialog_BreedingProgram : Window
{
    private const float SpeciesWidth = 210f;
    private const float SettingsWidth = 370f;
    private const float RowHeight = 26f;
    private const float Gap = 12f;

    private Vector2 herdScroll;
    private ThingDef selected;
    private float settingsHeight = 900f;
    private Vector2 settingsScroll;
    private Vector2 speciesScroll;

    public Dialog_BreedingProgram(ThingDef species, bool openPairings = false)
    {
        selected = species;
        showPairings = openPairings;
        doCloseX = true;
        doCloseButton = false;
        draggable = true;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = false;
        preventCameraMotion = false;
    }

    public override Vector2 InitialSize => new(1100f, 720f);

    private static Map Map => Find.CurrentMap;

    private static List<ThingDef> Species()
    {
        return Map?.mapPawns.SpawnedColonyAnimals
            .Where(p => p.AnimalGenetics() != null)
            .Select(p => p.def).Distinct()
            .OrderBy(d => d.label).ToList() ?? [];
    }

    private static List<Pawn> Herd(ThingDef def)
    {
        return Map?.mapPawns.SpawnedColonyAnimals
            .Where(p => p.def == def && p.AnimalGenetics() != null)
            .OrderByDescending(p => p.BreedingScoreOf()).ToList() ?? [];
    }

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), "AGB.ProgramTitle".Translate());
        Text.Font = GameFont.Small;

        var body = new Rect(inRect.x, inRect.y + 40f, inRect.width, inRect.height - 40f);
        var species = Species();
        if (species.Count == 0)
        {
            Widgets.Label(body, "AGB.NoAnimals".Translate());
            return;
        }

        if (selected == null || !species.Contains(selected))
        {
            selected = species[0];
        }

        var speciesRect = new Rect(body.x, body.y, SpeciesWidth, body.height);
        var settingsRect = new Rect(speciesRect.xMax + Gap, body.y, SettingsWidth, body.height);
        var herdRect = new Rect(settingsRect.xMax + Gap, body.y, body.xMax - settingsRect.xMax - Gap, body.height);

        DrawSpeciesList(speciesRect, species);
        DrawSettings(settingsRect, BreedingProgram.GetOrCreate(selected));
        // Herd / Pairings tabs on the right panel
        var tabRect = new Rect(herdRect.x, herdRect.y, 140f, 26f);
        if (Widgets.ButtonText(tabRect, "AGB.TabHerd".Translate(), true, true, !showPairings ? Color.white : Color.gray))
        {
            showPairings = false;
        }

        tabRect.x += 146f;
        if (Widgets.ButtonText(tabRect, "AGB.TabPairings".Translate(), true, true, showPairings ? Color.white : Color.gray))
        {
            showPairings = true;
        }

        var panel = new Rect(herdRect.x, herdRect.y + 32f, herdRect.width, herdRect.height - 32f);
        if (showPairings)
        {
            DrawPairings(panel, Herd(selected));
        }
        else
        {
            DrawHerd(panel, Herd(selected));
        }
    }

    private void DrawSpeciesList(Rect rect, List<ThingDef> species)
    {
        Widgets.DrawMenuSection(rect);
        var inner = rect.ContractedBy(4f);
        var view = new Rect(0f, 0f, inner.width - 16f, species.Count * RowHeight);
        Widgets.BeginScrollView(inner, ref speciesScroll, view);
        var y = 0f;
        foreach (var def in species)
        {
            var row = new Rect(0f, y, view.width, RowHeight);
            if (def == selected)
            {
                Widgets.DrawHighlightSelected(row);
            }
            else if (Mouse.IsOver(row))
            {
                Widgets.DrawHighlight(row);
            }

            var count = Map.mapPawns.SpawnedColonyAnimals.Count(p => p.def == def);
            Widgets.DefIcon(new Rect(row.x + 2f, row.y + 1f, 24f, 24f), def);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(row.x + 30f, row.y, row.width - 30f, row.height), $"{def.LabelCap} ({count})");
            Text.Anchor = TextAnchor.UpperLeft;
            if (Widgets.ButtonInvisible(row))
            {
                selected = def;
            }

            y += RowHeight;
        }

        Widgets.EndScrollView();
    }

    private void DrawSettings(Rect rect, SpeciesProgram program)
    {
        Widgets.DrawMenuSection(rect);
        var inner = rect.ContractedBy(8f);
        var view = new Rect(0f, 0f, inner.width - 16f, settingsHeight);
        Widgets.BeginScrollView(inner, ref settingsScroll, view);
        var listing = new Listing_Standard();
        listing.Begin(view);

        // Goal
        Text.Font = GameFont.Medium;
        listing.Label(selected.LabelCap);
        Text.Font = GameFont.Small;
        listing.Label("AGB.SectionGoal".Translate(), -1f, "AGB.SectionGoalTip".Translate());
        if (listing.ButtonTextLabeledPct("AGB.Preset".Translate(), BreedingPresets.LabelFor(program.presetKey), 0.45f))
        {
            Find.WindowStack.Add(new FloatMenu(BreedingPresets.All
                .Select(p => new FloatMenuOption(p.Label, () => program.ApplyPreset(p))).ToList()));
        }

        foreach (var stat in BreedingUtility.GenesFor(selected))
        {
            var weight = program.GetWeight(stat);
            var label = "AGB.WeightLabel".Translate(Constants.GetLabel(stat).CapitalizeFirst(),
                weight <= 0f ? "AGB.Ignored".Translate().Resolve() : "×" + weight.ToString("0"));
            var newWeight = Mathf.Round(listing.SliderLabeled(label, weight, 0f, 5f, 0.55f));
            program.SetWeight(stat, newWeight);
        }

        listing.GapLine();

        // Mating
        listing.Label("AGB.SectionMating".Translate());
        var oldMode = program.matingMode;
        var oldBest = program.bestMalesCount;
        var oldFemales = program.femalesNeedBreeder;
        foreach (var mode in new[] { MatingMode.Open, MatingMode.BreedersOnly, MatingMode.BestMales })
        {
            if (listing.RadioButton(("AGB.Mating_" + mode).Translate(), program.matingMode == mode, 12f,
                    ("AGB.MatingTip_" + mode).Translate()))
            {
                program.matingMode = mode;
            }
        }

        if (program.matingMode == MatingMode.BestMales)
        {
            listing.Label("AGB.BestMalesCount".Translate(program.bestMalesCount));
            listing.IntAdjuster(ref program.bestMalesCount, 1, 1);
        }

        if (program.matingMode != MatingMode.Open)
        {
            listing.CheckboxLabeled("AGB.FemalesNeedBreeder".Translate(), ref program.femalesNeedBreeder,
                "AGB.FemalesNeedBreederTip".Translate());
        }

        listing.GapLine();

        // Auto-slaughter
        listing.Label("AGB.SectionSlaughter".Translate());
        var oldAware = program.geneAwareSlaughter;
        var oldProtect = program.protectTopPerSex;
        var oldCulled = program.slaughterCulled;
        var oldBelow = program.slaughterBelowScore;
        var oldMin = program.minScore;
        listing.CheckboxLabeled("AGB.GeneAwareSlaughter".Translate(), ref program.geneAwareSlaughter,
            "AGB.GeneAwareSlaughterTip".Translate());
        listing.Label("AGB.ProtectTop".Translate(program.protectTopPerSex), -1f, "AGB.ProtectTopTip".Translate());
        listing.IntAdjuster(ref program.protectTopPerSex, 1);
        listing.CheckboxLabeled("AGB.SlaughterCulled".Translate(), ref program.slaughterCulled,
            "AGB.SlaughterCulledTip".Translate());
        listing.CheckboxLabeled("AGB.SlaughterBelow".Translate(), ref program.slaughterBelowScore,
            "AGB.SlaughterBelowTip".Translate());
        if (program.slaughterBelowScore)
        {
            program.minScore = Mathf.Round(listing.SliderLabeled(
                "AGB.MinScore".Translate(BreedingScore.Format(program.minScore)), program.minScore, -0.5f, 0.5f,
                0.45f) * 100f) / 100f;
        }

        listing.Gap(6f);
        if (listing.ButtonText("AGB.OpenAutoSlaughter".Translate()))
        {
            Find.WindowStack.Add(new Dialog_AutoSlaughter(Map));
        }

        if (oldMode != program.matingMode || oldBest != program.bestMalesCount ||
            oldFemales != program.femalesNeedBreeder || oldAware != program.geneAwareSlaughter ||
            oldProtect != program.protectTopPerSex || oldCulled != program.slaughterCulled ||
            oldBelow != program.slaughterBelowScore || !Mathf.Approximately(oldMin, program.minScore))
        {
            BreedingProgram.Notify_RolesChanged();
        }

        settingsHeight = listing.CurHeight + 12f;
        listing.End();
        Widgets.EndScrollView();
    }

    private void DrawHerd(Rect rect, List<Pawn> herd)
    {
        Widgets.DrawMenuSection(rect);
        var inner = rect.ContractedBy(6f);
        var toSlaughter = Map.autoSlaughterManager.AnimalsToSlaughter;

        // Column layout: name | sex | age | score | role | status
        var nameW = inner.width - 16f - 28f - 50f - 60f - 40f - 120f;
        var header = new Rect(inner.x, inner.y, inner.width - 16f, RowHeight);
        Text.Font = GameFont.Tiny;
        GUI.color = Color.gray;
        var x = header.x;
        Widgets.Label(new Rect(x, header.y, nameW, RowHeight), "AGB.ColName".Translate());
        x += nameW + 28f;
        Widgets.Label(new Rect(x, header.y, 50f, RowHeight), "AGB.ColAge".Translate());
        x += 50f;
        Widgets.Label(new Rect(x, header.y, 60f, RowHeight), "AGB.ColScore".Translate());
        x += 60f;
        Widgets.Label(new Rect(x, header.y, 40f, RowHeight), "AGB.ColRole".Translate());
        x += 40f;
        Widgets.Label(new Rect(x, header.y, 120f, RowHeight), "AGB.ColStatus".Translate());
        GUI.color = Color.white;
        Text.Font = GameFont.Small;

        var listRect = new Rect(inner.x, inner.y + RowHeight, inner.width, inner.height - RowHeight);
        var view = new Rect(0f, 0f, listRect.width - 16f, herd.Count * RowHeight);
        Widgets.BeginScrollView(listRect, ref herdScroll, view);
        var y = 0f;
        var index = 0;
        foreach (var pawn in herd)
        {
            var row = new Rect(0f, y, view.width, RowHeight);
            if (index++ % 2 == 1)
            {
                Widgets.DrawLightHighlight(row);
            }

            x = 0f;
            var nameRect = new Rect(x, y, nameW, RowHeight);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(nameRect, pawn.LabelShortCap.Truncate(nameW));
            if (Widgets.ButtonInvisible(nameRect))
            {
                CameraJumper.TryJumpAndSelect(pawn);
            }

            TooltipHandler.TipRegion(nameRect, "AGB.ClickToSelect".Translate());
            x += nameW;

            var genderIcon = pawn.gender.GetIcon();
            if (genderIcon != null)
            {
                GUI.DrawTexture(new Rect(x + 4f, y + 3f, 20f, 20f), genderIcon);
            }

            x += 28f;
            Widgets.Label(new Rect(x, y, 50f, RowHeight), pawn.ageTracker.AgeBiologicalYearsFloat.ToString("F1"));
            x += 50f;
            Text.Anchor = TextAnchor.UpperLeft;
            BreedingUi.DrawScore(new Rect(x, y, 60f, RowHeight), pawn);
            x += 60f;
            BreedingUi.DrawRoleButton(new Rect(x, y, 40f, RowHeight), pawn);
            x += 40f;

            Text.Anchor = TextAnchor.MiddleLeft;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(x, y, 120f, RowHeight), Status(pawn, toSlaughter));
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            y += RowHeight;
        }

        Widgets.EndScrollView();
    }

    private static string Status(Pawn pawn, List<Pawn> toSlaughter)
    {
        var parts = new List<string>();
        if (toSlaughter.Contains(pawn))
        {
            parts.Add("AGB.StatusSlaughter".Translate().Colorize(ColorLibrary.RedReadable));
        }

        if (BreedingRules.IsTopMale(pawn))
        {
            parts.Add("AGB.TopMale".Translate());
        }

        if (!BreedingRules.CanMate(pawn) && pawn.ageTracker.CurLifeStage.reproductive)
        {
            parts.Add("AGB.StatusNoBreeding".Translate().Colorize(ColorLibrary.Grey));
        }

        if (pawn.health.hediffSet.HasHediff(HediffDefOf.Pregnant))
        {
            parts.Add("AGB.StatusPregnant".Translate());
        }

        return string.Join(", ", parts);
    }
}
