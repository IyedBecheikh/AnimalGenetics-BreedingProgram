using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AnimalGenetics;
using RimWorld;
using UnityEngine;
using Verse;

namespace AnimalGeneticsBreeding;

public static class BreedingUi
{
    private static readonly HerdRole[] roles = [HerdRole.None, HerdRole.Breeder, HerdRole.Keep, HerdRole.Cull];

    public static List<FloatMenuOption> RoleOptions(List<Pawn> pawns, bool includeProgramLink = true)
    {
        var options = new List<FloatMenuOption>();
        foreach (var role in roles)
        {
            var label = pawns.Count > 1
                ? "AGB.SetRoleMany".Translate(role.Label(), pawns.Count).Resolve()
                : role.Label();
            options.Add(new FloatMenuOption(label, () =>
            {
                foreach (var pawn in pawns)
                {
                    pawn.Breeding()?.SetRole(role);
                }
            }, Textures.For(role), Color.white));
        }

        if (!includeProgramLink)
        {
            return options;
        }

        foreach (var def in pawns.Select(p => p.def).Distinct())
        {
            options.Add(new FloatMenuOption("AGB.OpenProgramFor".Translate(def.LabelCap),
                () => Find.WindowStack.Add(new Dialog_BreedingProgram(def))));
            options.Add(new FloatMenuOption("AGB.PreviewPairings".Translate(def.LabelCap),
                () => Find.WindowStack.Add(new Dialog_BreedingProgram(def, true))));
        }

        return options;
    }

    public static void DrawRoleButton(Rect rect, Pawn pawn)
    {
        var role = pawn.HerdRole();
        var iconRect = new Rect(0f, 0f, 24f, 24f).CenteredOnXIn(rect).CenteredOnYIn(rect);
        if (Widgets.ButtonImage(iconRect, Textures.For(role), role == HerdRole.None ? Color.gray : Color.white))
        {
            Find.WindowStack.Add(new FloatMenu(RoleOptions([pawn], false)));
        }

        TooltipHandler.TipRegion(iconRect, role.Label().CapitalizeFirst() + "\n\n" + role.Description());
    }

    public static void DrawScore(Rect rect, Pawn pawn)
    {
        var score = pawn.BreedingScoreOf();
        GUI.color = BreedingScore.ColorFor(score);
        Text.Anchor = TextAnchor.MiddleCenter;
        Widgets.Label(rect, BreedingScore.Format(score));
        Text.Anchor = TextAnchor.UpperLeft;
        GUI.color = Color.white;
        TooltipHandler.TipRegion(rect, new TipSignal(() => ExplainScore(pawn), pawn.thingIDNumber ^ 0x4A47B003));
    }

    public static string ExplainScore(Pawn pawn)
    {
        var program = BreedingProgram.Get(pawn.def);
        var sb = new StringBuilder();
        sb.AppendLine("AGB.ScoreExplainHeader".Translate(BreedingPresets.LabelFor(program.presetKey)));
        sb.AppendLine();
        foreach (var stat in pawn.GetGenes())
        {
            var weight = program.GetWeight(stat);
            if (weight <= 0f)
            {
                continue;
            }

            sb.AppendLine($"  {AgStats.Label(stat).CapitalizeFirst()}: {Genes.GetGene(pawn, stat) * 100f:F0}%  ×{weight:0.#}");
        }

        sb.AppendLine();
        sb.Append("AGB.ScoreExplainTotal".Translate(BreedingScore.Format(pawn.BreedingScoreOf())));
        return sb.ToString();
    }
}

public class Command_HerdRole : Command
{
    private readonly CompBreeding comp;

    public Command_HerdRole(CompBreeding comp)
    {
        this.comp = comp;
        defaultLabel = "AGB.CommandRole".Translate(comp.Role.Label());
        defaultDesc = "AGB.CommandRoleDesc".Translate();
        icon = Textures.For(comp.Role);
        groupKey = 0x4A47B001;
    }

    // One float menu for the whole selection, not one per selected animal.
    public override bool InheritInteractionsFrom(Gizmo other)
    {
        return false;
    }

    public override void ProcessInput(Event ev)
    {
        base.ProcessInput(ev);
        var selected = Find.Selector.SelectedPawns.Where(BreedingUtility.IsManagedAnimal).ToList();
        if (!selected.Contains(comp.Pawn))
        {
            selected.Add(comp.Pawn);
        }

        Find.WindowStack.Add(new FloatMenu(BreedingUi.RoleOptions(selected)));
    }
}

public class PawnColumnWorker_BreedingScore : PawnColumnWorker
{
    protected override GameFont DefaultHeaderFont => GameFont.Tiny;

    public override void DoCell(Rect rect, Pawn pawn, PawnTable table)
    {
        if (pawn.AnimalGenetics() == null || !pawn.RaceProps.Animal)
        {
            return;
        }

        BreedingUi.DrawScore(rect, pawn);
    }

    public override int GetMinWidth(PawnTable table)
    {
        return 60;
    }

    public override int Compare(Pawn a, Pawn b)
    {
        return a.BreedingScoreOf().CompareTo(b.BreedingScoreOf());
    }
}

public class PawnColumnWorker_HerdRole : PawnColumnWorker
{
    protected override GameFont DefaultHeaderFont => GameFont.Tiny;

    public override void DoCell(Rect rect, Pawn pawn, PawnTable table)
    {
        if (!BreedingUtility.IsManagedAnimal(pawn))
        {
            return;
        }

        BreedingUi.DrawRoleButton(rect, pawn);
    }

    public override int GetMinWidth(PawnTable table)
    {
        return 40;
    }

    public override int Compare(Pawn a, Pawn b)
    {
        return a.HerdRole().CompareTo(b.HerdRole());
    }
}
