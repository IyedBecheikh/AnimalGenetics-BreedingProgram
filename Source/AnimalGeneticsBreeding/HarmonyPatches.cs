using System.Collections.Generic;
using AnimalGenetics;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AnimalGeneticsBreeding;

[HarmonyPatch(typeof(PawnUtility), nameof(PawnUtility.FertileMateTarget))]
public static class PawnUtility_FertileMateTarget
{
    public static void Postfix(Pawn male, Pawn female, ref bool __result)
    {
        if (__result && (!BreedingRules.CanMate(male) || !BreedingRules.CanMate(female)))
        {
            __result = false;
        }
    }
}

[HarmonyPatch(typeof(JobGiver_Mate), "TryGiveJob")]
public static class JobGiver_Mate_TryGiveJob
{
    // Skip the reachability search entirely for males that aren't allowed to breed.
    public static bool Prefix(Pawn pawn, ref Job __result)
    {
        if (BreedingRules.CanMate(pawn))
        {
            return true;
        }

        __result = null;
        return false;
    }
}

[HarmonyPatch(typeof(AutoSlaughterManager), nameof(AutoSlaughterManager.AnimalsToSlaughter), MethodType.Getter)]
public static class AutoSlaughterManager_AnimalsToSlaughter
{
    private static readonly AccessTools.FieldRef<AutoSlaughterManager, bool> cacheDirty =
        AccessTools.FieldRefAccess<AutoSlaughterManager, bool>("cacheDirty");

    public static void Prefix(AutoSlaughterManager __instance, out bool __state)
    {
        __state = cacheDirty(__instance);
    }

    // Only re-rank when vanilla actually rebuilt its cached list.
    public static void Postfix(AutoSlaughterManager __instance, List<Pawn> __result, bool __state)
    {
        if (!__state || __result == null)
        {
            return;
        }

        try
        {
            BreedingRules.Reselect(__instance, __result);
        }
        catch (System.Exception e)
        {
            Log.ErrorOnce($"[AnimalGeneticsBreeding] Gene-aware auto-slaughter failed: {e}", 0x4A47B002);
        }
    }
}

[HarmonyPatch(typeof(MainTabWindow_AnimalGenetics), nameof(MainTabWindow_AnimalGenetics.DoWindowContents))]
public static class MainTabWindow_AnimalGenetics_DoWindowContents
{
    public static void Postfix(Rect rect)
    {
        // Animal Genetics' own controls end at rect.width - 530 (search box); the right edge belongs to the
        // table's "Manage areas" header button.
        var buttonRect = new Rect(rect.width - 530f - 12f - 170f, 10f, 170f, 24f);
        if (Widgets.ButtonText(buttonRect, "AGB.OpenProgram".Translate()))
        {
            Find.WindowStack.Add(new Dialog_BreedingProgram(null));
        }
    }
}
