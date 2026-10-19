# The in-game integration test harness

Goal: run *real* RimWorld logic (jobs, mating, births, save/load, UI drawing) against the mod, assert on it,
take screenshots, and quit, in ~40-130 seconds, without touching the user's config, saves or modlist.

Worked example: `Tests/` in https://github.com/IyedBecheikh/AnimalGenetics-BreedingProgram
(`AGBTestComponent.cs` = driver; `Phase*Tests.cs`, `ScenarioTests.cs`, `UiSmoke.cs`, `ErrorWatch.cs`,
`PerfBench.cs`, `DrawProfiler.cs`; `run-ingame-tests.sh` = runner). Skeleton in `../templates/`.

## Architecture

```
run-ingame-tests.sh
 ├─ build mod + harness (dotnet)
 ├─ copy mod and harness into <game>/Mods (harness removed again afterwards)
 ├─ write an isolated  <data>/Config/ModsConfig.xml   (Harmony, Core, deps, mod, harness [+ optional mods])
 ├─ RimWorldWin64.exe -savedatafolder=<data> -logfile <D:\...\player.log> -quicktest -popupwindow
 │     -screen-width 1600 -screen-height 900
 └─ print <data>/agbtest_results.txt; exit non-zero unless "DONE fails=0"
harness mod (depends on the mod under test)
 └─ GameComponent: tick-driven state machine → checks → save → LoadGame → verify → UI/screenshots → Finish()
```

Key facts:
- `-savedatafolder=` gives a private Config/Saves/Prefs. **Never** use the user's full modlist (20+ min loads);
  if they want a compatibility check, add the specific mods behind a flag (`--ui`, `--vge`).
- `-quicktest` starts a small generated colony immediately (3 colonists, random animals, random season).
- `-logfile` on a drive with space. Unity's default log goes to `%LOCALAPPDATA%Low` on C:.
- The harness writes its own results file. Don't rely on parsing Player.log for pass/fail.
- Optional mods via placeholders in the generated `ModsConfig.xml` replaced by `sed` per flag
  (lower-case packageIds work).
- Benchmarks are opt-in (`--perf`, flag file in the data folder): they double the runtime.

## Driver patterns (all proven)

**Tick state machine.** `GameComponentTick` runs synchronous checks at tick ≥120, then hands control to
phase classes that each expose `Start()`, `Tick()` and `Active`. Long scenarios set
`Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast` and advance by stages with timeouts.

**Checks.** `Check(bool ok, string what)` appends `[AGBTEST] PASS|FAIL what` to the results file and the log,
and counts failures. Put the measured value in the message (`got 0.0124`) so failures are diagnosable from the file alone.

**Save/reload.** Write a marker file with the thingIDNumbers to re-find, call `GameDataSaveLoader.SaveGame("x")`
then `GameDataSaveLoader.LoadGame("x")`. In the new game's component, `LoadedGame()` sees the marker and
verifies on the next tick. Verify invariants ("every sample keeps its genetics"), not specific objects that
earlier scenarios may legitimately have consumed.

**Finish.** Set a static `finished` flag *before* `Application.Quit()`. Quit is asynchronous, and without the flag the
reloaded game ran the whole suite a second time and overwrote the results.

**Real jobs, deterministically.** Don't wait for the AI to choose your work:
```csharp
var giver = (WorkGiver_Scanner)DefDatabase<WorkGiverDef>.GetNamed("X").Worker;
if (giver.HasJobOnThing(worker, target, forced: true))
    worker.jobs.StartJob(giver.JobOnThing(worker, target, true), JobCondition.InterruptForced);
```
Teleport the worker next to the target (`Position = ...; Notify_Teleported()`), set the relevant skill to 20, and
keep them working each tick: top up all needs, end mental states, re-issue the job every ~500 ticks if
`CurJobDef` drifted (`KeepWorking` helper). This removed every timing flake. `JobFailReason.Reason` right after a
failed `HasJobOnThing` tells you why (e.g. "not ready for surgery" = patient not in bed).

**Natural behaviour.** To test AI behaviour (mating), make it frequent instead of waiting:
`race.mateMtbHours = 0.01f`, then restore it. Count events with a Harmony postfix on the vanilla method
(`PawnUtility.Mated`). Assert on *who* did it, and only require a low minimum count.

