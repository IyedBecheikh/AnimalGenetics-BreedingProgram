# Animal Genetics: Breeding Program — design brainstorm

Working title for an add-on to **Animal Genetics (Continued)** (`Mlie.AnimalGenetics`) that gives the player the tools to actually *run* a breeding program: see the genes, pick who breeds, protect the good lines, cull the bad ones, and later bank and edit genetics. Soft-integrates with **Vanilla Genetics Expanded** (`VanillaExpanded.VGeneticsE` / GeneticRim) without copying its mechanics.

Reference sources cloned to `_ref/` (`_ref/ag` = Animal Genetics, `_ref/vge` = VGE, `_ref/gdoc.txt` = VGE design doc). Safe to delete once we start building.

---

## 1. What's actually wrong today (from the source)

| Problem | Where it comes from |
|---|---|
| Genes are 8 floats per animal (Health, Damage, Speed, Capacity, Meat, Leather, Wildness, Gather yield). Every child takes, per stat, the **better or worse parent value 50/50** (`bestGeneChance = 0.5`) plus a small Gaussian mutation (+3% ± 5%). | `GeneticCalculator.EnsureAllGenesExist` |
| **You can't pick the parents.** Vanilla mating pairs any fertile male with any fertile female of the species. AG just records whoever happened to mate. | vanilla `JobGiver_Mate` / `PawnUtility.FertileMateTarget`; AG only reads parents in `Hediff_Pregnant_DoBirthSpawn` |
| **Auto-slaughter can't see genes.** It culls by sex/age buckets, so a 160% speed stud is as likely to go as a 70% runt. | vanilla `AutoSlaughterManager.AnimalsToSlaughter` |
| **No overall "how good is this animal" number** for *your* goals. The tab shows 8 columns; you eyeball them. Market value averages all genes equally, which isn't what a wool farmer or a war-animal breeder cares about. | `MarketValueCalculator`, `MainTabWindow_AnimalGenetics` |
| **No progression or infrastructure.** Everything is visible from day one (`omniscientMode = true`), there's nothing to research, build, or craft, and good genes die with the animal. | `CoreSettings`, no Defs beyond tabs/columns |
| **VGE hybrids get random AG genes**, because they have no AG parents. The genome items say "muffalo", not *which* muffalo. | VGE `CompGenomorpher`/`CompElectroWomb` spawn pawns with no `ParentReferences` |

What AG does well and we keep: the gene model, inheritance, its tab/ITab, its settings, its Colony Manager + Multiplayer support. We build **on top** of it, never replace it.

---

## 2. Design pillars

