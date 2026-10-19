# Patterns and pitfalls

## Project / XML
- Layout: `About/About.xml` (+ `Preview.png` 16:9), `1.6/Assemblies`, `1.6/Defs`, `1.6/Patches`,
  `Languages/English/Keyed`, `Textures/`. Keep `Source/`, `Tests/` and docs in the repo; ship only the game folders.
- `modDependencies` (with steamWorkshopUrl + downloadUrl) for hard deps; `loadAfter` for soft ones.
- Add a comp to every pawn: `PatchOperationAdd` on `/Defs/ThingDef[@Name="BasePawn"]/comps`. Same for
  `inspectorTabs` (custom `ITab`). Animal operations go on `/Defs/ThingDef[@Name="AnimalThingBase"]/recipes`.
  Note corpse defs also have `race` set, so filter `!def.IsCorpse` when enumerating races.
- Insert into another table's columns relative to an existing entry:
  `PatchOperationInsert` + `<order>Append|Prepend</order>` on `/Defs/PawnTableDef[defName="X"]/columns/li[text()="Y"]`.
- Optional-mod XML: `PatchOperationFindMod` matches the mod's **name** (About `<name>`), not packageId. Inside it you
  can move research (`tab`, `prerequisites`, `researchViewX/Y`), remove your own `ResearchTabDef`, add recipe users,
  and add stock generators to traders. Add comps by comp class: `ThingDef[comps/li/@Class="Ns.CompProperties_X"]/comps`.
- All `ResearchTabDef`s are always shown, even empty ones; remove yours when moving projects elsewhere.
- Items crafted at benches: `recipeMaker` on the ThingDef (`recipeUsers`, `researchPrerequisite`, `workSpeedStat`,
  `workSkill`, `skillRequirements`). Medical operations: `RecipeDef ParentName="SurgeryFlesh"`, `targetsBodyPart false`,
  `surgerySuccessChanceFactor 99999` (= never fails), `genderPrerequisite`, custom `workerClass : Recipe_Surgery`
  (override `AvailableOnNow`, `ApplyOnPawn`; ingredients are still valid objects inside `ApplyOnPawn`).
- Ingredient choice for bills: `Bill.IsFixedOrAllowedIngredient(Thing)` is virtual and used by the bill search.
  Postfix it to make a bill accept only specific things (e.g. the best matching sample). Exclude forbidden things,
  or the bill can deadlock.
- Storage building: `thingClass Building_Storage`, `building.fixedStorageSettings` / `defaultStorageSettings`,
  `maxItemsInCell`, `blueprintClass Blueprint_Storage`, `inspectorTabs ITab_Storage`. Several items in one cell are
  all drawn on top of each other; hide them with the item comp's `DontDrawParent()` if the building art already
  implies contents.
- Build-menu icon: `uiIconPath` to a padded icon (~74% content) when the world texture is full-bleed.
- Stack graphics: `Graphic_StackCount` with a folder `Name/Name_a.png, _b, _c` (1 / middle / full stack).
- Keyed strings: `"Key".Translate(args)`, with `{0}` placeholders and literal `\n` escapes in XML.
  Compare keys used in code vs keys defined (grep both, `comm -23`).

## Harmony
- Patch from `[StaticConstructorOnStartup]`. Patching in the `Mod` constructor can run another mod's static
  constructors before defs/`DefOf` exist and break it for the session.
- `__state` (prefix → postfix), `Finalizer` for cleanup that must run even on exceptions (restore temporarily changed
  settings, pop pushed context), iterator postfix `IEnumerable<T> Postfix(IEnumerable<T> __result)` to wrap generators.
- Private fields: `AccessTools.FieldRefAccess<T, F>("name")`; private/protected methods as fast delegates:
  `AccessTools.MethodDelegate<Action<T>>(AccessTools.Method(typeof(T), "Name"))`; protected property getter
  `AccessTools.PropertyGetter(typeof(T), "Prop")`.
