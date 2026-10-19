using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AnimalGeneticsBreeding;

/// <summary>
///     Fix 3: vanilla <see cref="PawnTable.PawnTableOnGUI" /> draws a separator line for every row × every column on
///     every GUI event, including rows scrolled out of view. With a few hundred animals that is ~9000 line draws
///     per frame (~16 ms measured), while all visible cells together cost under 2 ms. We skip separator lines that
///     fall outside the table's visible scroll range. Pure render culling: nothing visible changes.
/// </summary>
public static class PerfFixes_RowLines
{
    private static readonly AccessTools.FieldRef<PawnTable, Vector2> scrollField =
        AccessTools.FieldRefAccess<PawnTable, Vector2>("scrollPosition");

    private static int tableDepth;
    private static int scrollDepth;
    private static float visibleMin;
    private static float visibleMax;

    // Only cull inside the table's own (outermost) scroll view, never in headers or nested widgets.
    private static bool Culling => tableDepth > 0 && scrollDepth == 1;

    [HarmonyPatch(typeof(PawnTable), nameof(PawnTable.PawnTableOnGUI))]
    public static class PawnTable_PawnTableOnGUI
    {
        public static void Prefix(PawnTable __instance)
        {
            if (!PerfFixes.Enabled)
            {
                return;
            }

            tableDepth++;
            scrollDepth = 0;
            var scroll = scrollField(__instance);
            // Generous range: the visible area is never taller than the screen.
            visibleMin = scroll.y - 4f;
            visibleMax = scroll.y + UI.screenHeight + 4f;
        }

        public static void Finalizer()
        {
            if (tableDepth > 0)
            {
                tableDepth--;
            }

            scrollDepth = 0;
        }
    }

    [HarmonyPatch(typeof(Widgets), nameof(Widgets.BeginScrollView))]
    public static class Widgets_BeginScrollView
    {
        public static void Postfix()
        {
            if (tableDepth > 0)
            {
                scrollDepth++;
            }
        }
    }

    [HarmonyPatch(typeof(Widgets), nameof(Widgets.EndScrollView))]
    public static class Widgets_EndScrollView
    {
        public static void Prefix()
        {
            if (tableDepth > 0 && scrollDepth > 0)
            {
                scrollDepth--;
            }
        }
    }

    [HarmonyPatch(typeof(Widgets), nameof(Widgets.DrawLineHorizontal), typeof(float), typeof(float), typeof(float))]
    public static class Widgets_DrawLineHorizontal
    {
        public static bool Prefix(float y)
        {
            return !Culling || (y >= visibleMin && y <= visibleMax);
        }
    }
}
