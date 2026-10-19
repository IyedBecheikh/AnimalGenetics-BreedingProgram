---
name: rimworld-modding
description: Build, test and publish RimWorld (1.5/1.6) C# + XML mods efficiently, especially add-ons that integrate with other mods. Use whenever the task is to create, extend, debug, profile, test, or publish a RimWorld mod; to patch vanilla or another mod with Harmony or XML; to make RimWorld textures; or to verify a mod in game. Covers the toolchain (dotnet, decompiling the installed game and mods), proven XML/Harmony patterns and pitfalls, and — most importantly — the isolated in-game integration test harness that runs real game logic in ~40-130 s without touching the user's modlist.
---

# RimWorld modding

Learned while building *Animal Genetics: Breeding Program*
(https://github.com/IyedBecheikh/AnimalGenetics-BreedingProgram), an add-on to Animal Genetics with
optional Vanilla Genetics Expanded integration. That repo is the worked example for everything here:
`Source/` (mod), `Tests/` (harness), `deploy.sh`.

Read the reference files as needed:
- `references/harness.md`: **the in-game test harness** (read before verifying anything in game)
- `references/patterns.md`: XML + Harmony + RimWorld API patterns and the pitfalls that cost time
- `references/art.md`: generating textures that match the game / Vanilla Expanded style
- `references/publishing.md`: repo layout, README, release zip
- `templates/`: copyable harness skeleton (`HarnessComponent.cs`, `ErrorWatch.cs`, `Harness.csproj`, `run-ingame-tests.sh`)

## Workflow that worked

1. **Read the real code, not docs.** Decompile the *installed* `Assembly-CSharp.dll` and the target mods'
   DLLs with `ilspycmd` (see below). GitHub sources of mods are often a version ahead or behind the
   Steam build; patch what is installed.
2. **Design as an add-on.** Depend on the base mod's public API (its comps, static helpers), hard-depend
   only on what is required, and make other integrations optional (`PatchOperationFindMod` + by-name Harmony hooks).
3. **Build** a `net48` class library against the game DLLs (`Private=false`), output to `1.6/Assemblies`.
4. **Verify in game with the harness**, never by asking the user to launch their game. The user's full
   modlist can take 20+ minutes to load; the harness uses an isolated save-data folder and a 5-mod list.
5. **Screenshot every new UI** from the harness and look at it: most real UI bugs (overlaps, overflow,
   wrong icon size, cluttered stacks) were only visible this way.
6. Publish (public repo + release zip) only after base, optional-mod and UI-mod runs are all green.

## Toolchain quick reference

```bash
# Machine-specific: if C: is full, redirect every dotnet cache/temp to another drive.
export NUGET_PACKAGES="D:\\dev\\nuget" DOTNET_CLI_HOME="D:\\dev\\home" TMP="D:\\dev\\tmp" TEMP="D:\\dev\\tmp" DOTNET_NOLOGO=1

# Decompiler (once): installs to a folder, no admin.
dotnet tool install ilspycmd --tool-path "D:\\dev\\tools"
ilspycmd -t Verse.AutoSlaughterManager "<game>/RimWorldWin64_Data/Managed/Assembly-CSharp.dll"   # one type
ilspycmd "<game>/RimWorldWin64_Data/Managed/Assembly-CSharp.dll" -p -o D:\\dev\\decomp              # whole game, then grep
ilspycmd "<Mods>/<id>/1.6/Assemblies/Other.dll" -p -o D:\\dev\\other -r "<game>/RimWorldWin64_Data/Managed"
```

- Target the **same .NET Framework as the mod you reference** (Animal Genetics is net48; net472 fails to resolve it).
- C# `LangVersion latest` works (SDK 10). Mods compiled with C# 14 `extension` blocks are callable as normal extension methods.
- Mod folders in a Steam/manual install are named by Workshop ID (`Mods/2830943477`); read `About/About.xml`
  with an XML parser to get `name` and `packageId` (dependency `packageId`s appear later in the file).
- Inline shell heredocs and `sed` mangle backslashes and quotes on Windows Git Bash. For anything non-trivial,
  write a small Python script file and run it with `python -I script.py`.

## Highest-value lessons (details in references)

- **Patch in a `[StaticConstructorOnStartup]` class, not the `Mod` constructor.** Patching another mod's methods
  early triggered its static initializer before `DefOf`s were ready and broke that mod for the whole session.
- Harmony **cannot see calls to tiny methods the JIT inlined** (e.g. `PawnTable.SetDirty`); patch the caller or
  the place the state is consumed instead.
- `AccessTools.Method(type, "Initialize")` on a `ThingComp` subclass is ambiguous (base `Initialize(CompProperties)`); pass parameter types.
- Vanilla requirements look like bugs: animal surgery needs the patient in a bed/sleeping spot; slaughter needs a
  violence-capable colonist; `Application.Quit()` is asynchronous (the game keeps ticking).
- Replacement windows: Numbers and Animal Tab swap vanilla tab windows for their own classes, and Numbers swaps
  the shown `PawnTableDef` at runtime. Detect by the *current* table def, not by window type.
- A comp can hide its parent from drawing with `DontDrawParent()` (e.g. items stored inside a cabinet), no patch needed.
- Measure before optimizing: one profile showed 16 of 20 ms/frame was vanilla drawing row lines for off-screen rows.