- Optional-mod hooks without a compile-time reference: `AccessTools.TypeByName`, then `harmony.Patch(...)` inside
  `if (ModsConfig.IsActive(packageId))` + `try/catch` that logs one clear error.
- Tiny methods (one assignment) may be inlined: patches on them silently never run. Patch callers or consumers.
- Patching a base virtual method affects subclasses only where they call `base` (worked for
  `MainTabWindow_PawnTable.DoWindowContents`, which subclasses call).
- "Ambiguous match": pass parameter types (e.g. `ThingComp.Initialize(CompProperties)` vs a mod's own `Initialize(...)`).

## RimWorld API notes
- `GameComponent` subclasses are auto-instantiated; they need a `(Game game)` constructor. Save state with `ExposeData`.
  Prefer lists of `IExposable` with a `Scribe_Defs` field over `Dictionary<Def, ...>` (removed defs → null keys).
- Comp hooks: `CompGetGizmosExtra`, `CompInspectStringExtra`, `TransformLabel`, `AllowStackWith`,
  `PostSplitOff`, `DontDrawParent`, `CompTickRare` (needs `tickerType Rare`).
- Gizmos for multi-selection that open a float menu: subclass `Command`, override `InheritInteractionsFrom => false`,
  and apply the choice to `Find.Selector.SelectedPawns` yourself. Otherwise one menu opens per selected pawn.
- Jobs on animals: copy vanilla `JobDriver_GatherAnimalBodyResources`. Use `PawnUtility.ForceWait(animal, ...)` to
  hold it still, plus a progress toil with `WithProgressBar` and a finish action that releases the animal.
- WorkGiver_Scanner: `PotentialWorkThingsGlobal`, `ShouldSkip` (cheap), `HasJobOnThing` (also clear stale orders),
  `JobOnThing`. Register with a `WorkGiverDef` (`workType`, `priorityInType`, `requiredCapacities`).
- `StockGenerator.GenerateThings(PlanetTile forTile, Faction faction)` in 1.6 (was `int`). Market value scaling:
  add a `StatPart` to `StatDefOf.MarketValue.parts` at startup.
- Pregnancy without a father pawn: `Hediff_Pregnant.SetParents(null, null, null)` is supported. Egg-layers:
  `CompEggLayer.Fertilize(null)` works; stamp data on eggs in a `ProduceEgg` postfix.
- `MapPawns` notifies every open `MainTabWindow_PawnTable` on any pawn (de)registration, so big modlists rebuild
  tables constantly. Throttle with a leading-edge timer: refresh immediately once, then coalesce.
- `PawnTable.PawnTableOnGUI` culls cells but draws a separator line per row × column for *all* rows. Skipping
  off-screen lines (prefix on `Widgets.DrawLineHorizontal(float,float,float)` while inside the table's scroll view)
  made it ~3× faster.
- Listing_Standard 1.6: `SliderLabeled(label, val, min, max, labelPct, tooltip)`, `RadioButton(label, active, tabIn, tooltip)`,
  `IntAdjuster(ref val, step, min)`, `CheckboxLabeled(label, ref b, tooltip)`, `ButtonTextLabeledPct(...)`.
- Main-tab window coordinates: in `DoWindowContents(Rect rect)`, place extra buttons where neither the window's own
  controls nor the table header (e.g. the "Manage areas" header button) are. Verify with a screenshot.

## Integrating with a base mod (pattern used with Animal Genetics)
- Find its data hooks and reuse them rather than reimplementing: its per-pawn comp/record, its parent links, its
  "context stack" for births (`ParentReferences.Push/Pop`), and its generator function.
- Hook the generator once (prefix/postfix/finalizer) for everything that changes inheritance, applying effects in a
  defined order. Multiple independent patches on the same method fight over ordering.
- Keep a reference to the base mod's own record objects (not copies) when you carry data around (samples, genomes),
  so lineage and save references stay intact.
