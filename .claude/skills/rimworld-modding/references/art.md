# Textures that match the game

- **Always give the generator real reference textures** at the target size from the game or from Vanilla Expanded
  mods (VE is the community style benchmark). Label their roles (style reference, scale reference). Good references:
  VE `Textures/UI/commands/*.png` (128 px command icons), VGE `Things/Item/Boosters/*` (items on a tray),
  VGE `Things/Building/Electrowombs/Electrowomb_Base.png` (1×1 building, top-down), your own earlier assets for consistency.
- **One generation per related set**: put 4-6 assets in a transparent grid (1024×1024 2×2, 1536×1024 3×2) with
  "one asset per cell, ~10% margin, no text". Cheaper, and the set comes out coherent.
- **Post-process deterministically with PIL** (no extra paid calls):
  - zero out low alpha (`a < 40`; up to `150` when the model painted a soft grey background/shadow);
  - crop each asset by its *actual* bounding box. Generated rows don't always sit on the grid, so crop by
    row/column ranges you've checked, never blindly by cell;
  - pad to a square (~1.05-1.1×), resize with LANCZOS to 128 px (items/icons) or 256 px (1×1 building);
  - composite a preview strip on a mid-dark background and look at it before shipping.
- Sizes: gizmo/UI icons 128×128; items 128×128 (drawn in one cell); 1×1 building 256×256 with `drawSize ~1.25`;
  build-menu icon via `uiIconPath` with the content at ~74%.
- Stackable items need `_a/_b/_c` stack art. Don't fake it by pasting copies of the single sprite: overlapping
  trays look like the clutter players complain about. Generate a neat "rack of 3" and "rack of 6".
- Small symbols/glyphs on labels are fine; reject any real text or letters.
- Verify in game: screenshot the item on the floor, in storage and in the build menu.
