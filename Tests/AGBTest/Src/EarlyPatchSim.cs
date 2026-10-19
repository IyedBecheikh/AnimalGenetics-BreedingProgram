using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace AGBTest;

// Reproduces what Quality Bionics Remastered does: Harmony-patch the mod's surgery recipes (and, wider, every
// plain method of the mod) from a Mod constructor, before DefOfs are bound. Patching recompiles a method; if it
// referenced AnimalGenetics.Constants, Mono would run Constants' initializer then and break Animal Genetics.
public class EarlyPatchSim : Mod
{
    public static int Patched;
    public static string FirstFailure;

    public EarlyPatchSim(ModContentPack content) : base(content)
    {
        var harmony = new Harmony("iyedm.agbtest.earlypatch");
        var postfix = new HarmonyMethod(typeof(EarlyPatchSim), nameof(Noop));
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.DeclaredOnly;
        foreach (var type in typeof(AnimalGeneticsBreeding.BreedingMod).Assembly.GetTypes())
        {
            if (type.IsInterface || type.ContainsGenericParameters || type.IsDefined(typeof(HarmonyPatch), true) ||
                type.Name == "AgStats")
            {
                continue;
            }

            foreach (var method in type.GetMethods(flags))
            {
                if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() == null)
                {
                    continue;
                }

                try
                {
                    harmony.Patch(method, postfix: postfix);
                    Patched++;
                }
                catch (Exception e)
                {
                    FirstFailure ??= $"{type.Name}.{method.Name}: {e.GetBaseException().Message}";
                }
            }
        }
    }

    public static void Noop()
    {
    }
}
