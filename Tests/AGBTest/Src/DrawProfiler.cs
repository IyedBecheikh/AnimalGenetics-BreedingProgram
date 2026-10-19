using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AGBTest;

// Splits pawn-table draw time into: per-column DoCell, DoHeader, horizontal row lines, and the rest.
public static class DrawProfiler
{
    private static readonly Dictionary<string, long> ticks = new();
    private static readonly Dictionary<string, int> calls = new();
    private static readonly Stack<(string key, long start)> stack = new();
    private static bool patched;
    public static bool Active;

    public static void Reset()
    {
        ticks.Clear();
        calls.Clear();
    }

    private static void Begin(string key)
    {
        if (Active)
        {
            stack.Push((key, Stopwatch.GetTimestamp()));
        }
    }

    private static void End()
    {
        if (!Active || stack.Count == 0)
        {
            return;
        }

        var (key, start) = stack.Pop();
        ticks.TryGetValue(key, out var t);
        calls.TryGetValue(key, out var c);
        ticks[key] = t + (Stopwatch.GetTimestamp() - start);
        calls[key] = c + 1;
    }

    public static void TablePrefix() => Begin("TOTAL PawnTableOnGUI");
    public static void CellPrefix(PawnColumnWorker __instance) => Begin("DoCell " + __instance.GetType().Name);
    public static void HeaderPrefix() => Begin("DoHeader (all)");
    public static void LinePrefix() => Begin("Widgets.DrawLineHorizontal");
    public static void Postfix() => End();

    public static void Patch()
    {
        if (patched)
        {
            return;
        }

        patched = true;
        var h = new Harmony("iyedm.agbtest.drawprof");
        var post = new HarmonyMethod(typeof(DrawProfiler), nameof(Postfix));
        h.Patch(AccessTools.Method(typeof(PawnTable), nameof(PawnTable.PawnTableOnGUI)),
            new HarmonyMethod(typeof(DrawProfiler), nameof(TablePrefix)), post);
        h.Patch(AccessTools.Method(typeof(Widgets), nameof(Widgets.DrawLineHorizontal),
                [typeof(float), typeof(float), typeof(float)]),
            new HarmonyMethod(typeof(DrawProfiler), nameof(LinePrefix)), post);

        var workerTypes = DefDatabase<PawnColumnDef>.AllDefs.Select(d => d.workerClass).Where(t => t != null)
            .Distinct();
        var patchedMethods = new HashSet<System.Reflection.MethodBase>();
        foreach (var type in workerTypes)
        {
            foreach (var (name, prefix) in new[] { ("DoCell", nameof(CellPrefix)), ("DoHeader", nameof(HeaderPrefix)) })
            {
                var m = AccessTools.Method(type, name);
                if (m == null || m.IsAbstract || !patchedMethods.Add(m))
                {
                    continue;
                }

                try
                {
                    h.Patch(m, new HarmonyMethod(typeof(DrawProfiler), prefix), post);
                }
                catch (Exception e)
                {
                    Log.Warning($"[AGBPROF] could not patch {type.Name}.{name}: {e.Message}");
                }
            }
        }
    }

    public static IEnumerable<string> Report(string label, int frames)
    {
        foreach (var kv in ticks.OrderByDescending(kv => kv.Value).Take(14))
        {
            var ms = kv.Value * 1000.0 / Stopwatch.Frequency / frames;
            yield return $"[AGBPROF] {label,-14} {kv.Key,-48} {ms,7:F2} ms/frame  {calls[kv.Key] / (float)frames,8:F0} calls/frame";
        }
    }
}
