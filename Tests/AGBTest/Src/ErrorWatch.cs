using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace AGBTest;

/// <summary>
///     Records every error/warning logged while our mod's code is on the stack (or that names it), for the whole run.
///     Reported as FAIL lines at the end, so "no exceptions" is checked everywhere, not just in asserted paths.
/// </summary>
[StaticConstructorOnStartup]
public static class ErrorWatch
{
    public static readonly List<string> Captured = [];

    /// <summary>While set (UI smoke), every error/warning is also kept here, whoever raised it.</summary>
    public static bool RecordAll;

    public static readonly List<string> AllDuringUi = [];
    private static bool inside;

    static ErrorWatch()
    {
        var h = new Harmony("iyedm.agbtest.errorwatch");
        h.Patch(AccessTools.Method(typeof(Log), nameof(Log.Error), [typeof(string)]),
            new HarmonyMethod(typeof(ErrorWatch), nameof(OnLog)));
        h.Patch(AccessTools.Method(typeof(Log), nameof(Log.Warning), [typeof(string)]),
            new HarmonyMethod(typeof(ErrorWatch), nameof(OnLog)));
    }

    public static void OnLog(string text)
    {
        if (inside || text == null || text.StartsWith("[AGBTEST]") || text.StartsWith("[AGBPROF]"))
        {
            return;
        }

        inside = true;
        try
        {
            if (RecordAll)
            {
                AllDuringUi.Add(text.Length > 300 ? text.Substring(0, 300) : text);
            }

            var stack = System.Environment.StackTrace;
            if (text.Contains("AnimalGeneticsBreeding") || stack.Contains("AnimalGeneticsBreeding."))
            {
                Captured.Add(text.Length > 400 ? text.Substring(0, 400) : text);
            }
        }
        finally
        {
            inside = false;
        }
    }
}
