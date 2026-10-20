#!/bin/bash
# RimWorld 1.7 harness: Validator/run17.sh (net10 + Harmony 3, game DLLs from Dependencies/1.7, mod from RimThreaded/1.7).
set -e
export DOTNET_ROOT=/opt/dotnet10 PATH=/opt/dotnet10:$PATH DOTNET_EnableWriteXorExecute=0 RW_VERSION=1.7
ROOT=$(cd "$(dirname "$0")/.." && pwd)
mkdir -p "$ROOT/Validator/run17"
cp "$ROOT"/Dependencies/1.7/*.dll "$ROOT/Validator/run17/"
cp "$ROOT"/RimThreaded/1.7/Assemblies/* "$ROOT/Validator/run17/"
(cd "$ROOT/Validator17" && dotnet build -c Release 2>&1 | grep -E " error " || true)
cd "$ROOT/Validator17/bin/Release/net10.0"
dotnet Validator.dll "$ROOT/Validator/run17" "$@" > /tmp/validator17_out.txt 2>&1 || true
grep -v "^hook\|^phase\|^start" /tmp/validator17_out.txt
python3 "$ROOT/Validator/scan_dispatch.py" "$ROOT/Source"