**Fresh fixtures.** Later scenarios must spawn their own pawns (`PawnGenerator.GeneratePawn(new
PawnGenerationRequest(kind, Faction.OfPlayer, fixedGender:, fixedBiologicalAge:, fixedChronologicalAge:))`),
because earlier phases leave animals pregnant, sampled, culled or dead.
Pick workers by capability: `!WorkTypeIsDisabled(WorkTypeDefOf.Doctor)`, `!WorkTagIsDisabled(WorkTags.Violent)`.
Iterate `.ToList()` copies when teleporting pawns (registries change during iteration).

**Statistics against the real generator.** When the mod predicts or alters random outcomes, sample the base
mod's real generator thousands of times and compare means/SDs, with a tolerance derived from the measured SD
(about 4 standard errors). A fixed tolerance produced one false failure.

**Births.** `Hediff_Pregnant.DoBirthSpawn(mother, father)` spawns offspring through the normal path (other mods'
birth patches run). Diff `map.mapPawns.AllPawnsSpawned` before/after to find the newborns.

**Calling private code of an optional mod.** `AccessTools.TypeByName("Ns.Type")`, then `AccessTools.Method(type, name, paramTypes)`,
then `.Invoke`. `Traverse.Create(obj).Field("x").SetValue(v)` sets dialog/window state for screenshots.

## Catch every error, not just asserted ones (`ErrorWatch`)

Harmony-prefix `Log.Error(string)` and `Log.Warning(string)`. If the message or `Environment.StackTrace` contains
your namespace, record it, then report every recorded line as FAIL at `Finish()`. While the UI smoke test runs, also
record errors from any source and print them as INFO. The UI smoke run with RimHUD, Dubs Mint Menus,
Numbers, Animal Tab and Scrollable Inspect Panes loaded proved compatibility this way.

## UI: screenshots and smoke

- Work in `GameComponentUpdate` (per frame), with a stage counter and `nextFrame = Time.frameCount + 30`, so
  windows have drawn before capture: `ScreenCapture.CaptureScreenshot(path)` (reference `UnityEngine.ScreenCaptureModule`).
- Open things: `Find.WindowStack.Add(new MyDialog())`; main tabs `Find.MainTabsRoot.SetCurrentTab(def, false)`;
  inspect tab `Find.Selector.Select(t)`, then `SetCurrentTab(MainButtonDefOf.Inspect)` and
  `((MainTabWindow_Inspect)MainButtonDefOf.Inspect.TabWindow).OpenTabType = typeof(ITab_X)`;
  Architect category via `Traverse` on `desPanelsCached` / `selectedDesPanel`; mod settings `new Dialog_ModSettings(LoadedModManager.GetMod<MyMod>())`.
- Camera close-up: `Find.CameraDriver.SetRootPosAndSize(cell.ToVector3Shifted(), 4f)`. Place test objects on a clear
  cell (check radius for pawns/items/buildings) or other things overlap them.
- **UiSmoke:** a list of `(name, Action open)` steps (every window/tab/menu/inspect pane in many states: wild animal,
  colonist, newborn, hybrid, etc.). Each step opens, waits ~6 frames, closes. Then assert ErrorWatch saw nothing new.
- Crop screenshots with PIL before viewing (full 1920×1080 frames are mostly map).
- Lighting differs by in-game hour; a "brown" sprite may just be dawn.

## Performance measurement

- Window contents draw inside Unity's deferred `GUI.Window` callback. Time `Verse.Window.InnerWindowOnGUI`
  (filter by window type), **not** `WindowStack.WindowStackOnGUI` (that measured ~0 ms).
- Count expensive work by prefixing it and reading state (e.g. `PawnTable.RecacheIfDirty` + the private `dirty` field
  via `FieldRefAccess`) instead of patching tiny setters that get inlined. To find who triggers it, log
  `Environment.StackTrace` from that prefix a few times.
- DrawProfiler: patch each `PawnColumnWorker` subclass's declared `DoCell`, plus `Widgets.DrawLineHorizontal`.
  Patching undeclared overrides throws "You can only patch implemented methods"; catch and skip those.
- Simulate churn (spawn+destroy a pawn every frame) and report `ms/frame` and `rebuilds/frame` for fix off/on.
- Use a single static `Enabled` kill switch in the mod's perf patches so the bench can measure the baseline.
- Spawning random generated wild animals at random cells occasionally throws in vanilla code (flight tracker on
  unwalkable cells). That's harness noise; don't attribute it to the mod.

## Typical runtimes (this project)
base ~45-100 s · with VEF+VGE ~130 s · with five UI mods + perf ~160 s.
