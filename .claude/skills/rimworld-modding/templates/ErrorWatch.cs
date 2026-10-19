// Template: record every error/warning logged while the mod under test is on the stack.
using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace MyModTest;

[StaticConstructorOnStartup]
public static class ErrorWatch
{
    private const string WatchedNamespace = "MyMod."; // namespace of the mod under test

    public static readonly List<string> Captured = [];
    public static bool RecordAll;                       // set during UI smoke to also log other mods' errors
    public static readonly List<string> AllDuringUi = [];
    private static bool inside;

    static ErrorWatch()
    {
        var h = new Harmony("mymodtest.errorwatch");
        h.Patch(AccessTools.Method(typeof(Log), nameof(Log.Error), [typeof(string)]), new HarmonyMethod(typeof(ErrorWatch), nameof(OnLog)));
        h.Patch(AccessTools.Method(typeof(Log), nameof(Log.Warning), [typeof(string)]), new HarmonyMethod(typeof(ErrorWatch), nameof(OnLog)));
    }

    public static void OnLog(string text)
    {
        if (inside || text == null || text.StartsWith("[HARNESS]"))
        {
            return;
        }

        inside = true;
        try
        {
            if (RecordAll)
            {
                AllDuringUi.Add(text);
            }

            if (text.Contains(WatchedNamespace) || System.Environment.StackTrace.Contains(WatchedNamespace))
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
