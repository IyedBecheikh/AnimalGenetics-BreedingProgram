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
    public static readonly Texture2D RoleNone = ContentFinder<Texture2D>.Get("UI/AGB/Role_None");
    public static readonly Texture2D RoleBreeder = ContentFinder<Texture2D>.Get("UI/AGB/Role_Breeder");
    public static readonly Texture2D RoleKeep = ContentFinder<Texture2D>.Get("UI/AGB/Role_Keep");
    public static readonly Texture2D RoleCull = ContentFinder<Texture2D>.Get("UI/AGB/Role_Cull");

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
