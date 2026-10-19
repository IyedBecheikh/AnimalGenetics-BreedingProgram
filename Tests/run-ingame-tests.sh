#!/usr/bin/env bash
# In-game integration test (~40 s): isolated save-data folder, minimal mod list
# (Harmony, Core, Animal Genetics, this mod, test harness). Never touches your real config.
# Output: $DATA/agbtest_results.txt and screenshots in $DATA/shots.
# Pass --perf to also run the pawn-table benchmark (adds ~1 min).
# Pass --ui to also load RimHUD, Dubs Mint Menus, Numbers, Animal Tab and Scrollable Inspect Panes.
# Pass --vge to also load Vanilla Expanded Framework + Vanilla Genetics Expanded (tests the VGE bridge).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GAME="/d/Games/Rimworld.v1.6.4850/game"
DEV="/d/FunNoTrabajo/_devtmp"
DATA="$DEV/rwdata"
export NUGET_PACKAGES="D:\FunNoTrabajo\_devtmp\nuget" DOTNET_CLI_HOME="D:\FunNoTrabajo\_devtmp\home"
export TMP="D:\FunNoTrabajo\_devtmp\tmp" TEMP="D:\FunNoTrabajo\_devtmp\tmp" DOTNET_NOLOGO=1

bash "$ROOT/deploy.sh"
mkdir -p "$ROOT/Tests/AGBTest/Assemblies"
dotnet build "$ROOT/Tests/AGBTest/Src/AGBTest.csproj" -c Release -nologo -v q -p:OutputPath="$(cygpath -w "$ROOT/Tests/AGBTest/Assemblies")\\"
rm -rf "$GAME/Mods/AGBTest"; mkdir -p "$GAME/Mods/AGBTest"
cp -r "$ROOT/Tests/AGBTest/About" "$ROOT/Tests/AGBTest/Assemblies" "$GAME/Mods/AGBTest/"

rm -rf "$DATA"; mkdir -p "$DATA/Config"
cat > "$DATA/Config/ModsConfig.xml" <<'XML'
<?xml version="1.0" encoding="utf-8"?>
<ModsConfigData>
  <version>1.6.4850 rev645</version>
  <activeMods>
    <li>brrainz.harmony</li><li>ludeon.rimworld</li>UI_MODSVGE_DEPS<li>mlie.animalgenetics</li>VGE_MOD
    <li>iyedm.animalgeneticsbreeding</li><li>iyedm.agbtest</li>
  </activeMods>
</ModsConfigData>
XML
for arg in "$@"; do
  case "$arg" in
    --perf) touch "$DATA/agbtest_perf.flag" ;;
  esac
done
if [[ " $* " == *" --ui "* ]]; then
  sed -i 's#UI_MODS#<li>jaxe.rimhud</li><li>dubwise.dubsmintmenus</li><li>mehni.numbers</li><li>fluffy.animaltab</li><li>kathanon.scrollableinspectpanes</li>#' "$DATA/Config/ModsConfig.xml"
else
  sed -i 's#UI_MODS##' "$DATA/Config/ModsConfig.xml"
fi
if [[ " $* " == *" --vge "* ]]; then
  sed -i 's#VGE_DEPS#<li>oskarpotocki.vanillafactionsexpanded.core</li>#; s#VGE_MOD#<li>vanillaexpanded.vgeneticse</li>#' "$DATA/Config/ModsConfig.xml"
else
  sed -i 's#VGE_DEPS##; s#VGE_MOD##' "$DATA/Config/ModsConfig.xml"
fi
(cd "$GAME" && ./RimWorldWin64.exe -savedatafolder="$(cygpath -w "$DATA")" -logfile "$(cygpath -w "$DEV/player.log")" \
  -quicktest -screen-width 1600 -screen-height 900 -popupwindow) || true
rm -rf "$GAME/Mods/AGBTest"
cat "$DATA/agbtest_results.txt"
grep -q "DONE fails=0" "$DATA/agbtest_results.txt"