1. **Control first, magic later.** Early game = choose who breeds and who dies. Mid game = bank and move genes. Late game = nudge mutations. Never "set gene to 200%".
2. **Don't remake VGE.** VGE is about *species* (genomes → hybrids, quality via genoframes, boosters for hybrid odds). We are about *individuals inside a species* (pedigree, studs, samples). Where the two meet, we make VGE's stuff carry AG data instead of adding parallel buildings.
3. **Hard dependency on AG only.** VGE, Colony Manager, Animal Tab etc. are optional patches (`MayRequire` / `loadAfter`).
4. **Safe to add mid-save**, ideally safe to remove (our data lives in our own comps/components, keyed off AG's existing `GeneticInformation`).
5. **Every new screen answers one question.** "Which animals should I keep?" "Who should breed with whom?" "What will the kids look like?"

---

## 3. Systems by progression tier

### Tier 0 — Herd Management (no research, available immediately)

This tier alone fixes the two complaints (no choice of mates, auto-slaughter destroys good genes).

**3.1 Breeding goals & Breeding Score**
- Per species (or per animal group), the player sets a **weight** for each AG gene: e.g. Muffalo = Capacity ×3, Speed ×2, Wool ×1, rest ×0.
- Each animal gets a **Breeding Score** = weighted average of `(gene − 1)` (Wildness inverted, as AG already does for market value).
- Presets: *Pack animal*, *Meat*, *Wool/Milk*, *War beast*, *Balanced*. Custom per species.
- Shown as a new sortable column in AG's Genetics tab + the vanilla Animals tab, and in the AG ITab.

**3.2 Herd roles (one-click tags on each animal)**
- **Breeder** — allowed to mate.
- **Keep** — never auto-slaughtered, never sold by "sell all" mods.
- **Cull** — first in line for auto-slaughter; won't be allowed to mate.
- **Untagged** — handled by rules below.
- Gizmo on the animal, column toggle in the tab, multi-select works.

**3.3 Mating control ("Studbook")**
- Per species toggle: *Open breeding* (vanilla) / *Studs only* / *Assigned pairs*.
- *Studs only*: only animals tagged **Breeder** may mate (patch `PawnUtility.FertileMateTarget` / `JobGiver_Mate`). Simple and covers 90% of use.
- *Assigned pairs*: drag a male onto one or more females in a pairing dialog; males only mate assigned females. For players who want to micromanage lines.
- Optional "auto-stud": keep the top N males by Breeding Score as Breeders automatically.

**3.4 Gene-aware auto-slaughter**
- Extend the vanilla Auto-slaughter dialog with: *Protect top N by score*, *Protect Keep/Breeder*, *Slaughter below score X*, and *Order by: lowest score first* (instead of vanilla's ordering).
- Implementation: postfix `AutoSlaughterManager.AnimalsToSlaughter` to re-rank/filter its result. Vanilla still decides *how many*; we decide *which*.
- Same rules applied to the Colony Manager livestock job if AG's Colony Manager integration is on (AG already patches `ManagerJob_Livestock.DoButcherJobs`).

**3.5 Offspring preview**
- Hover two animals (or open the pairing dialog) → per-gene expected range for kids: `min(parent)…max(parent)` shifted by AG's mutation settings, plus the chance the kid beats the herd's current best. Pure maths from AG settings, no new mechanic.

**3.6 Optional: genes start "unknown" (progression hook)**
- New setting (off by default so nobody loses info they have today): when on, your animals show gene **ranges** (e.g. "Speed 85–125%") instead of exact values.
- A handler doing a new **Assess animal** job (Animals skill) narrows the range; higher skill = tighter. Tier 2 sequencing gives exact values.
- Replaces AG's binary `omniscientMode` with a gradient. Gives Tier 0–2 a real reason to exist.

### Tier 1 — Selective Breeding (research, neolithic/medieval)

**3.7 Pedigree view**
- New ITab sub-page: 3-generation family tree with each ancestor's gene values (AG already stores `Mother`/`Father` `GeneticInformation` references, even for dead animals).
- Highlights which ancestor a gene came from (AG stores `GeneRecord.Parent`).

**3.8 Inbreeding (optional)**
- AG's FAQ says "may add an optional penalty". We add it: coefficient from shared ancestors; if enabled, inbred kids get extra negative mutation and a chance of a hereditary hediff ("Frail", "Weak lungs"). Shown in offspring preview so it's a decision, not a surprise.
- Gives the reason to bring in outside blood (buy/tame wild animals, or Tier 2 samples).

**3.9 Breeding pen marker (cheap building, optional)**
- 1×1 post. Animals inside an assigned pen area only mate with each other. Mostly a convenience over zones + Studs only; skip if Studbook covers it.

### Tier 2 — Germline Banking (research, industrial; needs Electricity. If VGE is loaded, requires VGE's *Genetic Alteration* and sits on VGE's research tab)

**3.10 Germline sample (item)**
- Collected from a living or freshly dead animal by a new work job at a **Germline Station** (or VGE's Genome Extractor Table if present — added as a bill/target there instead of a new table).
- The item carries a reference to that animal's `GeneticInformation`, species and sex role. Shows the full gene readout in its inspect pane. Market value scales with genes (reuse AG's formula).
- Spoils unless refrigerated or kept in the **Cryobank**.

**3.11 Cryobank (building)**
- Small powered storage (2×1) for samples; no spoilage. Lists stored samples with Breeding Scores. Think vanilla Gene Bank, but for animals.
- If VGE is loaded, reuse/extend VGE's **DNA Storage Bank** instead of adding a second freezer (decide once we inspect `Building_DNAStorageBank`).

**3.12 Artificial insemination**
- Doctor/handler job: use a male sample on a fertile female of the same species → she becomes pregnant (or eggs become fertilized) with the **sample as father**.
- Implementation: AG already reads the father from the `FatherGeneticInformation` comp on the vanilla `Pregnant` hediff. We create the hediff and set that comp to the sample's `GeneticInformation`. AG's birth code does the rest. Eggs: set AG's `EggGeneticInformation` on the produced fertilized egg.
- This is the big one: genes survive the animal's death, can be traded between colonies, and let you outcross without buying animals.

**3.13 Egg sorting**
- Fertilized eggs already carry AG genetics (`EggGeneticInformation`). Add a storage filter "Egg Breeding Score ≥ X" (`SpecialThingFilterDef`) so good eggs go to the hatchery and bad ones to the kitchen. Small, very useful for chicken farms.

### Tier 3 — Germline Tuning (research, spacer)

Nudges, not overrides. Each is consumed on an insemination or pregnancy.

**3.14 Breeding serums (items, applied with a sample or to a pregnant animal)**
- **Dominance serum (gene X)** — for one chosen gene, the child takes the better parent's value (forces AG's `bestGeneChance` to 100% for that stat only).
- **Stabilizing serum** — mutation std-dev ×0.25 for this birth (predictable kids).
- **Mutagenic serum** — mutation std-dev ×3 (gamble for outliers; pairs with "Cull" for a jackpot strategy).
- Implemented by pushing a modifier into AG's inheritance via a prefix/postfix around `GeneticCalculator.EnsureAllGenesExist` while that birth's `ParentReferences` record is active.
- Visual language matches VGE boosters (capsule vials) so they feel like the same lab, but they act on normal breeding, not hybrids.

**3.15 Gene splice (rare, expensive)**
- Copy one gene value from sample A into sample B, producing a new sample. Uses advanced components + time at the Germline Station. Hard cap: result can't exceed the best value in the colony's records + small bonus, so it accelerates breeding rather than skipping it.

---

## 4. VGE integration (only when VGE is loaded)

| VGE thing | What we add |
|---|---|
| Genome extraction (table / excavators) | When extracted from a **specific** animal, the genome item remembers that animal's AG genes. Generic genomes from corpses-by-bill stay random. |
| Genomorpher → growth cell → electrowomb | When the hybrid spawns, push an AG `ParentReferences` record with the two genome profiles as mother/father → hybrid inherits real genes instead of random. Genoframe quality could add a small positive mean shift. |
| Hybrid quality (`CompHybrid`) | Leave it alone. VGE quality and AG genes both multiply MoveSpeed/Leather/HealthScale/damage — they already stack multiplicatively. Show both lines in the stat explanation so players understand. |
| Fertile hybrids | Our Tier 0–3 tools work on them like any species. |
| Research tab | Our Tier 2/3 projects live on VGE's genetics tab and require VGE's *Genetic Alteration*; without VGE they go on a small tab of our own. |
| Genetic trader | Trader stock includes germline samples; they buy samples priced by genes. |
| VGE – More Lab Stuff | No overlap found (boosters, control implant, genetrainers, age pills). Nothing to do. |

---

## 5. Other installed mods to keep in mind

- **Colony Manager** — AG already integrates; our roles/score should feed its livestock job.
- **Animal Tab (Continued)** — add Breeding Score + role columns there too.
- **Animal Traits System** (hediff-based traits, some inheritable) — optional: let traits add/subtract from Breeding Score via a weight. Later.
- **Big and Small / Biotech Expansion mammals** etc. — anything with AG's comp works automatically.
- **Multiplayer** — AG ships `0MultiplayerAPI.dll`; our gizmos/dialogs must go through synced methods too.

---

## 6. Architecture sketch

- **Mod:** separate add-on, `packageId` e.g. `iyedm.AnimalGeneticsBreeding`. `modDependencies`: Harmony, `Mlie.AnimalGenetics`. `loadAfter`: AG, VGE. Folder `1.6/` (and `1.5/` if cheap).
- **AG hooks we rely on** (all public): `pawn.AnimalGenetics()`, `GeneticInformation.GeneRecords`, `Mother`/`Father`, `ParentReferences.Push/Pop`, `FatherGeneticInformation` (hediff comp on `Pregnant`), `EggGeneticInformation`, `Constants.AffectedStats`/`InvertedStats`, `CoreMod.Instance.Settings`.
- **Our data:** `CompBreedingRole` on pawns (tag), `WorldComponent_BreedingProgram` (per-species goals, mating mode, pairs, slaughter rules). Samples are `ThingWithComps` holding a `GeneticInformation` reference — AG's static `GeneticInformation.ExposeData` saves every live instance, so references from our items survive save/load (verify in testing).
- **Harmony targets (verified names exist in 1.6 Assembly-CSharp):** `AutoSlaughterManager.AnimalsToSlaughter`, `PawnUtility.FertileMateTarget`, `JobGiver_Mate`, `CompHatcher`, plus AG's own `GeneticCalculator.EnsureAllGenesExist` for serums. VGE patches only when loaded.
- **UI:** extend AG's Genetics main tab (new columns + a "Breeding" sub-tab for goals/pairs/rules) rather than adding another main button.

---

## 7. Assets needed (style references for image generation)

Rule: always feed the generator a reference texture at the same size from VGE/vanilla so line weight, shading and palette match.

| Asset | Size | Style reference |
|---|---|---|
| Germline sample (item, `_a/_b/_c` stack variants) | 128×128 | VGE `Things/Item/Boosters/*/…_a.png` (capsule vials) |
| Breeding serums ×3 | 128×128 | VGE booster vials (different liquid colors) |
| Cryobank (2×1, N/E/S) | ~448×192 / 192×448 | VGE `TableGeneticExtraction_*` (same footprint) |
| Germline Station (3×2, N/E/S) — skip if we reuse VGE's extractor | 448×384 | VGE `GenomeRecombinator_*` |
| Breeding pen post (1×1, optional) | 128×128 or 256×256 | vanilla animal sleeping spot / VGE electrowomb base for scale |
| Role icons: Breeder / Keep / Cull, Breeding Score column icon | 32–64 px UI | vanilla `UI/Icons` gizmo style; Biotech gene icons for the "lab" look |
| Research project icons (if own tab) | 128×128 | VGE research tab icons |

All reference files are in `Mods/2801160906/Textures/…`.

---

## 8. Build order (each phase is shippable on its own)

1. **MVP — Herd Management:** Breeding Score + goals, roles, Studs-only mating, gene-aware auto-slaughter, score columns. *Fixes both main complaints.* No art needed except 3–4 icons.
   **Status: built** (`Source/`, deployed by `deploy.sh`; in-game tests: `Tests/run-ingame-tests.sh`). Not yet included: "assigned pairs" mating, Colony Manager livestock integration, Multiplayer sync.
2. **Pedigree + offspring preview + optional inbreeding.**
   **Status: built.** Pedigree inspector tab (3 generations, genes, inbreeding coefficient); Pairings tab in the Breeding Program window (per-gene child prediction mirroring Animal Genetics' inheritance, odds of beating parents/herd, best-pair ranking); optional inbreeding depression in mod settings (off by default; Wright's coefficient on AG's parent graph). Hereditary-disease hediffs from the original idea were left out.
   **Also shipped:** performance hotfix for the Genetics / Wildlife / Animals tabs (see `PerfFixes*.cs`; measured ~3× faster with ~360 animals).
3. **Germline banking:** samples, cryobank, artificial insemination, egg filter. First real buildings/art.
   **Status: built.** Research "germline banking" (own Breeding research tab, needs Electricity). "Collect sample" toggle on adult males → handler job → sample item holding the donor's own GeneticInformation (3 days viable; frozen or powered cryobank = no decay; 3-day donor cooldown). "Inseminate..." on fertile females → pick a sample (shows donor score, expected child, relatedness) → handler job (60% + 3%/Animals level, max 95%). Mammals: pregnancy whose father genetics = sample (AG's own birth code does the rest). Egg-layers: vanilla fertilize with no male, each fertilized egg stamped with the sample as father. Cryobank: 1×1 powered storage, samples only. Egg storage filters: good / poor genes. No separate Germline Station; VGE DNA bank reuse moved to phase 4.
4. **VGE bridge:** genome profiles → hybrid genes, research-tab placement, trader stock.
5. **Serums + gene splice.**
   **Status (4 + 5): built.** VGE (all optional, via `PatchOperationFindMod` + by-name Harmony hooks in `VgeBridge.cs`): genomes from extractor bills/excavators remember the source animal (no stacking between different animals); genomorpher → growth cell → electrowomb carry the two genome genetics; hybrids (and failure results) are born with them as parents in Animal Genetics. Research moves to VGE's genetics tab after Genetic Alteration; serums also craftable at the tinkering table; the VGE genetics trader buys/sells germline samples ("pedigree stock", best of 3 random rolls) and serums. Sample value scales with genes (x(1 + 4·score), min x0.25). VGE's DNA storage bank is a genome generator, not storage, so it wasn't reused.
   Serums (research "germline tuning", drug lab): dominance (one chosen gene from the better parent), stabilizing (mutation spread x0.25), mutagenic (x3); applied to a female by a handler, used up at her next birth or after 15 days (eggs: genes rolled at laying). Gene splice: cryobank gizmo, doctor job, target gene moves 75% toward the donor's value, donor sample used up.
   Tests: `bash Tests/run-ingame-tests.sh` (no VGE) and `--vge` (loads VEF + VGE, ~70 s).
6. **Unknown-genes progression mode** (can slot in anywhere after 1). Not started.

---

## 9. Decisions for you

1. **Name / packageId** for the mod.
2. **Unknown-genes mode**: off by default (current behavior) or on (more progression)?
3. **Germline Station**: own building, or bolt onto VGE's Genome Extractor Table when VGE is present (and our own only without VGE)?
4. **Inbreeding**: include in phase 2, or leave out entirely?
5. Start with **phase 1 (MVP)** as code now?
