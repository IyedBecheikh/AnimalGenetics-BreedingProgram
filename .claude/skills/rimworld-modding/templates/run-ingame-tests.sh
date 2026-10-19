#!/usr/bin/env bash
# Template runner: build, install the harness, start RimWorld in an ISOLATED save-data folder with a minimal
# mod list, run the harness, print results. Never touches the user's real config/saves/modlist.
# Flags: --perf (benchmarks), --opt (optional mods), --ui (UI mods). Edit the paths and packageIds.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GAME="/c/Path/To/RimWorld"
DEV="/d/dev"                     # any drive with free space (logs, data folder, caches)
DATA="$DEV/rwdata"
HARNESS_PKG="me.mymodtest"
MOD_PKG="me.mymod"

export NUGET_PACKAGES="$(cygpath -w "$DEV/nuget")" DOTNET_CLI_HOME="$(cygpath -w "$DEV/home")"
export TMP="$(cygpath -w "$DEV/tmp")" TEMP="$(cygpath -w "$DEV/tmp")" DOTNET_NOLOGO=1

dotnet build "$ROOT/Source/MyMod/MyMod.csproj" -c Release -nologo -v q
dotnet build "$ROOT/Tests/MyModTest/Src/MyModTest.csproj" -c Release -nologo -v q

rm -rf "$GAME/Mods/MyMod" "$GAME/Mods/MyModTest"
mkdir -p "$GAME/Mods/MyMod" "$GAME/Mods/MyModTest"
cp -r "$ROOT/About" "$ROOT/1.6" "$ROOT/Languages" "$ROOT/Textures" "$GAME/Mods/MyMod/"
cp -r "$ROOT/Tests/MyModTest/About" "$ROOT/Tests/MyModTest/Assemblies" "$GAME/Mods/MyModTest/"

rm -rf "$DATA"; mkdir -p "$DATA/Config"
cat > "$DATA/Config/ModsConfig.xml" <<XML
<?xml version="1.0" encoding="utf-8"?>
<ModsConfigData>
  <version>1.6.0</version>
  <activeMods>
    <li>brrainz.harmony</li><li>ludeon.rimworld</li>UI_MODSOPT_MODS<li>${MOD_PKG,,}</li><li>${HARNESS_PKG,,}</li>
  </activeMods>
</ModsConfigData>
XML
for arg in "$@"; do [ "$arg" = "--perf" ] && touch "$DATA/harness_perf.flag"; done
if [[ " $* " == *" --opt "* ]]; then sed -i 's#OPT_MODS#<li>some.optional.mod</li>#' "$DATA/Config/ModsConfig.xml"; else sed -i 's#OPT_MODS##' "$DATA/Config/ModsConfig.xml"; fi
if [[ " $* " == *" --ui "* ]]; then sed -i 's#UI_MODS#<li>jaxe.rimhud</li><li>mehni.numbers</li>#' "$DATA/Config/ModsConfig.xml"; else sed -i 's#UI_MODS##' "$DATA/Config/ModsConfig.xml"; fi

(cd "$GAME" && ./RimWorldWin64.exe -savedatafolder="$(cygpath -w "$DATA")" -logfile "$(cygpath -w "$DEV/player.log")" \
  -quicktest -screen-width 1600 -screen-height 900 -popupwindow) || true
rm -rf "$GAME/Mods/MyModTest"
cat "$DATA/harness_results.txt"
grep -q "DONE fails=0" "$DATA/harness_results.txt"
