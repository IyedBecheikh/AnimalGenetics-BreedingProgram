using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AnimalGeneticsBreeding;

/// <summary>
///     Fix 4: every text cell (<see cref="PawnColumnWorker_Text" />) recomputes its string on every GUI event. For
///     stat columns (Numbers' meat / leather amount, tame chance, food consumption...) that is a full
///     GetStatValue per cell per event, and on a big modlist each stat runs the stat parts of many mods, so a
///     20-row Wildlife table alone can cost several ms per frame. Inside animal tables we reuse each cell's string
///     for about half a second (jittered so refreshes spread over frames). The text is still drawn every frame.
/// </summary>
public static class PerfFixes_TextCells
{
    public const float RefreshSeconds = 0.5f;
    private const int MaxEntries = 20000;

    private static readonly AccessTools.FieldRef<PawnTable, PawnTableDef> tableDef =
        AccessTools.FieldRefAccess<PawnTable, PawnTableDef>("def");

    private static readonly System.Func<PawnColumnWorker_Text, Pawn, string> getTextFor =
        AccessTools.MethodDelegate<System.Func<PawnColumnWorker_Text, Pawn, string>>(
            AccessTools.Method(typeof(PawnColumnWorker_Text), "GetTextFor"));

    private static readonly Dictionary<(PawnColumnWorker_Text, Pawn), Entry> cache = new();
    private static int animalTableDepth;

    // Not Verse.Rand: drawing must not advance the game's random state.
    private static readonly System.Random jitter = new();

    /// <summary>Calls to the real GetTextFor inside animal tables (read by the benchmark).</summary>
    public static int Computed;

    public static string GetText(PawnColumnWorker_Text worker, Pawn pawn)
    {
        if (animalTableDepth == 0)
        {
            return getTextFor(worker, pawn);
        }

        if (!PerfFixes.Enabled)
        {
            Computed++;
            return getTextFor(worker, pawn);
        }

        var now = Time.realtimeSinceStartup;
        var key = (worker, pawn);
        if (cache.TryGetValue(key, out var entry) && now < entry.Expires)
        {
            return entry.Text;
        }

        if (cache.Count > MaxEntries)
        {
            cache.Clear();
        }

        Computed++;
        var text = getTextFor(worker, pawn);
        cache[key] = new Entry { Text = text, Expires = now + RefreshSeconds * (0.75f + 0.5f * (float)jitter.NextDouble()) };
        return text;
    }

    private struct Entry
    {
        public string Text;
        public float Expires;
    }

    [HarmonyPatch(typeof(PawnColumnWorker_Text), nameof(PawnColumnWorker_Text.DoCell))]
    public static class PawnColumnWorker_Text_DoCell
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var target = AccessTools.Method(typeof(PawnColumnWorker_Text), "GetTextFor");
            var replacement = AccessTools.Method(typeof(PerfFixes_TextCells), nameof(GetText));
            var replaced = false;
            foreach (var code in instructions)
            {
                if (!replaced && code.Calls(target))
                {
                    replaced = true;
                    yield return new CodeInstruction(OpCodes.Call, replacement).MoveLabelsFrom(code);
                    continue;
                }

                yield return code;
            }

            if (!replaced)
            {
                Log.Warning("[AnimalGeneticsBreeding] PawnColumnWorker_Text.DoCell changed; text cell cache inactive.");
            }
        }
    }

    [HarmonyPatch(typeof(PawnTable), nameof(PawnTable.PawnTableOnGUI))]
    public static class PawnTable_PawnTableOnGUI
    {
        public static void Prefix(PawnTable __instance, out bool __state)
        {
            __state = PerfFixes.IsAnimalTableDef(tableDef(__instance));
            if (__state)
            {
                animalTableDepth++;
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state && animalTableDepth > 0)
            {
                animalTableDepth--;
            }
        }
    }

    // Re-sorting or refiltering rebuilds the table: show fresh values right after.
    [HarmonyPatch(typeof(PawnTable), nameof(PawnTable.SetDirty))]
    public static class PawnTable_SetDirty
    {
        public static void Postfix()
        {
            cache.Clear();
        }
    }
}
