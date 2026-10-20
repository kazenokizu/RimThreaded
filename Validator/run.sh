#!/bin/bash
# Usage: Validator/run.sh   (from repo root). Stages game DLLs + built mod into Validator/run and runs the harness.
set -e
export PATH=$PATH:/opt/dotnet DOTNET_ROLL_FORWARD=Major DOTNET_EnableWriteXorExecute=0
ROOT=$(cd "$(dirname "$0")/.." && pwd)
mkdir -p "$ROOT/Validator/run"
cp "$ROOT"/Dependencies/1.6/*.dll "$ROOT/Validator/run/"
cp "$ROOT"/RimThreaded/1.6/Assemblies/* "$ROOT/Validator/run/"
(cd "$ROOT/Validator" && dotnet build -c Release 2>&1 | grep -E " error " || true)
cd "$ROOT/Validator/bin/Release/net8.0"
dotnet Validator.dll "$ROOT/Validator/run" > /tmp/validator_out.txt 2>&1 || true
grep -v "^hook\|^phase\|^start" /tmp/validator_out.txt
