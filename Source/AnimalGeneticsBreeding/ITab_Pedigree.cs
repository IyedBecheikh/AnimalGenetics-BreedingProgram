using System.Collections.Generic;
using System.Linq;
using AnimalGenetics;
using RimWorld;
using UnityEngine;
using Verse;

namespace AnimalGeneticsBreeding;

/// <summary>Three-generation family tree with each ancestor's genes, built from Animal Genetics' parent graph.</summary>
public class ITab_Pedigree : ITab
{
    private const float Pad = 12f;
    private const float HeaderHeight = 44f;
    private const float BoxGap = 8f;
    private const float LineHeight = 14f;

    // Box = title (20) + score (16) + up to 4 gene rows + padding; tab height fits 4 grandparent boxes.
    private const float BoxHeight = 20f + 16f + (4 * LineHeight) + 14f;

    private static Dictionary<GeneticInformation, Pawn> index = new();
    private static int indexFrame = -9999;

    public ITab_Pedigree()
    {
        labelKey = "AGB.TabPedigree";
        size = new Vector2(780f, (2 * Pad) + HeaderHeight + (4 * BoxHeight) + (3 * BoxGap));
    }

    public override bool IsVisible
    {
        get
        {
            var pawn = SelPawn;
            if (pawn == null || !pawn.RaceProps.Animal || pawn.AnimalGenetics() == null)
            {
                return false;
            }

            return CoreMod.Instance.Settings.omniscientMode || pawn.Faction == Faction.OfPlayer;
        }
    }

    /// <summary>GeneticInformation → pawn, so ancestors that still exist (alive or dead) get names.</summary>
    private static Pawn PawnFor(GeneticInformation gi)
    {
        if (gi == null)
        {
            return null;
        }

        if (Time.frameCount - indexFrame > 250)
        {
            index = new Dictionary<GeneticInformation, Pawn>();
            IEnumerable<Pawn> all = Find.WorldPawns.AllPawnsAliveOrDead;
            foreach (var map in Find.Maps)
            {
                all = all.Concat(map.mapPawns.AllPawns);
            }

            foreach (var pawn in all)
            {
                if (pawn?.RaceProps?.Animal != true)
                {
                    continue;
                }

                var g = pawn.AnimalGenetics();
                if (g != null)
                {
                    index[g] = pawn;
                }
            }

            indexFrame = Time.frameCount;
        }

        return index.TryGetValue(gi, out var p) ? p : null;
    }

    protected override void FillTab()
    {
        var pawn = SelPawn;
        var gi = pawn.AnimalGenetics();
        var rect = new Rect(0f, 0f, size.x, size.y).ContractedBy(Pad);

        // Header
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(rect.x, rect.y, rect.width, 30f), "AGB.PedigreeOf".Translate(pawn.LabelShortCap));
        Text.Font = GameFont.Tiny;
        var coefficient = Kinship.Inbreeding(gi);
        var inbred = "AGB.PedigreeInbreeding".Translate((coefficient * 100f).ToString("0.#"),
            Kinship.Describe(coefficient)).Resolve();
        Widgets.Label(new Rect(rect.x, rect.y + 28f, rect.width, 18f), inbred);
        Text.Font = GameFont.Small;

        var body = new Rect(rect.x, rect.y + HeaderHeight, rect.width, rect.height - HeaderHeight);
        var colWidth = (body.width - (2 * 24f)) / 3f;
        var boxHeight = (body.height - (3 * BoxGap)) / 4f;

        // Grandparents (col 2): MM, MF, FM, FF top to bottom; parents (col 1) centered on their pair.
        var mother = gi.Mother;
        var father = gi.Father;
        var grand = new[] { mother?.Mother, mother?.Father, father?.Mother, father?.Father };
        var grandRects = new Rect[4];
        for (var i = 0; i < 4; i++)
        {
            grandRects[i] = new Rect(body.x + (2 * (colWidth + 24f)), body.y + (i * (boxHeight + BoxGap)), colWidth,
                boxHeight);
        }

        var motherRect = new Rect(body.x + colWidth + 24f, Mid(grandRects[0], grandRects[1]) - (boxHeight / 2f),
            colWidth, boxHeight);
        var fatherRect = new Rect(motherRect.x, Mid(grandRects[2], grandRects[3]) - (boxHeight / 2f), colWidth,
            boxHeight);
        var selfRect = new Rect(body.x, body.y + (body.height / 2f) - ((boxHeight * 1.6f) / 2f), colWidth,
            boxHeight * 1.6f);

        GUI.color = new Color(1f, 1f, 1f, 0.35f);
        Connect(selfRect, motherRect, fatherRect);
        Connect(motherRect, grandRects[0], grandRects[1]);
        Connect(fatherRect, grandRects[2], grandRects[3]);
        GUI.color = Color.white;

