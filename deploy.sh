#!/usr/bin/env bash
# Build the assembly and copy the mod into the RimWorld Mods folder.
# C: is full on this machine, so all dotnet caches/temp go to D:.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
GAME_MODS="/d/Games/Rimworld.v1.6.4850/game/Mods"
DEST="$GAME_MODS/AnimalGeneticsBreeding"

export NUGET_PACKAGES="D:\\FunNoTrabajo\\_devtmp\\nuget"
export DOTNET_CLI_HOME="D:\\FunNoTrabajo\\_devtmp\\home"
export TMP="D:\\FunNoTrabajo\\_devtmp\\tmp" TEMP="D:\\FunNoTrabajo\\_devtmp\\tmp"
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

dotnet build "$ROOT/Source/AnimalGeneticsBreeding/AnimalGeneticsBreeding.csproj" -c Release -nologo -v q

rm -rf "$DEST"
mkdir -p "$DEST"
cp -r "$ROOT/About" "$ROOT/1.6" "$ROOT/Languages" "$ROOT/Textures" "$DEST/"
echo "Deployed to $DEST"
