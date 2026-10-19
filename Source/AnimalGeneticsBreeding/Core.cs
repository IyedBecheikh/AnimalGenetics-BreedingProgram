using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace AnimalGeneticsBreeding;

// Patch only after defs are loaded: patching Animal Genetics' methods earlier triggers its static
// Constants initializer before StatDefOf is ready, which breaks Animal Genetics for the whole session.
[StaticConstructorOnStartup]
public static class BreedingMod
{
    public const string HarmonyId = "iyedm.AnimalGeneticsBreeding";

    static BreedingMod()
    {
        new Harmony(HarmonyId).PatchAll(Assembly.GetExecutingAssembly());
    }
}

public enum HerdRole
{
    None,
    Breeder,
    Keep,
    Cull
}

public enum MatingMode
{
    Open,
    BreedersOnly,
    BestMales
}

[StaticConstructorOnStartup]
public static class Textures
{
    // Loaded on first use, not in a static initializer: another mod Harmony-patching these methods early would
    // otherwise run the initializer before content is loaded and leave the textures null.
    public static Texture2D RoleNone => roleNone ??= ContentFinder<Texture2D>.Get("UI/AGB/Role_None");
    private static Texture2D roleNone;
    public static Texture2D RoleBreeder => roleBreeder ??= ContentFinder<Texture2D>.Get("UI/AGB/Role_Breeder");
    private static Texture2D roleBreeder;
    public static Texture2D RoleKeep => roleKeep ??= ContentFinder<Texture2D>.Get("UI/AGB/Role_Keep");
    private static Texture2D roleKeep;
    public static Texture2D RoleCull => roleCull ??= ContentFinder<Texture2D>.Get("UI/AGB/Role_Cull");
    private static Texture2D roleCull;

    public static Texture2D For(HerdRole role)
    {
        return role switch
        {
            HerdRole.Breeder => RoleBreeder,
            HerdRole.Keep => RoleKeep,
            HerdRole.Cull => RoleCull,
            _ => RoleNone
        };
    }
}

public static class HerdRoleExtensions
{
    public static string Label(this HerdRole role)
    {
        return ("AGB.Role_" + role).Translate();
    }

    public static string Description(this HerdRole role)
    {
        return ("AGB.RoleDesc_" + role).Translate();
    }
}