        DrawBox(selfRect, pawn.def, gi, pawn, Gender.None, true);
        DrawBox(motherRect, pawn.def, mother, PawnFor(mother), Gender.Female, false);
        DrawBox(fatherRect, pawn.def, father, PawnFor(father), Gender.Male, false);
        for (var i = 0; i < 4; i++)
        {
            DrawBox(grandRects[i], pawn.def, grand[i], PawnFor(grand[i]), i % 2 == 0 ? Gender.Female : Gender.Male,
                false);
        }

        Text.Anchor = TextAnchor.UpperLeft;
        Text.Font = GameFont.Small;
    }

    private static float Mid(Rect a, Rect b)
    {
        return (a.center.y + b.center.y) / 2f;
    }

    private static void Connect(Rect child, Rect upper, Rect lower)
    {
        var x = child.xMax + 12f;
        Widgets.DrawLineHorizontal(child.xMax, child.center.y, 12f);
        Widgets.DrawLineVertical(x, upper.center.y, lower.center.y - upper.center.y);
        Widgets.DrawLineHorizontal(x, upper.center.y, upper.x - x);
        Widgets.DrawLineHorizontal(x, lower.center.y, lower.x - x);
    }

    private static void DrawBox(Rect rect, ThingDef species, GeneticInformation gi, Pawn pawn, Gender role,
        bool isSelf)
    {
        Widgets.DrawMenuSection(rect);
        var inner = rect.ContractedBy(5f);
        var records = gi?._geneRecords;

        // Title: name (or unknown), gender icon, status
        var gender = pawn?.gender ?? role;
        var titleRect = new Rect(inner.x, inner.y, inner.width, 20f);
        var icon = gender.GetIcon();
        if (icon != null && gender != Gender.None)
        {
            GUI.DrawTexture(new Rect(titleRect.x, titleRect.y + 2f, 16f, 16f), icon);
        }

        string title;
        if (gi == null)
        {
            title = role == Gender.Female ? "AGB.UnknownDam".Translate() : "AGB.UnknownSire".Translate();
        }
        else if (pawn != null)
        {
            title = pawn.LabelShortCap + (pawn.Dead ? " " + "AGB.Deceased".Translate() : "");
        }
        else
        {
            title = (role == Gender.Female ? "AGB.Dam" : "AGB.Sire").Translate() + " " +
                    "AGB.GoneFromWorld".Translate();
        }

        Text.Font = GameFont.Small;
        Text.Anchor = TextAnchor.MiddleLeft;
        Widgets.Label(new Rect(titleRect.x + 20f, titleRect.y, titleRect.width - 20f, titleRect.height),
            title.Truncate(titleRect.width - 20f));

        if (pawn is { Spawned: true } && !isSelf && Widgets.ButtonInvisible(rect))
        {
            CameraJumper.TryJumpAndSelect(pawn);
        }

        if (gi == null || records == null)
        {
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            Widgets.Label(new Rect(inner.x, inner.y + 22f, inner.width, 18f), "AGB.NoRecord".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            return;
        }

        // Score line
        var score = BreedingScore.FromValues(species, s => records.TryGetValue(s, out var r) ? r.Value : 1f);
        Text.Font = GameFont.Tiny;
        GUI.color = BreedingScore.ColorFor(score);
        var scoreText = "AGB.ColScore".Translate() + " " + BreedingScore.Format(score);
        if (!isSelf && Kinship.Inbreeding(gi) > 0.001f)
        {
            scoreText += "   F " + (Kinship.Inbreeding(gi) * 100f).ToString("0.#") + "%";
        }

        Widgets.Label(new Rect(inner.x, inner.y + 20f, inner.width, 16f), scoreText);
        GUI.color = Color.white;

        // Genes in two columns
        var stats = BreedingUtility.GenesFor(species).ToList();
        var rows = Mathf.CeilToInt(stats.Count / 2f);
        var colW = inner.width / 2f;
        var lineH = isSelf ? LineHeight + 4f : LineHeight;
        for (var i = 0; i < stats.Count; i++)
        {
            var stat = stats[i];
            if (!records.TryGetValue(stat, out var record))
            {
                continue;
            }

            var col = i / rows;
            var row = i % rows;
            var cell = new Rect(inner.x + (col * colW), inner.y + 36f + (row * lineH), colW - 4f, lineH);
            GUI.color = Color.gray;
            Widgets.Label(cell, Constants.GetLabel(stat).CapitalizeFirst().Truncate(colW * 0.62f));
            GUI.color = Utilities.TextColor(record.Value);
            var valueText = (record.Value * 100f).ToString("F0") + "%";
            if (isSelf && record.Parent != GeneRecord.Source.None)
            {
                valueText += " " + global::AnimalGenetics.Genes.GetGenderSymbol(record.Parent);
            }

            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(cell, valueText);
            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = Color.white;
        }

        if (isSelf)
        {
            GUI.color = Color.gray;
            Widgets.Label(new Rect(inner.x, inner.yMax - 16f, inner.width, 16f), "AGB.PedigreeSourceHint".Translate());
            GUI.color = Color.white;
        }

        Text.Font = GameFont.Small;
    }
}
