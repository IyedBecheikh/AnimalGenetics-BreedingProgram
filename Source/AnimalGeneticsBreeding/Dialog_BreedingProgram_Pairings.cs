using System.Collections.Generic;
using System.Linq;
using AnimalGenetics;
using RimWorld;
using UnityEngine;
using Verse;

namespace AnimalGeneticsBreeding;

/// <summary>Pairings tab: predicted offspring for a chosen sire × dam, plus the best-ranked pairs in the herd.</summary>
public partial class Dialog_BreedingProgram
{
    private const int SuggestedPairs = 8;

    private Pawn dam;
    private Vector2 pairScroll;
    private bool showPairings;
    private Pawn sire;

    private static bool Fertile(Pawn p, Gender gender)
    {
        return p.gender == gender && p.ageTracker.CurLifeStage.reproductive && !p.Sterile() && !p.Dead &&
               p.HerdRole() != HerdRole.Cull;
    }

    private void DrawPairings(Rect rect, List<Pawn> herd)
    {
        Widgets.DrawMenuSection(rect);
        var inner = rect.ContractedBy(8f);
        var males = herd.Where(p => Fertile(p, Gender.Male)).ToList();
        var females = herd.Where(p => Fertile(p, Gender.Female)).ToList();
        if (males.Count == 0 || females.Count == 0)
        {
            Widgets.Label(inner, "AGB.NoPairs".Translate());
            return;
        }

        if (sire == null || !males.Contains(sire))
        {
            sire = males[0];
        }

        if (dam == null || !females.Contains(dam))
        {
            dam = females[0];
        }

        var y = inner.y;

        // Pickers
        var half = (inner.width - 8f) / 2f;
        if (Widgets.ButtonText(new Rect(inner.x, y, half, 28f), "AGB.Sire".Translate() + ": " + PickLabel(sire)))
        {
            Find.WindowStack.Add(new FloatMenu(males.Select(m =>
                new FloatMenuOption(PickLabel(m), () => sire = m)).ToList()));
        }

        if (Widgets.ButtonText(new Rect(inner.x + half + 8f, y, half, 28f), "AGB.Dam".Translate() + ": " + PickLabel(dam)))
        {
            Find.WindowStack.Add(new FloatMenu(females.Select(f =>
                new FloatMenuOption(PickLabel(f), () => dam = f)).ToList()));
        }

        y += 36f;

        var herdBest = herd.Count > 0 ? herd.Max(p => p.BreedingScoreOf()) : 0f;
        var prediction = OffspringModel.Predict(selected, sire.AnimalGenetics(), dam.AnimalGenetics(), herdBest);

        // Gene table
        var cols = new[] { 0.34f, 0.16f, 0.16f, 0.16f, 0.18f };
        string[] headers =
        [
            "AGB.ColGene".Translate(), "AGB.Sire".Translate(), "AGB.Dam".Translate(),
            "AGB.ColChildAvg".Translate(), "AGB.ColChildRange".Translate()
        ];
        Text.Font = GameFont.Tiny;
        GUI.color = Color.gray;
        DrawRow(inner.x, y, inner.width, cols, headers, null);
        GUI.color = Color.white;
        y += 18f;
        Text.Font = GameFont.Small;
        foreach (var gene in prediction.Genes)
        {
            DrawRow(inner.x, y, inner.width, cols,
            [
                Constants.GetLabel(gene.Stat).CapitalizeFirst(), Pct(gene.Father), Pct(gene.Mother), Pct(gene.Mean),
                $"{Pct(gene.Low)}–{Pct(gene.High)}"
            ], [null, gene.Father, gene.Mother, gene.Mean, null]);
            y += 22f;
        }

        y += 6f;
        Widgets.DrawLineHorizontal(inner.x, y, inner.width);
        y += 6f;

        // Summary
        var summary = new List<string>
        {
            "AGB.PredScore".Translate(BreedingScore.Format(prediction.ScoreMean), BreedingScore.Format(prediction.ScoreLow),
                BreedingScore.Format(prediction.ScoreHigh)),
            "AGB.PredBeatsParents".Translate(prediction.ChanceBeatsParents.ToStringPercent()),
            "AGB.PredBeatsHerd".Translate(prediction.ChanceBeatsHerdBest.ToStringPercent(), BreedingScore.Format(herdBest)),
            "AGB.PredKinship".Translate((prediction.Coancestry * 100f).ToString("0.#"), Kinship.Describe(prediction.Coancestry)) +
            (prediction.Coancestry > 0.001f
                ? " " + (prediction.PenaltyPerGene > 0f
                    ? "AGB.PredPenalty".Translate((prediction.PenaltyPerGene * 100f).ToString("0.#")).Resolve()
                    : "AGB.PredPenaltyOff".Translate().Resolve())
                : "")
        };
        if (!BreedingRules.CanMate(sire) || !BreedingRules.CanMate(dam))
        {
            summary.Add("AGB.PredBlocked".Translate().Colorize(ColorLibrary.RedReadable));
        }

        foreach (var line in summary)
        {
            var h = Text.CalcHeight(line, inner.width);
            Widgets.Label(new Rect(inner.x, y, inner.width, h), line);
            y += h + 2f;
        }

        if (Widgets.ButtonText(new Rect(inner.x, y + 2f, 200f, 26f), "AGB.TagPairBreeders".Translate()))
        {
            sire.Breeding()?.SetRole(HerdRole.Breeder);
            dam.Breeding()?.SetRole(HerdRole.Breeder);
        }

        y += 36f;

        // Suggested pairs (closed-form expectation, all fertile combinations)
        Text.Font = GameFont.Small;
        Widgets.Label(new Rect(inner.x, y, inner.width, 24f), "AGB.SuggestedPairs".Translate());
        y += 24f;
        var pairs = (from m in males
                from f in females
                select (m, f, score: OffspringModel.ExpectedScore(selected, m.AnimalGenetics(), f.AnimalGenetics())))
            .OrderByDescending(t => t.score).Take(SuggestedPairs).ToList();

        var listRect = new Rect(inner.x, y, inner.width, inner.yMax - y);
        var view = new Rect(0f, 0f, listRect.width - 16f, pairs.Count * 24f);
        Widgets.BeginScrollView(listRect, ref pairScroll, view);
        var py = 0f;
        foreach (var (m, f, score) in pairs)
        {
            var row = new Rect(0f, py, view.width, 24f);
            if (m == sire && f == dam)
            {
                Widgets.DrawHighlightSelected(row);
            }
            else if (Mouse.IsOver(row))
            {
                Widgets.DrawHighlight(row);
            }

            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(row.x + 4f, row.y, row.width * 0.7f, row.height),
                $"{m.LabelShortCap} × {f.LabelShortCap}" +
                (BreedingRules.CanMate(m) && BreedingRules.CanMate(f) ? "" : "  " + "AGB.Blocked".Translate()));
            GUI.color = BreedingScore.ColorFor(score);
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(new Rect(row.x, row.y, row.width - 4f, row.height),
                "AGB.ExpectedShort".Translate(BreedingScore.Format(score)));
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            if (Widgets.ButtonInvisible(row))
            {
                sire = m;
                dam = f;
            }

            py += 24f;
        }

        Widgets.EndScrollView();
        Text.Font = GameFont.Small;
    }

    private static string PickLabel(Pawn p)
    {
        return $"{p.LabelShortCap} ({BreedingScore.Format(p.BreedingScoreOf())})";
    }

    private static string Pct(float v)
    {
        return (v * 100f).ToString("F0") + "%";
    }

    private static void DrawRow(float x, float y, float width, float[] cols, string[] cells, float?[] colorValues)
    {
        var cx = x;
        for (var i = 0; i < cols.Length; i++)
        {
            var w = width * cols[i];
            if (colorValues?[i] is { } v)
            {
                GUI.color = Utilities.TextColor(v);
            }

            Text.Anchor = i == 0 ? TextAnchor.MiddleLeft : TextAnchor.MiddleCenter;
            Widgets.Label(new Rect(cx, y, w, 22f), cells[i]);
            if (colorValues != null)
            {
                GUI.color = Color.white;
            }

            cx += w;
        }

        Text.Anchor = TextAnchor.UpperLeft;
    }
}
