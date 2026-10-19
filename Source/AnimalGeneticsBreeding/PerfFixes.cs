using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using AGWorld = AnimalGenetics.AnimalGenetics;

namespace AnimalGeneticsBreeding;

/// <summary>
///     Performance hotfixes for animal pawn tables.
///     1. Animal Genetics' Genetics tab compares a freshly built bool[] against a new array by reference, which is
///     always "changed", so it marks the whole table dirty on every GUI event and rebuilds it (re-query, sort,
///     column widths, window resize) several times per frame. We only let that SetDirty through when a filter
///     actually changed.
///     2. Vanilla rebuilds every open pawn table each time any pawn on the map spawns, despawns or changes faction.
///     Big modlists have constant churn, so the Wildlife/Animals/Genetics tabs rebuild nearly every frame. We
///     coalesce those notifications to at most one rebuild per <see cref="ChurnRefreshSeconds" /> (the first one
///     after a quiet period still applies immediately).
/// </summary>
public static class PerfFixes
{
    public const float ChurnRefreshSeconds = 0.5f;

    /// <summary>Kill switch (also used by the in-game benchmark to measure the unfixed baseline).</summary>
    public static bool Enabled = true;

    private static readonly ConditionalWeakTable<MainTabWindow_PawnTable, PendingState> pending = new();

    private static bool inGeneticsDraw;
    private static Snapshot drawStart;

    private static bool forceDirty;

    private static readonly System.Action<MainTabWindow_PawnTable> setDirty =
        AccessTools.MethodDelegate<System.Action<MainTabWindow_PawnTable>>(
            AccessTools.Method(typeof(MainTabWindow_PawnTable), "SetDirty"));

    private static readonly System.Func<MainTabWindow_PawnTable, PawnTableDef> tableDefOf =
        AccessTools.MethodDelegate<System.Func<MainTabWindow_PawnTable, PawnTableDef>>(
            AccessTools.PropertyGetter(typeof(MainTabWindow_PawnTable), "PawnTableDef"));

    private static readonly System.Collections.Generic.Dictionary<PawnTableDef, bool> animalDefCache = new();

    /// <summary>
    ///     Any window currently showing an animal table: vanilla Animals / Wildlife, Animal Genetics' Genetics tab,
    ///     and replacements such as Animal Tab (Continued) or Numbers (Numbers_Animals / Numbers_WildAnimals).
    ///     Decided by the table def shown right now, because some windows (Numbers) switch tables at runtime.
    /// </summary>
    private static bool IsAnimalTable(MainTabWindow_PawnTable window)
    {
        PawnTableDef def;
        try
        {
            def = tableDefOf(window);
        }
        catch
        {
            return false;
        }

        if (def == null)
        {
            return false;
        }

        if (!animalDefCache.TryGetValue(def, out var result))
        {
            result = def == PawnTableDefOf.Animals || def == PawnTableDefOf.Wildlife || def.defName == "Genetics" ||
                     def.defName is "Numbers_Animals" or "Numbers_WildAnimals";
            animalDefCache[def] = result;
        }

        return result;
    }

    private static Snapshot TakeSnapshot()
    {
        var world = Find.World?.GetComponent<AGWorld>();
        return new Snapshot
        {
            ShowAnimals = world?.ShowAnimals ?? false,
            ShowFaction = world?.ShowFaction ?? false,
            ShowOther = world?.ShowOther ?? false,
            ShowWild = world?.ShowWild ?? false,
            ShowHumans = world?.ShowHumans ?? false,
            FilterText = world?.FilterText,
            SortMode = global::AnimalGenetics.CoreMod.Instance.Settings.sortMode
        };
    }

    private struct Snapshot
    {
        public bool ShowAnimals, ShowFaction, ShowOther, ShowWild, ShowHumans;
        public string FilterText;
        public int SortMode;

        public bool SameAs(Snapshot other)
        {
            return ShowAnimals == other.ShowAnimals && ShowFaction == other.ShowFaction &&
                   ShowOther == other.ShowOther && ShowWild == other.ShowWild &&
                   ShowHumans == other.ShowHumans && SortMode == other.SortMode &&
                   (FilterText ?? "") == (other.FilterText ?? "");
        }
    }

    private class PendingState
    {
        public bool Dirty;
        public float LastRefresh = -999f;
    }

    // ---- Fix 1: Genetics tab per-event SetDirty

    [HarmonyPatch(typeof(global::AnimalGenetics.MainTabWindow_AnimalGenetics),
        nameof(global::AnimalGenetics.MainTabWindow_AnimalGenetics.DoWindowContents))]
    public static class GeneticsTab_DoWindowContents
    {
        [HarmonyPriority(Priority.First)]
        public static void Prefix()
        {
            inGeneticsDraw = true;
            drawStart = TakeSnapshot();
        }

        [HarmonyPriority(Priority.Last)]
        public static void Finalizer()
        {
            inGeneticsDraw = false;
        }
    }

    [HarmonyPatch(typeof(MainTabWindow_PawnTable), "SetDirty")]
    public static class PawnTableWindow_SetDirty
    {
        public static bool Prefix(MainTabWindow_PawnTable __instance)
        {
            if (!Enabled || forceDirty || !inGeneticsDraw ||
                __instance is not global::AnimalGenetics.MainTabWindow_AnimalGenetics)
            {
                return true;
            }

            // Inside the Genetics tab's draw: only a real filter/sort change justifies a rebuild.
            return !TakeSnapshot().SameAs(drawStart);
        }
    }

    // ---- Fix 2: coalesce pawn-churn rebuilds

    [HarmonyPatch(typeof(MainTabWindow_PawnTable), nameof(MainTabWindow_PawnTable.Notify_PawnsChanged))]
    public static class PawnTableWindow_NotifyPawnsChanged
    {
        public static bool Prefix(MainTabWindow_PawnTable __instance)
        {
            if (!Enabled || !IsAnimalTable(__instance))
            {
                return true;
            }

            // Leading-edge throttle: the first change after a quiet period refreshes at once (so filter clicks
            // and births show instantly); bursts inside the window collapse into one deferred refresh.
            var state = pending.GetOrCreateValue(__instance);
            var now = Time.realtimeSinceStartup;
            if (now - state.LastRefresh >= ChurnRefreshSeconds)
            {
                state.LastRefresh = now;
                state.Dirty = false;
                return true;
            }

            state.Dirty = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(MainTabWindow_PawnTable), nameof(MainTabWindow_PawnTable.DoWindowContents))]
    public static class PawnTableWindow_DoWindowContents
    {
        public static void Prefix(MainTabWindow_PawnTable __instance)
        {
            if (!pending.TryGetValue(__instance, out var state) || !state.Dirty)
            {
                return;
            }

            var now = Time.realtimeSinceStartup;
            if (Enabled && now - state.LastRefresh < ChurnRefreshSeconds)
            {
                return;
            }

            state.Dirty = false;
            state.LastRefresh = now;
            forceDirty = true;
            try
            {
                setDirty(__instance);
            }
            finally
            {
                forceDirty = false;
            }
        }
    }
}
