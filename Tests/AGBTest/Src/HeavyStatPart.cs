using System.Diagnostics;
using System.Text;
using RimWorld;
using Verse;

namespace AGBTest;

// Stands in for a big modlist: every stat evaluation also runs ~10 µs of other mods' stat parts.
public class HeavyStatPart : StatPart
{
    public static int Evaluations;
    private static readonly long SpinTicks = Stopwatch.Frequency / 100000; // 10 µs

    public override void TransformValue(StatRequest req, ref float val)
    {
        Evaluations++;
        var start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetTimestamp() - start < SpinTicks)
        {
        }
    }

    public override string ExplanationPart(StatRequest req) => null;

    public static void AddToAllStats()
    {
        foreach (var stat in DefDatabase<StatDef>.AllDefs)
        {
            stat.parts ??= [];
            stat.parts.Add(new HeavyStatPart { parentStat = stat });
        }
    }
}
